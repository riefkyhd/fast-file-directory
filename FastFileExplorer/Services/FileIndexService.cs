using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using FastFileExplorer.Models;
using Microsoft.Data.Sqlite;

namespace FastFileExplorer.Services;

public sealed class FileIndexService : IDisposable
{
    internal static readonly HashSet<string> LowLevelRootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "Recovery",
        "PerfLogs",
        "$Recycle.Bin",
        "System Volume Information",
        "MSOCache"
    };

    private const int MaxCacheItems = 10_000_000;
    private const int MaxFlushUpsertsPerCycle = 4_000;
    private const int MaxFlushDeletesPerCycle = 4_000;
    private const int FastSearchCandidateMultiplier = 4;
    private const int FullSearchCandidateMultiplier = 12;
    private const int FastSearchMaxCandidates = 400;
    private const int FullSearchMaxCandidates = 4000;
    private const int ShadowBuildBatchSize = 5000;
    private const int ShadowWriteBatchSize = 20000;
    private const int ShadowQueueCapacity = 120000;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _watcherLock = new();
    private readonly object _storeLock = new();
    private readonly ConcurrentDictionary<string, IndexedItem> _pendingUpserts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly InMemorySearchIndex _memoryIndex = new();

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _persistCts;
    private Task? _persistTask;
    private volatile bool _isIndexing;
    private bool _includeLowLevelContent;
    private SqliteIndexStore? _store;
    private string? _storePath;
    private int _itemCount;
    private long _lastCountRefreshUtcTicks;
    private long _lastIndexChangedUtcTicks;
    private int _pendingIndexChanged;
    private int _shutdownStarted;

    public bool IsIndexing => _isIndexing;
    public int ItemCount => Volatile.Read(ref _itemCount);
    public int WatchedRootsCount
    {
        get
        {
            lock (_watcherLock)
            {
                return _watchers.Count;
            }
        }
    }

    public event Action? IndexChanged;
    public event Action<string>? StatusChanged;

    public string? LastCacheLoadMessage { get; private set; }
    public string? LastCacheSaveMessage { get; private set; }
    public string CurrentIndexState { get; private set; } = SqliteIndexStore.IndexStateUnknown;

    public async Task<bool> LoadCacheAsync(string cachePath, bool includeLowLevelContent)
    {
        try
        {
            await Task.Run(() => PromoteCompleteBuildIfAvailable(cachePath));
            await Task.Run(() => EnsureStore(cachePath));
            _includeLowLevelContent = includeLowLevelContent;

            var metadata = _store!.TryReadMetadata();
            if (metadata is null || metadata.ItemCount == 0)
            {
                var imported = await TryImportLegacyJsonAsync(cachePath, includeLowLevelContent);
                if (!imported)
                {
                    return false;
                }

                metadata = _store.TryReadMetadata();
                if (metadata is null || metadata.ItemCount == 0)
                {
                    return false;
                }
            }

            if (metadata.ItemCount > MaxCacheItems)
            {
                LastCacheLoadMessage = "Cache item count too large. Building fresh index.";
                PublishStatus(LastCacheLoadMessage);
                return false;
            }

            if (metadata.IncludeLowLevelContent != includeLowLevelContent)
            {
                LastCacheLoadMessage = "Cache mode changed. Rebuilding index.";
                PublishStatus(LastCacheLoadMessage);
                return false;
            }

            var items = await Task.Run(() => _store.LoadAllItems());
            _memoryIndex.ReplaceAll(items);
            Interlocked.Exchange(ref _itemCount, _memoryIndex.Count);
            Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);
            CurrentIndexState = metadata.IndexState;
            LastCacheLoadMessage = metadata.IndexState == SqliteIndexStore.IndexStateIncomplete
                ? $"Loaded cache: {ItemCount:N0} items (recovering)"
                : $"Loaded cache: {ItemCount:N0} items";
            PublishStatus(LastCacheLoadMessage);
            NotifyIndexChanged(force: true);
            return true;
        }
        catch
        {
            LastCacheLoadMessage = "Cache load failed. Building fresh index.";
            PublishStatus(LastCacheLoadMessage);
            return false;
        }
    }

    public async Task SaveCacheAsync(string cachePath)
    {
        try
        {
            EnsureStore(cachePath);
            await FlushPendingChangesAsync(refreshItemCount: true, flushAll: true);
            if (_store is not null)
            {
                _store.SetItemCountCheckpoint(ItemCount);
                if (_isIndexing)
                {
                    _store.SetIndexState(SqliteIndexStore.IndexStateIncomplete);
                    CurrentIndexState = SqliteIndexStore.IndexStateIncomplete;
                }
                else
                {
                    _store.MarkFullScanCompleted(ItemCount);
                    CurrentIndexState = SqliteIndexStore.IndexStateComplete;
                }
            }
            LastCacheSaveMessage = $"Cache synced: {ItemCount:N0} items";
            PublishStatus(LastCacheSaveMessage);
        }
        catch
        {
            LastCacheSaveMessage = "Cache save failed.";
            PublishStatus(LastCacheSaveMessage);
        }
    }

    public async Task StartOrRebuildIndexAsync(IReadOnlyList<string> roots, bool includeLowLevelContent)
    {
        await RunIndexAsync(roots, includeLowLevelContent, resetExistingIndex: true);
    }

    public async Task ContinueIndexAsync(IReadOnlyList<string> roots, bool includeLowLevelContent)
    {
        await RunIndexAsync(roots, includeLowLevelContent, resetExistingIndex: false);
    }

    private async Task RunIndexAsync(IReadOnlyList<string> roots, bool includeLowLevelContent, bool resetExistingIndex)
    {
        await _rebuildGate.WaitAsync();
        try
        {
            CancelIndexing();
            EnsureStore(_storePath ?? SettingsService.GetDefaultCachePath());

            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            _includeLowLevelContent = includeLowLevelContent;

            _isIndexing = true;
            if (resetExistingIndex)
            {
                PublishStatus("Reindexing in background (active index stays searchable)...");
                await BuildShadowIndexAndSwapAsync(roots, includeLowLevelContent, token);
            }
            else
            {
                PublishStatus("Continuing index from cache...");
                _store!.SetIndexState(SqliteIndexStore.IndexStateIncomplete);
                CurrentIndexState = SqliteIndexStore.IndexStateIncomplete;
                await Task.Run(() => BuildIndexFromRoots(roots, token), token);
                await FlushPendingChangesAsync(refreshItemCount: true, flushAll: true);
                _store.MarkFullScanCompleted(ItemCount);
                CurrentIndexState = SqliteIndexStore.IndexStateComplete;
            }
            ConfigureWatchers(roots);
        }
        catch (OperationCanceledException)
        {
            PublishStatus("Reindex canceled");
            return;
        }
        finally
        {
            _isIndexing = false;
            _rebuildGate.Release();
        }

        PublishStatus(resetExistingIndex
            ? $"Index ready: {ItemCount:N0} items"
            : $"Index resumed: {ItemCount:N0} items");
        NotifyIndexChanged(force: true);
    }

    private async Task BuildShadowIndexAndSwapAsync(
        IReadOnlyList<string> roots,
        bool includeLowLevelContent,
        CancellationToken token)
    {
        var activePath = _storePath ?? SettingsService.GetDefaultCachePath();
        var buildPath = SettingsService.GetBuildCachePath(activePath);
        var buildStore = new SqliteIndexStore(buildPath);
        buildStore.EnsureInitialized();
        buildStore.ResetIndex(includeLowLevelContent);
        buildStore.SetIndexState(SqliteIndexStore.IndexStateIncomplete);

        var scanned = await Task.Run(() => BuildIndexIntoStore(roots, includeLowLevelContent, token, buildStore), token);
        token.ThrowIfCancellationRequested();

        buildStore.MarkFullScanCompleted(scanned);

        await FlushPendingChangesAsync(refreshItemCount: true, flushAll: true);
        ReplaceActiveStoreWithBuildStore(activePath, buildPath);

        EnsureStore(activePath);
        var items = _store!.LoadAllItems();
        _memoryIndex.ReplaceAll(items);
        Interlocked.Exchange(ref _itemCount, _memoryIndex.Count);
        Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);
        CurrentIndexState = SqliteIndexStore.IndexStateComplete;
    }

    private int BuildIndexIntoStore(
        IReadOnlyList<string> roots,
        bool includeLowLevelContent,
        CancellationToken token,
        SqliteIndexStore destinationStore)
    {
        var orderedRoots = GetEffectiveRoots(OrderRoots(roots));
        var scanned = 0;
        var errors = new ConcurrentQueue<string>();
        var writeErrors = new ConcurrentQueue<Exception>();
        using var queue = new BlockingCollection<IndexedItem>(ShadowQueueCapacity);

        var writerTask = Task.Run(() =>
        {
            var writeBatch = new List<IndexedItem>(ShadowWriteBatchSize);
            try
            {
                foreach (var item in queue.GetConsumingEnumerable(token))
                {
                    writeBatch.Add(item);
                    if (writeBatch.Count < ShadowWriteBatchSize)
                    {
                        continue;
                    }

                    destinationStore.ApplyChanges(writeBatch, Array.Empty<string>(), includeLowLevelContent);
                    writeBatch.Clear();
                }

                if (writeBatch.Count > 0)
                {
                    destinationStore.ApplyChanges(writeBatch, Array.Empty<string>(), includeLowLevelContent);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
            catch (Exception ex)
            {
                writeErrors.Enqueue(ex);
            }
        }, token);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(2, Math.Min(Math.Max(2, Environment.ProcessorCount), orderedRoots.Count == 0 ? 2 : orderedRoots.Count))
        };

        Parallel.ForEach(orderedRoots, parallelOptions, root =>
        {
            var localBatch = new List<IndexedItem>(ShadowBuildBatchSize);
            void QueueLocalBatch()
            {
                if (localBatch.Count == 0)
                {
                    return;
                }

                foreach (var item in localBatch)
                {
                    queue.Add(item, parallelOptions.CancellationToken);
                }

                localBatch.Clear();
            }

            try
            {
                parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                PublishStatus($"Indexing {root}...");
                TraverseRoot(root, parallelOptions.CancellationToken, includeLowLevelContent, onDirectory: dir =>
                {
                    localBatch.Add(ToIndexedDirectory(dir));
                    var current = Interlocked.Increment(ref scanned);
                    if (localBatch.Count >= ShadowBuildBatchSize)
                    {
                        QueueLocalBatch();
                    }

                    if (current % 8000 == 0)
                    {
                        PublishStatus($"Indexing... scanned {current:N0} items");
                        NotifyIndexChanged();
                    }
                }, onFile: file =>
                {
                    localBatch.Add(ToIndexedFile(file));
                    var current = Interlocked.Increment(ref scanned);
                    if (localBatch.Count >= ShadowBuildBatchSize)
                    {
                        QueueLocalBatch();
                    }

                    if (current % 8000 == 0)
                    {
                        PublishStatus($"Indexing... scanned {current:N0} items");
                        NotifyIndexChanged();
                    }
                });

                QueueLocalBatch();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Enqueue($"{root}: {ex.Message}");
            }
        });

        queue.CompleteAdding();
        writerTask.GetAwaiter().GetResult();

        if (!writeErrors.IsEmpty)
        {
            throw new InvalidOperationException($"Shadow index write failed: {writeErrors.First().Message}");
        }

        if (!errors.IsEmpty)
        {
            var sample = string.Join(" | ", errors.Take(3));
            PublishStatus($"Indexing completed with {errors.Count} root error(s): {sample}");
        }

        return scanned;
    }

    private void ReplaceActiveStoreWithBuildStore(string activePath, string buildPath)
    {
        lock (_storeLock)
        {
            _persistCts?.Cancel();
            try
            {
                _ = _persistTask?.Wait(1500);
            }
            catch
            {
                // Ignore persistence loop shutdown races during swap.
            }

            var previousPath = SettingsService.GetPreviousCachePath(activePath);
            var activeDirectory = Path.GetDirectoryName(activePath);
            if (!string.IsNullOrWhiteSpace(activeDirectory))
            {
                Directory.CreateDirectory(activeDirectory);
            }

            SqliteConnection.ClearAllPools();

            try
            {
                if (File.Exists(previousPath))
                {
                    File.Delete(previousPath);
                }
            }
            catch
            {
                // Ignore previous backup cleanup failures.
            }

            if (File.Exists(activePath))
            {
                File.Replace(buildPath, activePath, previousPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(buildPath, activePath, overwrite: true);
            }

            _store = new SqliteIndexStore(activePath);
            _store.EnsureInitialized();
            _storePath = activePath;
            StartPersistenceLoop();
        }
    }

    private void PromoteCompleteBuildIfAvailable(string activePath)
    {
        var buildPath = SettingsService.GetBuildCachePath(activePath);
        if (!File.Exists(buildPath))
        {
            return;
        }

        var buildStore = new SqliteIndexStore(buildPath);
        buildStore.EnsureInitialized();
        var buildMetadata = buildStore.TryReadMetadata();
        if (buildMetadata is null || buildMetadata.IndexState != SqliteIndexStore.IndexStateComplete)
        {
            return;
        }

        var activeStore = new SqliteIndexStore(activePath);
        if (activeStore.Exists())
        {
            activeStore.EnsureInitialized();
            var activeMetadata = activeStore.TryReadMetadata();
            if (activeMetadata is not null &&
                activeMetadata.IndexState == SqliteIndexStore.IndexStateComplete &&
                activeMetadata.ItemCount >= buildMetadata.ItemCount)
            {
                return;
            }
        }

        ReplaceActiveStoreWithBuildStore(activePath, buildPath);
    }

    public void StartWatchersOnly(IReadOnlyList<string> roots, bool includeLowLevelContent)
    {
        _includeLowLevelContent = includeLowLevelContent;
        ConfigureWatchers(roots);
    }

    public IReadOnlyList<IndexedItem> SearchFastCandidates(string query, SearchOptions options, int limit)
    {
        return SearchCore(query, options, limit, SearchMode.FastCandidates);
    }

    public IReadOnlyList<IndexedItem> SearchFullRelevance(string query, SearchOptions options, int limit)
    {
        return SearchCore(query, options, limit, SearchMode.FullRelevance);
    }

    public IReadOnlyList<IndexedItem> Search(string query, SearchOptions options, int limit, bool preferFast = false)
    {
        return preferFast
            ? SearchFastCandidates(query, options, limit)
            : SearchFullRelevance(query, options, limit);
    }

    private IReadOnlyList<IndexedItem> SearchCore(string query, SearchOptions options, int limit, SearchMode mode)
    {
        if (limit <= 0)
        {
            return [];
        }

        var normalized = NormalizeText(query);
        var terms = SplitTerms(normalized);
        if (terms.Length == 0)
        {
            return [];
        }

        var candidateLimit = mode == SearchMode.FastCandidates
            ? Math.Min(FastSearchMaxCandidates, limit)
            : Math.Min(FullSearchMaxCandidates, Math.Max(limit, limit * FullSearchCandidateMultiplier));

        return mode == SearchMode.FastCandidates
            ? _memoryIndex.SearchFastCandidates(normalized, options, candidateLimit).Take(limit).ToList()
            : _memoryIndex.SearchFullRelevance(normalized, options, candidateLimit).Take(limit).ToList();
    }

    public void CancelIndexing()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
        _isIndexing = false;
    }

    private void EnsureStore(string cachePath)
    {
        lock (_storeLock)
        {
            if (_store is not null && string.Equals(_storePath, cachePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _storePath = cachePath;
            _store = new SqliteIndexStore(cachePath);
            _store.EnsureInitialized();
            StartPersistenceLoop();
        }
    }

    private void StartPersistenceLoop()
    {
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = new CancellationTokenSource();
        var token = _persistCts.Token;

        _persistTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(120), token);
                    await FlushPendingChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Keep loop alive for transient persistence failures.
                }
            }
        }, token);
    }

    private async Task FlushPendingChangesAsync(bool refreshItemCount = false, bool flushAll = false)
    {
        if (_store is null)
        {
            return;
        }

        await _flushGate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (_pendingUpserts.IsEmpty && _pendingDeletes.IsEmpty)
                {
                    if (refreshItemCount)
                    {
                        Interlocked.Exchange(ref _itemCount, _memoryIndex.Count);
                        Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);
                    }
                    return;
                }

                do
                {
                    var upsertMap = new Dictionary<string, IndexedItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in _pendingUpserts)
                    {
                        if (upsertMap.Count >= MaxFlushUpsertsPerCycle && !flushAll)
                        {
                            break;
                        }

                        if (_pendingUpserts.TryRemove(pair.Key, out var item))
                        {
                            upsertMap[pair.Key] = item;
                        }
                    }

                    var deleteSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in _pendingDeletes)
                    {
                        if (deleteSet.Count >= MaxFlushDeletesPerCycle && !flushAll)
                        {
                            break;
                        }

                        if (_pendingDeletes.TryRemove(pair.Key, out _))
                        {
                            deleteSet.Add(pair.Key);
                        }
                    }

                    foreach (var deletedPath in deleteSet)
                    {
                        upsertMap.Remove(deletedPath);
                    }

                    if (upsertMap.Count == 0 && deleteSet.Count == 0)
                    {
                        break;
                    }

                    try
                    {
                        _store.ApplyChanges(upsertMap.Values.ToList(), deleteSet.ToList(), _includeLowLevelContent);
                    }
                    catch
                    {
                        foreach (var upsert in upsertMap.Values)
                        {
                            _pendingUpserts[upsert.FullPath] = upsert;
                        }

                        foreach (var deletedPath in deleteSet)
                        {
                            _pendingDeletes[deletedPath] = 0;
                        }

                        throw;
                    }
                } while (flushAll && (!_pendingUpserts.IsEmpty || !_pendingDeletes.IsEmpty));

                var nowTicks = DateTime.UtcNow.Ticks;
                var needsRefresh = refreshItemCount ||
                    nowTicks - Interlocked.Read(ref _lastCountRefreshUtcTicks) >= TimeSpan.FromSeconds(5).Ticks;
                if (!needsRefresh)
                {
                    return;
                }

                Interlocked.Exchange(ref _itemCount, _memoryIndex.Count);
                Interlocked.Exchange(ref _lastCountRefreshUtcTicks, nowTicks);
            });
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task<bool> TryImportLegacyJsonAsync(string cachePath, bool includeLowLevelContent)
    {
        try
        {
            var cacheDirectory = Path.GetDirectoryName(cachePath) ?? string.Empty;
            var localLegacy = SettingsService.GetDefaultLegacyJsonPath();
            var candidatePaths = new[]
            {
                localLegacy,
                Path.Combine(cacheDirectory, "index-cache-v1.json"),
                cachePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? cachePath : string.Empty
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

            var legacyPath = candidatePaths.FirstOrDefault(File.Exists);
            if (legacyPath is null)
            {
                return false;
            }

            await using var stream = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var legacy = await JsonSerializer.DeserializeAsync<LegacyIndexCache>(stream);
            if (legacy is null || legacy.Version != 1 || legacy.Items.Count == 0)
            {
                return false;
            }

            if (legacy.IncludeLowLevelContent != includeLowLevelContent)
            {
                return false;
            }

            var importedItems = legacy.Items.Select(record => new IndexedItem
            {
                FullPath = record.FullPath,
                Name = record.Name,
                Directory = record.Directory,
                Extension = record.Extension,
                LastWriteTimeUtc = record.LastWriteTimeUtc,
                SizeBytes = record.SizeBytes,
                Kind = record.Kind,
                NormalizedName = NormalizeText(record.Name)
            }).ToList();

            _store!.ResetIndex(includeLowLevelContent);
            const int batchSize = 10_000;
            for (var i = 0; i < importedItems.Count; i += batchSize)
            {
                var batch = importedItems.Skip(i).Take(batchSize).ToList();
                _store.ApplyChanges(batch, Array.Empty<string>(), includeLowLevelContent);
            }
            _store.MarkFullScanCompleted(importedItems.Count);
            _memoryIndex.ReplaceAll(importedItems);
            CurrentIndexState = SqliteIndexStore.IndexStateComplete;

            Interlocked.Exchange(ref _itemCount, importedItems.Count);
            Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);
            LastCacheLoadMessage = $"Imported legacy cache: {ItemCount:N0} items";
            PublishStatus(LastCacheLoadMessage);
            NotifyIndexChanged(force: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void BuildIndexFromRoots(IReadOnlyList<string> roots, CancellationToken token)
    {
        var orderedRoots = GetEffectiveRoots(OrderRoots(roots));
        var scanned = 0;
        var errors = new ConcurrentQueue<string>();
        try
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(2, Math.Min(Math.Max(2, Environment.ProcessorCount), orderedRoots.Count == 0 ? 2 : orderedRoots.Count))
            };

            Parallel.ForEach(orderedRoots, parallelOptions, root =>
            {
                try
                {
                    parallelOptions.CancellationToken.ThrowIfCancellationRequested();
                    PublishStatus($"Indexing {root}...");

                    TraverseRoot(root, parallelOptions.CancellationToken, _includeLowLevelContent, onDirectory: dir =>
                    {
                        AddOrUpdateDirectory(dir);
                        var current = Interlocked.Increment(ref scanned);
                        if (current % 8000 == 0)
                        {
                            PublishStatus($"Indexing... scanned {current:N0} items");
                            if (current % 24000 == 0)
                            {
                                _ = SafeFlushCheckpointAsync();
                            }
                            NotifyIndexChanged();
                        }
                    }, onFile: file =>
                    {
                        AddOrUpdateFile(file);
                        var current = Interlocked.Increment(ref scanned);
                        if (current % 8000 == 0)
                        {
                            PublishStatus($"Indexing... scanned {current:N0} items");
                            if (current % 24000 == 0)
                            {
                                _ = SafeFlushCheckpointAsync();
                            }
                            NotifyIndexChanged();
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Enqueue($"{root}: {ex.Message}");
                }
            });
        }
        catch (OperationCanceledException)
        {
            PublishStatus("Index canceled");
            return;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            PublishStatus("Index canceled");
            return;
        }
        catch (AggregateException ex)
        {
            errors.Enqueue($"parallel indexing: {ex.Flatten().Message}");
        }

        if (!errors.IsEmpty)
        {
            var sample = string.Join(" | ", errors.Take(3));
            PublishStatus($"Indexing completed with {errors.Count} root error(s): {sample}");
        }
    }

    private async Task SafeFlushCheckpointAsync()
    {
        try
        {
            await FlushPendingChangesAsync(refreshItemCount: true);
        }
        catch
        {
            // Ignore transient flush failures; periodic loop and final flush recover.
        }
    }

    private static List<string> GetEffectiveRoots(IReadOnlyList<string> orderedRoots)
    {
        var driveRoots = orderedRoots
            .Where(IsDriveRoot)
            .Select(root => NormalizeDriveRoot(root))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effectiveRoots = new List<string>(orderedRoots.Count);
        foreach (var root in orderedRoots)
        {
            var normalizedDriveRoot = NormalizeDriveRoot(root);
            if (!IsDriveRoot(root) &&
                !string.IsNullOrWhiteSpace(normalizedDriveRoot) &&
                driveRoots.Contains(normalizedDriveRoot))
            {
                continue;
            }

            effectiveRoots.Add(root);
        }

        return effectiveRoots;
    }

    private static List<string> OrderRoots(IReadOnlyList<string> roots)
    {
        var distinct = roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        distinct.Sort((a, b) =>
        {
            var aIsDrive = IsDriveRoot(a);
            var bIsDrive = IsDriveRoot(b);
            if (aIsDrive && !bIsDrive)
            {
                return -1;
            }
            if (!aIsDrive && bIsDrive)
            {
                return 1;
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });

        return distinct;
    }

    private static bool IsDriveRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            return string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeDriveRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            return root.TrimEnd('\\') + "\\";
        }
        catch
        {
            return null;
        }
    }

    private static void TraverseRoot(string root, CancellationToken token, bool includeLowLevelContent, Action<DirectoryInfo> onDirectory, Action<FileInfo> onFile)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();
            var currentInfo = new DirectoryInfo(current);
            if (ShouldSkipDirectory(currentInfo, root, includeLowLevelContent))
            {
                continue;
            }

            try
            {
                onDirectory(currentInfo);
            }
            catch
            {
                // Ignore inaccessible directories.
            }

            try
            {
                foreach (var childDir in Directory.EnumerateDirectories(current))
                {
                    var childInfo = new DirectoryInfo(childDir);
                    if (!ShouldSkipDirectory(childInfo, root, includeLowLevelContent))
                    {
                        pending.Push(childDir);
                    }
                }
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(current))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if (!ShouldSkipFile(fileInfo, includeLowLevelContent))
                        {
                            onFile(fileInfo);
                        }
                    }
                    catch
                    {
                        // Ignore transient files.
                    }
                }
            }
            catch
            {
                // Ignore inaccessible directories.
            }
        }
    }

    private void ConfigureWatchers(IReadOnlyList<string> roots)
    {
        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // Ignore shutdown errors.
                }
            }

            _watchers.Clear();
        }

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            // Drive-root recursive watchers are noisy and unreliable at this scale.
            // Indexing still scans drives; live watching is focused on user-level roots.
            if (IsDriveRoot(root))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                Filter = "*"
            };

            watcher.Created += (_, args) => TryRefreshFromPath(args.FullPath);
            watcher.Changed += (_, args) => TryRefreshFromPath(args.FullPath);
            watcher.Renamed += (_, args) =>
            {
                RemoveItem(args.OldFullPath);
                TryRefreshFromPath(args.FullPath);
            };
            watcher.Deleted += (_, args) => RemoveItem(args.FullPath);
            watcher.Error += (_, _) =>
            {
                // Keep UI stable; watcher faults are non-fatal and periodic indexing preserves correctness.
            };
            watcher.EnableRaisingEvents = true;

            lock (_watcherLock)
            {
                _watchers.Add(watcher);
            }
        }
    }

    private void TryRefreshFromPath(string fullPath)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                AddOrUpdateDirectory(new DirectoryInfo(fullPath));
                NotifyIndexChanged();
                return;
            }

            if (File.Exists(fullPath))
            {
                AddOrUpdateFile(new FileInfo(fullPath));
                NotifyIndexChanged();
                return;
            }

            RemoveItem(fullPath);
        }
        catch
        {
            // Ignore races.
        }
    }

    private void AddOrUpdateDirectory(DirectoryInfo info)
    {
        if (ShouldSkipDirectory(info, info.Root.FullName, _includeLowLevelContent))
        {
            RemoveItem(info.FullName);
            return;
        }

        var item = ToIndexedDirectory(info);
        AddOrReplace(item);
    }

    private void AddOrUpdateFile(FileInfo info)
    {
        if (ShouldSkipFile(info, _includeLowLevelContent))
        {
            RemoveItem(info.FullName);
            return;
        }

        var item = ToIndexedFile(info);
        AddOrReplace(item);
    }

    private static IndexedItem ToIndexedDirectory(DirectoryInfo info)
    {
        return new IndexedItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            Directory = info.Parent?.FullName ?? info.Root.FullName,
            Extension = "folder",
            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            SizeBytes = 0,
            Kind = IndexedItemKind.Folder,
            NormalizedName = NormalizeText(info.Name)
        };
    }

    private static IndexedItem ToIndexedFile(FileInfo info)
    {
        var extension = string.IsNullOrWhiteSpace(info.Extension)
            ? "(none)"
            : info.Extension.TrimStart('.').ToLowerInvariant();

        return new IndexedItem
        {
            FullPath = info.FullName,
            Name = info.Name,
            Directory = info.DirectoryName ?? string.Empty,
            Extension = extension,
            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.UtcNow,
            SizeBytes = info.Exists ? info.Length : 0,
            Kind = IndexedItemKind.File,
            NormalizedName = NormalizeText(info.Name)
        };
    }

    private void AddOrReplace(IndexedItem item)
    {
        _pendingDeletes.TryRemove(item.FullPath, out _);
        _pendingUpserts[item.FullPath] = item;
        _memoryIndex.Upsert(item);
    }

    private void RemoveItem(string fullPath)
    {
        _pendingUpserts.TryRemove(fullPath, out _);
        _pendingDeletes[fullPath] = 0;
        _memoryIndex.Remove(fullPath);
        NotifyIndexChanged();
    }

    private static string NormalizeText(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return new string(chars);
    }

    private List<IndexedItem> SearchPendingCandidates(
        string normalizedQuery,
        SearchOptions options,
        int candidateLimit)
    {
        var terms = SplitTerms(normalizedQuery);
        if (terms.Length == 0)
        {
            return [];
        }

        var bufferedMatches = new List<IndexedItem>(capacity: Math.Min(candidateLimit * 8, 5000));
        var pendingSnapshot = _pendingUpserts.Values.ToArray();
        var deletedSnapshot = _pendingDeletes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in pendingSnapshot)
        {
            if (deletedSnapshot.Contains(item.FullPath))
            {
                continue;
            }

            if (!terms.All(term => item.NormalizedName.Contains(term, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!PassesItemFilter(item, options.ItemFilter) || !PassesDateFilter(item, options.DateFilter))
            {
                continue;
            }

            bufferedMatches.Add(item);
        }

        return bufferedMatches
            .OrderBy(item => GetRelevanceRank(item, terms[0]))
            .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(candidateLimit)
            .ToList();
    }

    private static IReadOnlyList<IndexedItem> SortDeterministically(
        IEnumerable<IndexedItem> items,
        string[] terms,
        SearchMode mode,
        int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var firstTerm = terms.Length == 0 ? string.Empty : terms[0];

        if (mode == SearchMode.FastCandidates)
        {
            return items
                .OrderBy(item => GetRelevanceRank(item, firstTerm))
                .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        return items
            .OrderBy(item => GetRelevanceRank(item, firstTerm))
            .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static string[] SplitTerms(string normalizedQuery)
    {
        return normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int GetRelevanceRank(IndexedItem item, string firstTerm)
    {
        if (string.IsNullOrWhiteSpace(firstTerm))
        {
            return 0;
        }

        if (item.NormalizedName.StartsWith(firstTerm, StringComparison.Ordinal))
        {
            return 0;
        }

        var wordPrefix = " " + firstTerm;
        if (item.NormalizedName.Contains(wordPrefix, StringComparison.Ordinal))
        {
            return 1;
        }

        return 2;
    }

    private enum SearchMode
    {
        FastCandidates,
        FullRelevance
    }

    private static bool PassesItemFilter(IndexedItem item, ItemFilter filter)
    {
        return filter switch
        {
            ItemFilter.All => true,
            ItemFilter.Folder => item.Kind == IndexedItemKind.Folder,
            ItemFilter.File => item.Kind == IndexedItemKind.File,
            ItemFilter.Document => item.Kind == IndexedItemKind.File && item.Extension is "txt" or "doc" or "docx" or "pdf" or "rtf" or "ppt" or "pptx" or "xls" or "xlsx" or "csv" or "md",
            ItemFilter.Picture => item.Kind == IndexedItemKind.File && item.Extension is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tiff" or "webp" or "heic",
            ItemFilter.Video => item.Kind == IndexedItemKind.File && item.Extension is "mp4" or "mov" or "avi" or "mkv" or "wmv" or "flv" or "webm",
            _ => true
        };
    }

    private static bool PassesDateFilter(IndexedItem item, DateFilter filter)
    {
        if (filter == DateFilter.All)
        {
            return true;
        }

        var minUtc = filter switch
        {
            DateFilter.Last1Day => DateTime.UtcNow.AddDays(-1),
            DateFilter.Last7Days => DateTime.UtcNow.AddDays(-7),
            DateFilter.Last30Days => DateTime.UtcNow.AddDays(-30),
            DateFilter.Last365Days => DateTime.UtcNow.AddDays(-365),
            _ => DateTime.MinValue
        };

        if (minUtc == DateTime.MinValue)
        {
            return true;
        }

        return item.LastWriteTimeUtc >= minUtc;
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory, string root, bool includeLowLevelContent)
    {
        if (!directory.Exists)
        {
            return true;
        }

        if (includeLowLevelContent)
        {
            return false;
        }

        if (!string.Equals(directory.FullName, root, StringComparison.OrdinalIgnoreCase))
        {
            if (directory.Attributes.HasFlag(FileAttributes.Hidden) ||
                directory.Attributes.HasFlag(FileAttributes.System) ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            var name = directory.Name;
            if (name is "$Recycle.Bin" or "System Volume Information")
            {
                return true;
            }

            if (directory.Parent is not null &&
                string.Equals(directory.Parent.FullName, root, StringComparison.OrdinalIgnoreCase) &&
                LowLevelRootDirectoryNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipFile(FileInfo file, bool includeLowLevelContent)
    {
        if (!file.Exists)
        {
            return true;
        }

        if (includeLowLevelContent)
        {
            return false;
        }

        if (file.Attributes.HasFlag(FileAttributes.Hidden) ||
            file.Attributes.HasFlag(FileAttributes.System))
        {
            return true;
        }

        return false;
    }

    private void PublishStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void NotifyIndexChanged(bool force = false)
    {
        if (force)
        {
            _lastIndexChangedUtcTicks = DateTime.UtcNow.Ticks;
            IndexChanged?.Invoke();
            return;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var sinceLast = nowTicks - Interlocked.Read(ref _lastIndexChangedUtcTicks);
        if (sinceLast > TimeSpan.FromMilliseconds(450).Ticks)
        {
            Interlocked.Exchange(ref _lastIndexChangedUtcTicks, nowTicks);
            IndexChanged?.Invoke();
            return;
        }

        if (Interlocked.Exchange(ref _pendingIndexChanged, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(450);
            Interlocked.Exchange(ref _pendingIndexChanged, 0);
            Interlocked.Exchange(ref _lastIndexChangedUtcTicks, DateTime.UtcNow.Ticks);
            IndexChanged?.Invoke();
        });
    }

    public void Shutdown(bool flushPendingChanges, int flushTimeoutMs = 1500)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1)
        {
            return;
        }

        CancelIndexing();
        _persistCts?.Cancel();
        _persistCts?.Dispose();
        _persistCts = null;

        if (flushPendingChanges)
        {
            try
            {
                var flushTask = FlushPendingChangesAsync(refreshItemCount: true, flushAll: true);
                _ = flushTask.Wait(flushTimeoutMs);
            }
            catch
            {
                // Ignore final flush failures.
            }
        }

        if (_persistTask is not null)
        {
            try
            {
                _ = _persistTask.Wait(500);
            }
            catch
            {
                // Ignore persistence loop shutdown errors.
            }
        }

        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Dispose();
                }
                catch
                {
                    // Ignore shutdown errors.
                }
            }

            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        Shutdown(flushPendingChanges: true, flushTimeoutMs: 2500);
    }

    private sealed class LegacyIndexCache
    {
        public required int Version { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required bool IncludeLowLevelContent { get; init; }
        public required List<LegacyCachedItem> Items { get; init; }
    }

    private sealed class LegacyCachedItem
    {
        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required string Directory { get; init; }
        public required string Extension { get; init; }
        public required DateTime LastWriteTimeUtc { get; init; }
        public required long SizeBytes { get; init; }
        public required IndexedItemKind Kind { get; init; }
    }
}
