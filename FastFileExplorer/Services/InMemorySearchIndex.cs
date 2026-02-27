using System.Threading;
using FastFileExplorer.Models;

namespace FastFileExplorer.Services;

internal sealed class InMemorySearchIndex
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, IndexedItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _prefix1 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _prefix2 = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _itemsByPath.Count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public void ReplaceAll(IReadOnlyList<IndexedItem> items)
    {
        _lock.EnterWriteLock();
        try
        {
            _itemsByPath.Clear();
            _prefix1.Clear();
            _prefix2.Clear();
            foreach (var item in items)
            {
                AddInternal(item);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Upsert(IndexedItem item)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_itemsByPath.TryGetValue(item.FullPath, out var previous))
            {
                RemovePrefixes(previous);
            }

            AddInternal(item);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Remove(string fullPath)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_itemsByPath.TryGetValue(fullPath, out var previous))
            {
                return;
            }

            RemovePrefixes(previous);
            _itemsByPath.Remove(fullPath);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public IReadOnlyList<IndexedItem> SearchFastCandidates(string normalizedQuery, SearchOptions options, int limit)
    {
        return SearchInternal(normalizedQuery, options, limit, fullRelevance: false);
    }

    public IReadOnlyList<IndexedItem> SearchFullRelevance(string normalizedQuery, SearchOptions options, int limit)
    {
        return SearchInternal(normalizedQuery, options, limit, fullRelevance: true);
    }

    private IReadOnlyList<IndexedItem> SearchInternal(string normalizedQuery, SearchOptions options, int limit, bool fullRelevance)
    {
        if (limit <= 0)
        {
            return [];
        }

        var terms = SplitTerms(normalizedQuery);
        if (terms.Length == 0)
        {
            return [];
        }

        _lock.EnterReadLock();
        try
        {
            var prefixOptimized = terms.Length == 1 && terms[0].Length <= 2;
            IEnumerable<IndexedItem> candidates = ResolveCandidates(terms[0]);

            var filtered = new List<IndexedItem>(Math.Min(limit * 6, 2000));
            var fastPhaseCap = prefixOptimized ? Math.Max(limit * 2, limit) : Math.Max(limit * 8, limit);
            foreach (var item in candidates)
            {
                if (!prefixOptimized &&
                    !terms.All(term => item.NormalizedName.Contains(term, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!PassesItemFilter(item, options.ItemFilter) || !PassesDateFilter(item, options.DateFilter))
                {
                    continue;
                }

                filtered.Add(item);
                if (!fullRelevance && filtered.Count >= fastPhaseCap)
                {
                    break;
                }
            }

            var firstTerm = terms[0];
            return fullRelevance
                ? filtered
                    .OrderBy(item => GetRelevanceRank(item, firstTerm))
                    .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(item => item.LastWriteTimeUtc)
                    .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList()
                : filtered
                    .OrderBy(item => GetRelevanceRank(item, firstTerm))
                    .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Take(limit)
                    .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private IEnumerable<IndexedItem> ResolveCandidates(string firstTerm)
    {
        if (firstTerm.Length >= 2 &&
            _prefix2.TryGetValue(firstTerm[..2], out var prefix2Set) &&
            prefix2Set.Count > 0)
        {
            return prefix2Set
                .Select(path => _itemsByPath.TryGetValue(path, out var item) ? item : null)
                .Where(item => item is not null)!;
        }

        if (firstTerm.Length >= 1 &&
            _prefix1.TryGetValue(firstTerm[..1], out var prefix1Set) &&
            prefix1Set.Count > 0)
        {
            return prefix1Set
                .Select(path => _itemsByPath.TryGetValue(path, out var item) ? item : null)
                .Where(item => item is not null)!;
        }

        return _itemsByPath.Values;
    }

    private void AddInternal(IndexedItem item)
    {
        _itemsByPath[item.FullPath] = item;
        if (string.IsNullOrWhiteSpace(item.NormalizedName))
        {
            return;
        }

        var compact = CompactNormalized(item.NormalizedName);
        if (compact.Length == 0)
        {
            return;
        }

        var p1 = compact[..1];
        AddPrefix(_prefix1, p1, item.FullPath);
        if (compact.Length >= 2)
        {
            var p2 = compact[..2];
            AddPrefix(_prefix2, p2, item.FullPath);
        }
    }

    private void RemovePrefixes(IndexedItem item)
    {
        var compact = CompactNormalized(item.NormalizedName);
        if (compact.Length == 0)
        {
            return;
        }

        var p1 = compact[..1];
        RemovePrefix(_prefix1, p1, item.FullPath);
        if (compact.Length >= 2)
        {
            var p2 = compact[..2];
            RemovePrefix(_prefix2, p2, item.FullPath);
        }
    }

    private static void AddPrefix(Dictionary<string, HashSet<string>> map, string key, string fullPath)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            map[key] = set;
        }

        set.Add(fullPath);
    }

    private static void RemovePrefix(Dictionary<string, HashSet<string>> map, string key, string fullPath)
    {
        if (!map.TryGetValue(key, out var set))
        {
            return;
        }

        set.Remove(fullPath);
        if (set.Count == 0)
        {
            map.Remove(key);
        }
    }

    private static string CompactNormalized(string normalizedName)
    {
        return new string(normalizedName.Where(char.IsLetterOrDigit).ToArray());
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
}
