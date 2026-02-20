using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using FastFileExplorer.Models;

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
    private const int MaxFlushUpsertsPerCycle = 8_000;
    private const int MaxFlushDeletesPerCycle = 8_000;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _watcherLock = new();
    private readonly object _storeLock = new();
    private readonly ConcurrentDictionary<string, IndexedItem> _pendingUpserts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _flushGate = new(1, 1);

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

    public async Task<bool> LoadCacheAsync(string cachePath, bool includeLowLevelContent)
    {
        try
        {
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

            Interlocked.Exchange(ref _itemCount, metadata.ItemCount);
            Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);
            LastCacheLoadMessage = $"Loaded cache: {ItemCount:N0} items";
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
        CancelIndexing();
        EnsureStore(_storePath ?? SettingsService.GetDefaultCachePath());

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        _includeLowLevelContent = includeLowLevelContent;

        _isIndexing = true;
        PublishStatus("Reindexing...");
        Interlocked.Exchange(ref _itemCount, 0);
        Interlocked.Exchange(ref _lastCountRefreshUtcTicks, DateTime.UtcNow.Ticks);

        _pendingUpserts.Clear();
        _pendingDeletes.Clear();
        _store!.ResetIndex(includeLowLevelContent);

        ConfigureWatchers(roots);
        try
        {
            await Task.Run(() => BuildIndexFromRoots(roots, token), token);
            await FlushPendingChangesAsync(refreshItemCount: true, flushAll: true);
        }
        finally
        {
            _isIndexing = false;
        }

        PublishStatus($"Index ready: {ItemCount:N0} items");
        NotifyIndexChanged(force: true);
    }

    public void StartWatchersOnly(IReadOnlyList<string> roots, bool includeLowLevelContent)
    {
        _includeLowLevelContent = includeLowLevelContent;
        ConfigureWatchers(roots);
        PublishStatus($"Watching {WatchedRootsCount:N0} roots");
    }

    public IReadOnlyList<IndexedItem> Search(string query, SearchOptions options, int limit)
    {
        if (_store is null)
        {
            return [];
        }

        var normalized = NormalizeText(query);
        return _store.Search(normalized, options, limit);
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
                    await Task.Delay(TimeSpan.FromMilliseconds(600), token);
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
                        Interlocked.Exchange(ref _itemCount, _store.GetItemCount());
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

                Interlocked.Exchange(ref _itemCount, _store.GetItemCount());
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
        var orderedRoots = OrderRoots(roots);
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount * 2)
        };

        var scanned = 0;
        try
        {
            Parallel.ForEach(orderedRoots, options, root =>
            {
                PublishStatus($"Indexing {root}...");
                TraverseRoot(root, token, _includeLowLevelContent, onDirectory: dir =>
                {
                    AddOrUpdateDirectory(dir);
                    var current = Interlocked.Increment(ref scanned);
                    if (current % 8000 == 0)
                    {
                        Interlocked.Exchange(ref _itemCount, current);
                        PublishStatus($"Indexing {root}... {current:N0} items");
                        NotifyIndexChanged();
                    }
                }, onFile: file =>
                {
                    AddOrUpdateFile(file);
                    var current = Interlocked.Increment(ref scanned);
                    if (current % 8000 == 0)
                    {
                        Interlocked.Exchange(ref _itemCount, current);
                        PublishStatus($"Indexing {root}... {current:N0} items");
                        NotifyIndexChanged();
                    }
                });
            });
        }
        catch (OperationCanceledException)
        {
            PublishStatus("Index canceled");
        }
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

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
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
            watcher.Error += (_, _) => PublishStatus("Watcher warning: one or more directories are unavailable.");
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

        var item = new IndexedItem
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

        AddOrReplace(item);
    }

    private void AddOrUpdateFile(FileInfo info)
    {
        if (ShouldSkipFile(info, _includeLowLevelContent))
        {
            RemoveItem(info.FullName);
            return;
        }

        var extension = string.IsNullOrWhiteSpace(info.Extension)
            ? "(none)"
            : info.Extension.TrimStart('.').ToLowerInvariant();

        var item = new IndexedItem
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

        AddOrReplace(item);
    }

    private void AddOrReplace(IndexedItem item)
    {
        _pendingDeletes.TryRemove(item.FullPath, out _);
        _pendingUpserts[item.FullPath] = item;
    }

    private void RemoveItem(string fullPath)
    {
        _pendingUpserts.TryRemove(fullPath, out _);
        _pendingDeletes[fullPath] = 0;
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
