using System.Collections.Generic;
using System.Threading;
using FastFileExplorer.Models;

namespace FastFileExplorer.Services;

internal sealed class InMemorySearchIndex
{
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, IndexedItem> _itemsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<char, List<string>> _prefix1 = [];
    private readonly Dictionary<string, List<string>> _prefix2 = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            _gate.EnterReadLock();
            try
            {
                return _itemsByPath.Count;
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    public void Rebuild(IReadOnlyList<IndexedItem> items)
    {
        _gate.EnterWriteLock();
        try
        {
            _itemsByPath.Clear();
            _prefix1.Clear();
            _prefix2.Clear();

            foreach (var item in items
                         .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                _itemsByPath[item.FullPath] = item;
                AddPrefixes(item);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Upsert(IndexedItem item)
    {
        _gate.EnterWriteLock();
        try
        {
            if (_itemsByPath.TryGetValue(item.FullPath, out var existing))
            {
                RemovePrefixes(existing);
            }

            _itemsByPath[item.FullPath] = item;
            AddPrefixes(item);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Remove(string fullPath)
    {
        _gate.EnterWriteLock();
        try
        {
            if (!_itemsByPath.TryGetValue(fullPath, out var existing))
            {
                return;
            }

            _itemsByPath.Remove(fullPath);
            RemovePrefixes(existing);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public IReadOnlyList<IndexedItem> SearchFastCandidates(string query, SearchOptions options, int limit)
    {
        return SearchCore(query, options, limit, candidateMultiplier: 4, candidateCeiling: 500, fullMode: false);
    }

    public IReadOnlyList<IndexedItem> SearchFullRelevance(string query, SearchOptions options, int limit)
    {
        return SearchCore(query, options, limit, candidateMultiplier: 14, candidateCeiling: 4500, fullMode: true);
    }

    private IReadOnlyList<IndexedItem> SearchCore(
        string query,
        SearchOptions options,
        int limit,
        int candidateMultiplier,
        int candidateCeiling,
        bool fullMode)
    {
        if (limit <= 0)
        {
            return [];
        }

        var terms = NormalizeTerms(query);
        if (terms.Length == 0)
        {
            return [];
        }

        var candidateTarget = Math.Min(candidateCeiling, Math.Max(limit, limit * candidateMultiplier));
        var firstTerm = terms[0];

        List<IndexedItem> candidates = [];

        _gate.EnterReadLock();
        try
        {
            foreach (var path in GetCandidatePaths(firstTerm))
            {
                if (!_itemsByPath.TryGetValue(path, out var item))
                {
                    continue;
                }

                if (!PassesTerms(item, terms) ||
                    !PassesItemFilter(item, options.ItemFilter) ||
                    !PassesDateFilter(item, options.DateFilter))
                {
                    continue;
                }

                candidates.Add(item);
                if (candidates.Count >= candidateTarget)
                {
                    break;
                }
            }

            if (candidates.Count == 0)
            {
                // Fallback for uncommon names where prefix buckets miss.
                foreach (var item in _itemsByPath.Values)
                {
                    if (!PassesTerms(item, terms) ||
                        !PassesItemFilter(item, options.ItemFilter) ||
                        !PassesDateFilter(item, options.DateFilter))
                    {
                        continue;
                    }

                    candidates.Add(item);
                    if (candidates.Count >= candidateTarget)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _gate.ExitReadLock();
        }

        return SortDeterministically(candidates, firstTerm, limit, fullMode);
    }

    private IEnumerable<string> GetCandidatePaths(string firstTerm)
    {
        if (string.IsNullOrWhiteSpace(firstTerm))
        {
            return [];
        }

        if (firstTerm.Length == 1)
        {
            var c = firstTerm[0];
            return _prefix1.TryGetValue(c, out var list) ? list : [];
        }

        var key = firstTerm[..2];
        return _prefix2.TryGetValue(key, out var pairList) ? pairList : [];
    }

    private void AddPrefixes(IndexedItem item)
    {
        foreach (var token in SplitNameTokens(item.NormalizedName))
        {
            if (token.Length == 0)
            {
                continue;
            }

            var k1 = token[0];
            if (!_prefix1.TryGetValue(k1, out var list1))
            {
                list1 = [];
                _prefix1[k1] = list1;
            }
            list1.Add(item.FullPath);

            if (token.Length >= 2)
            {
                var k2 = token[..2];
                if (!_prefix2.TryGetValue(k2, out var list2))
                {
                    list2 = [];
                    _prefix2[k2] = list2;
                }
                list2.Add(item.FullPath);
            }
        }
    }

    private void RemovePrefixes(IndexedItem item)
    {
        foreach (var token in SplitNameTokens(item.NormalizedName))
        {
            if (token.Length == 0)
            {
                continue;
            }

            var k1 = token[0];
            if (_prefix1.TryGetValue(k1, out var list1))
            {
                list1.Remove(item.FullPath);
                if (list1.Count == 0)
                {
                    _prefix1.Remove(k1);
                }
            }

            if (token.Length >= 2)
            {
                var k2 = token[..2];
                if (_prefix2.TryGetValue(k2, out var list2))
                {
                    list2.Remove(item.FullPath);
                    if (list2.Count == 0)
                    {
                        _prefix2.Remove(k2);
                    }
                }
            }
        }
    }

    private static IReadOnlyList<IndexedItem> SortDeterministically(
        IEnumerable<IndexedItem> source,
        string firstTerm,
        int limit,
        bool fullMode)
    {
        if (fullMode)
        {
            return source
                .OrderBy(item => GetRelevanceRank(item, firstTerm))
                .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.LastWriteTimeUtc)
                .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        return source
            .OrderBy(item => GetRelevanceRank(item, firstTerm))
            .ThenBy(item => item.Kind == IndexedItemKind.Folder ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static bool PassesTerms(IndexedItem item, string[] terms)
    {
        foreach (var term in terms)
        {
            if (!item.NormalizedName.Contains(term, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<string> SplitNameTokens(string normalizedName)
    {
        return normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] NormalizeTerms(string query)
    {
        var normalized = new string(query
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray());

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
