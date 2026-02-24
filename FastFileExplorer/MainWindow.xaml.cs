using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastFileExplorer.Models;
using FastFileExplorer.Services;
using Forms = System.Windows.Forms;

namespace FastFileExplorer;

public partial class MainWindow : Window
{
    private readonly FileIndexService _indexService = new();
    private readonly FileIconProvider _iconProvider = new();
    private readonly ObservableCollection<SearchResultRow> _rows = [];
    private readonly string[] _roots;
    private readonly string _cachePath;
    private CancellationTokenSource? _phaseAFetchCts;
    private readonly object _searchWorkerLock = new();
    private SearchWorkItem? _latestSearchWorkItem;
    private bool _phaseBWorkerRunning;
    private int _queryVersion;
    private DateTime _lastIndexDrivenSearchUtc = DateTime.MinValue;
    private string _lastServiceStatus = "Ready";
    private string _lastSearchQuery = string.Empty;
    private SearchOptions _lastSearchOptions = new();
    private IReadOnlyList<IndexedItem> _lastSearchItems = Array.Empty<IndexedItem>();
    private readonly List<CachedQueryResult> _recentQueryCache = [];
    private readonly SearchRenderState _searchRenderState = new();
    private bool _includeLowLevelContent;
    private bool _builtInReady;
    private bool _shellMenuOpen;
    private bool _allowExit;
    private bool _isShuttingDown;
    private bool _resumeIncompleteIndex;
    private bool _checkpointInProgress;
    private bool _reindexInProgress;
    private int _lastCheckpointItemCount;
    private DateTime _lastCheckpointUtc = DateTime.MinValue;
    private Forms.NotifyIcon? _trayIcon;
    private readonly SemaphoreSlim _reindexGate = new(1, 1);
    private readonly DispatcherTimer _checkpointTimer;
    private const int MaxResults = 80;
    private const int MaxRecentQueryCacheEntries = 32;

    private readonly bool _disableTrayIcon;

    public MainWindow(IReadOnlyList<string>? roots = null, string? cachePath = null, bool disableTrayIcon = false)
    {
        _checkpointTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(2)
        };
        _checkpointTimer.Tick += async (_, _) => await TrySaveCheckpointAsync();

        InitializeComponent();

        ResultsList.ItemsSource = _rows;
        _disableTrayIcon = disableTrayIcon;
        _roots = roots?.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                 ?? BuildDefaultRoots();
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? SettingsService.GetDefaultCachePath() : cachePath;

        _indexService.StatusChanged += message =>
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                _lastServiceStatus = message;
                StatusText.Text = message;
            });
        };
        _indexService.IndexChanged += () =>
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if ((now - _lastIndexDrivenSearchUtc) < TimeSpan.FromSeconds(1.2))
                {
                    return;
                }

                _lastIndexDrivenSearchUtc = now;
                QueueSearch(fromIndexRefresh: true);
            });
        };

        if (!_disableTrayIcon)
        {
            InitializeTrayIcon();
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        ApplyVersionInfo();
        _ = InitializeIndexingAsync();
    }

    private async Task InitializeIndexingAsync()
    {
        try
        {
            var settings = SettingsService.Load();
            _includeLowLevelContent = settings.IncludeLowLevelContent;
            _resumeIncompleteIndex = settings.ResumeIncompleteIndex;
            IncludeLowLevelCheckBox.IsChecked = _includeLowLevelContent;
            IncludeLowLevelCheckBox.IsEnabled = true;
            await EnsureBuiltInBackendReadyAsync();

            QueueSearch();
        }
        catch
        {
            StatusText.Text = "Initialization failed.";
        }
    }

    private async Task EnsureBuiltInBackendReadyAsync()
    {
        if (_builtInReady)
        {
            return;
        }

        StatusText.Text = "Loading built-in index...";
        SettingsService.MigrateLegacyCacheIfNeeded(_cachePath);

        var loadedFromCache = await _indexService.LoadCacheAsync(_cachePath, _includeLowLevelContent);
        _indexService.StartWatchersOnly(_roots, _includeLowLevelContent);

        if (!loadedFromCache || _resumeIncompleteIndex)
        {
            if (_resumeIncompleteIndex && loadedFromCache)
            {
                StatusText.Text = "Resuming indexing without reset...";
                await _indexService.ContinueIndexAsync(_roots, _includeLowLevelContent);
            }
            else
            {
                StatusText.Text = _indexService.LastCacheLoadMessage ?? "Cache missing. Building index.";
                await _indexService.StartOrRebuildIndexAsync(_roots, _includeLowLevelContent);
            }

            await _indexService.SaveCacheAsync(_cachePath);
            _resumeIncompleteIndex = false;
            SaveSettings();
        }
        else if (!string.IsNullOrWhiteSpace(_indexService.LastCacheLoadMessage))
        {
            StatusText.Text = _indexService.LastCacheLoadMessage;
        }

        _builtInReady = true;
        _checkpointTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowExit && !_disableTrayIcon)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        var wasIndexing = _indexService.IsIndexing;
        _checkpointTimer.Stop();
        _phaseAFetchCts?.Cancel();
        _phaseAFetchCts?.Dispose();
        _phaseAFetchCts = null;
        _indexService.CancelIndexing();
        _resumeIncompleteIndex = wasIndexing;
        SaveSettings();
        DisposeTrayIcon();
        _indexService.Shutdown(flushPendingChanges: true, flushTimeoutMs: 9000);
    }

    private async void ReindexButton_Click(object sender, RoutedEventArgs e)
    {
        await RunReindexAsync();
    }

    private async void IncludeLowLevelCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _includeLowLevelContent = IncludeLowLevelCheckBox.IsChecked == true;
        StatusText.Text = _includeLowLevelContent
            ? "Reindexing with hidden/system files on..."
            : "Reindexing with hidden/system files off...";
        await RunReindexAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        var isEmpty = string.IsNullOrWhiteSpace(query);
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;

        if (isEmpty)
        {
            Interlocked.Increment(ref _queryVersion);
            CancelFastPhaseLookup();
            lock (_searchWorkerLock)
            {
                _latestSearchWorkItem = null;
            }

            _rows.Clear();
            ResultsList.SelectedIndex = -1;
            _lastSearchQuery = string.Empty;
            _lastSearchItems = Array.Empty<IndexedItem>();
            _searchRenderState.LastStableQuery = string.Empty;
            _searchRenderState.LastStableOptions = new SearchOptions();
            _searchRenderState.LastStableResults = Array.Empty<IndexedItem>();
            UpdateStatus();
            return;
        }

        QueueSearch();
    }

    private void SearchBox_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Down && _rows.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
            ResultsList.Focus();
        }
    }

    private void TypeFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueSearch();
    }

    private void DateFilterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        QueueSearch();
    }

    private void ResultsList_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelected();
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void ResultsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_shellMenuOpen)
        {
            return;
        }

        var row = GetSelectedRow();
        if (row is null || (string.IsNullOrWhiteSpace(row.FullPath)))
        {
            return;
        }

        var path = row.FullPath;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusText.Text = "Item no longer exists.";
            return;
        }

        var screenPoint = PointToScreen(e.GetPosition(this));
        _shellMenuOpen = true;
        ShowBuiltInContextMenu(path, screenPoint);

        e.Handled = true;
    }

    private void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not System.Windows.Controls.ListViewItem)
        {
            element = VisualTreeHelper.GetParent(element);
        }

        if (element is System.Windows.Controls.ListViewItem item)
        {
            item.IsSelected = true;
        }
    }

    private void ShowBuiltInContextMenu(string path, System.Windows.Point screenPoint)
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.AbsolutePoint,
            HorizontalOffset = screenPoint.X,
            VerticalOffset = screenPoint.Y
        };

        menu.Closed += (_, _) =>
        {
            _shellMenuOpen = false;
            QueueSearch();
        };

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => OpenPath(path);
        menu.Items.Add(openItem);

        var openContaining = new MenuItem { Header = "Open Containing Folder" };
        openContaining.Click += (_, _) => OpenContainingFolder(path);
        menu.Items.Add(openContaining);

        var copyPath = new MenuItem { Header = "Copy Path to Clipboard" };
        copyPath.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(path);
            }
            catch
            {
                StatusText.Text = "Path copy failed.";
            }
        };
        menu.Items.Add(copyPath);

        var editVsCode = new MenuItem { Header = "Edit with VS Code" };
        editVsCode.Click += (_, _) => EditWithVsCode(path);
        menu.Items.Add(editVsCode);

        menu.Items.Add(new Separator());

        var propertiesItem = new MenuItem { Header = "Properties" };
        propertiesItem.Click += (_, _) => OpenPropertiesInExplorer(path);
        menu.Items.Add(propertiesItem);

        menu.IsOpen = true;
    }

    private void OpenContainingFolder(string path)
    {
        if (Directory.Exists(path))
        {
            OpenPath(path);
            return;
        }

        OpenInExplorer(path);
    }

    private void EditWithVsCode(string path)
    {
        try
        {
            var argument = Directory.Exists(path)
                ? $"\"{path}\""
                : $"-g \"{path}\"";

            _ = Process.Start(new ProcessStartInfo("code", argument) { UseShellExecute = true });
        }
        catch
        {
            StatusText.Text = "VS Code command not found.";
        }
    }

    private void OpenSelected()
    {
        var row = GetSelectedRow();
        if (row is null)
        {
            return;
        }

        OpenPath(row.FullPath);
    }

    private void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            StatusText.Text = "Item no longer exists.";
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            StatusText.Text = "Open failed.";
        }
    }

    private void OpenInExplorer(string fullPath)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            StatusText.Text = "Open containing folder failed.";
        }
    }

    private void OpenPropertiesInExplorer(string path)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            StatusText.Text = "Properties failed.";
        }
    }

    private SearchResultRow? GetSelectedRow()
    {
        return ResultsList.SelectedItem as SearchResultRow;
    }

    private void QueueSearch(bool fromIndexRefresh = false)
    {
        if (_shellMenuOpen)
        {
            return;
        }

        var work = CaptureSearchWorkItem();
        if (work is null)
        {
            if (!fromIndexRefresh)
            {
                UpdateStatus();
            }
            return;
        }

        RunFastPhase(work);
        QueueFullRelevance(work);
    }

    private SearchWorkItem? CaptureSearchWorkItem()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var options = new SearchOptions
        {
            ItemFilter = GetSelectedItemFilter(),
            DateFilter = GetSelectedDateFilter()
        };
        var selectedPath = GetSelectedRow()?.FullPath;
        var scrollViewer = FindDescendant<ScrollViewer>(ResultsList);
        var previousOffset = scrollViewer?.VerticalOffset ?? 0;
        var version = Interlocked.Increment(ref _queryVersion);

        return new SearchWorkItem(query, options, version, selectedPath, previousOffset);
    }

    private void RunFastPhase(SearchWorkItem work)
    {
        if (_shellMenuOpen)
        {
            return;
        }

        TryRenderInstantFromCache(work);
        CancelFastPhaseLookup();

        var cts = new CancellationTokenSource();
        _phaseAFetchCts = cts;
        var token = cts.Token;

        _ = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return _indexService.SearchFastCandidates(work.Query, work.Options, MaxResults);
        }, token).ContinueWith(async task =>
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyFastPhaseResults(work, task.Result);
            }, DispatcherPriority.Input);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ApplyFastPhaseResults(SearchWorkItem work, IReadOnlyList<IndexedItem> results)
    {
        if (_shellMenuOpen || !IsWorkCurrent(work))
        {
            return;
        }

        if (results.Count == 0)
        {
            return;
        }

        ApplyRenderedResults(work, results, isStable: false, allowEmpty: false);
    }

    private void QueueFullRelevance(SearchWorkItem work)
    {
        lock (_searchWorkerLock)
        {
            _latestSearchWorkItem = work;
            if (_phaseBWorkerRunning)
            {
                return;
            }

            _phaseBWorkerRunning = true;
        }

        _ = Task.Run(ProcessFullRelevanceLoopAsync);
    }

    private async Task ProcessFullRelevanceLoopAsync()
    {
        while (true)
        {
            SearchWorkItem? work;
            lock (_searchWorkerLock)
            {
                work = _latestSearchWorkItem;
                if (work is null)
                {
                    _phaseBWorkerRunning = false;
                    return;
                }
            }

            IReadOnlyList<IndexedItem> results;
            try
            {
                results = await Task.Run(() => _indexService.SearchFullRelevance(work.Query, work.Options, MaxResults));
            }
            catch
            {
                results = Array.Empty<IndexedItem>();
            }

            await Dispatcher.InvokeAsync(() =>
            {
                ApplyFullPhaseResults(work, results);
            }, DispatcherPriority.Background);

            lock (_searchWorkerLock)
            {
                if (_latestSearchWorkItem is null || _latestSearchWorkItem.Version <= work.Version)
                {
                    _phaseBWorkerRunning = false;
                    return;
                }
            }
        }
    }

    private void ApplyFullPhaseResults(SearchWorkItem work, IReadOnlyList<IndexedItem> results)
    {
        if (_shellMenuOpen || !IsWorkCurrent(work))
        {
            return;
        }

        if (results.Count == 0 &&
            _searchRenderState.LastRenderedVersion == work.Version &&
            _searchRenderState.LastStableResults.Count > 0)
        {
            return;
        }

        ApplyRenderedResults(work, results, isStable: true, allowEmpty: true);
    }

    private void ApplyRenderedResults(SearchWorkItem work, IReadOnlyList<IndexedItem> results, bool isStable, bool allowEmpty)
    {
        if (_shellMenuOpen || !IsWorkCurrent(work))
        {
            return;
        }

        if (results.Count == 0 && !allowEmpty)
        {
            return;
        }

        _lastSearchQuery = work.Query;
        _lastSearchOptions = work.Options;
        _lastSearchItems = results;
        CacheRecentQuery(work.Query, work.Options, results);

        _searchRenderState.LastRenderedVersion = work.Version;
        _searchRenderState.LastStableQuery = work.Query;
        _searchRenderState.LastStableOptions = work.Options;
        if (results.Count > 0 || isStable)
        {
            _searchRenderState.LastStableResults = results;
        }

        RenderResults(results, work.SelectedPath, work.ScrollOffset);
        UpdateStatus();
    }

    private bool IsWorkCurrent(SearchWorkItem work)
    {
        if (work.Version != Volatile.Read(ref _queryVersion))
        {
            return false;
        }

        if (!string.Equals(SearchBox.Text, work.Query, StringComparison.Ordinal))
        {
            return false;
        }

        var currentOptions = new SearchOptions
        {
            ItemFilter = GetSelectedItemFilter(),
            DateFilter = GetSelectedDateFilter()
        };
        return SameOptions(currentOptions, work.Options);
    }

    private void CancelFastPhaseLookup()
    {
        var cts = _phaseAFetchCts;
        _phaseAFetchCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void TryRenderInstantFromCache(SearchWorkItem work)
    {
        var candidate = GetBestInstantCandidate(work.Query, work.Options);
        if (candidate is null)
        {
            return;
        }

        var filtered = FilterForQuery(candidate, work.Query);
        if (filtered.Count == 0)
        {
            ApplyRenderedResults(work, Array.Empty<IndexedItem>(), isStable: false, allowEmpty: true);
            return;
        }

        ApplyRenderedResults(work, filtered, isStable: false, allowEmpty: false);
    }

    private IReadOnlyList<IndexedItem>? GetBestInstantCandidate(string query, SearchOptions options)
    {
        if (!string.IsNullOrWhiteSpace(_lastSearchQuery) &&
            SameOptions(_lastSearchOptions, options) &&
            query.StartsWith(_lastSearchQuery, StringComparison.OrdinalIgnoreCase))
        {
            return _lastSearchItems;
        }

        CachedQueryResult? best = null;
        foreach (var entry in _recentQueryCache)
        {
            if (!SameOptions(entry.Options, options))
            {
                continue;
            }

            if (!query.StartsWith(entry.Query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || entry.Query.Length > best.Query.Length)
            {
                best = entry;
            }
        }

        if (best is not null)
        {
            return best.Results;
        }

        if (SameOptions(_lastSearchOptions, options) && _lastSearchItems.Count > 0)
        {
            return _lastSearchItems;
        }

        return null;
    }

    private static List<IndexedItem> FilterForQuery(IReadOnlyList<IndexedItem> source, string query)
    {
        var terms = NormalizeTerms(query);
        if (terms.Length == 0)
        {
            return [];
        }

        return source
            .Where(item => terms.All(term => item.NormalizedName.Contains(term, StringComparison.Ordinal)))
            .Take(MaxResults)
            .ToList();
    }

    private void CacheRecentQuery(string query, SearchOptions options, IReadOnlyList<IndexedItem> results)
    {
        if (string.IsNullOrWhiteSpace(query) || results.Count == 0)
        {
            return;
        }

        _recentQueryCache.RemoveAll(entry =>
            string.Equals(entry.Query, query, StringComparison.OrdinalIgnoreCase) &&
            SameOptions(entry.Options, options));
        _recentQueryCache.Insert(0, new CachedQueryResult(query, options, results));
        if (_recentQueryCache.Count > MaxRecentQueryCacheEntries)
        {
            _recentQueryCache.RemoveRange(MaxRecentQueryCacheEntries, _recentQueryCache.Count - MaxRecentQueryCacheEntries);
        }
    }

    private void RenderResults(IReadOnlyList<IndexedItem> results, string? selectedPath, double previousOffset)
    {
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        _rows.Clear();
        foreach (var item in results)
        {
            _rows.Add(ToRow(item));
        }

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var selectedIndex = _rows
                .Select((row, index) => new { row, index })
                .FirstOrDefault(x => string.Equals(x.row.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?.index ?? -1;

            if (selectedIndex >= 0)
            {
                ResultsList.SelectedIndex = selectedIndex;
            }
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            var currentScrollViewer = FindDescendant<ScrollViewer>(ResultsList);
            if (currentScrollViewer is not null)
            {
                var offset = Math.Clamp(previousOffset, 0, currentScrollViewer.ScrollableHeight);
                currentScrollViewer.ScrollToVerticalOffset(offset);
            }
        }, DispatcherPriority.Background);
    }

    private static bool SameOptions(SearchOptions left, SearchOptions right)
    {
        return left.ItemFilter == right.ItemFilter && left.DateFilter == right.DateFilter;
    }

    private static string[] NormalizeTerms(string query)
    {
        var normalized = new string(query
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray());

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void UpdateStatus()
    {
        if (StatusText is null)
        {
            return;
        }

        if (_indexService.IsIndexing)
        {
            StatusText.Text = _lastServiceStatus;
            return;
        }

        var state = _indexService.IsIndexing ? "Indexing" : "Ready";
        var lowLevelLabel = _includeLowLevelContent ? "low-level on" : "low-level off";
        StatusText.Text = $"{state} | Items: {_indexService.ItemCount:N0} | Showing: {_rows.Count:N0} | Roots: {_indexService.WatchedRootsCount:N0} | {lowLevelLabel}";
    }

    private ItemFilter GetSelectedItemFilter()
    {
        if (TypeFilterList.SelectedItem is not ListBoxItem item || item.Tag is not string tag)
        {
            return ItemFilter.All;
        }

        return Enum.TryParse<ItemFilter>(tag, out var result) ? result : ItemFilter.All;
    }

    private DateFilter GetSelectedDateFilter()
    {
        if (DateFilterList.SelectedItem is not ListBoxItem item || item.Tag is not string tag)
        {
            return DateFilter.All;
        }

        return Enum.TryParse<DateFilter>(tag, out var result) ? result : DateFilter.All;
    }

    private SearchResultRow ToRow(IndexedItem item)
    {
        return new SearchResultRow
        {
            FullPath = item.FullPath,
            Name = item.Name,
            Directory = item.Directory,
            Type = item.Kind == IndexedItemKind.Folder ? "Folder" : item.Extension.ToUpperInvariant(),
            SizeLabel = item.Kind == IndexedItemKind.Folder ? "-" : FormatBytes(item.SizeBytes),
            ModifiedLabel = item.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Kind = item.Kind,
            Icon = _iconProvider.GetIcon(item)
        };
    }

    public static string[] BuildDefaultRoots()
    {
        var preferred = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable))
            .Select(drive => drive.RootDirectory.FullName);

        return preferred
            .Concat(drives)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private void SaveSettings()
    {
        SettingsService.Save(new AppSettings
        {
            IncludeLowLevelContent = _includeLowLevelContent,
            CachePath = _cachePath,
            ResumeIncompleteIndex = _resumeIncompleteIndex
        });
    }

    private async Task RunReindexAsync()
    {
        if (_reindexInProgress)
        {
            StatusText.Text = "Reindex already running...";
            return;
        }

        await _reindexGate.WaitAsync();
        _reindexInProgress = true;
        try
        {
            ReindexButton.IsEnabled = false;
            IncludeLowLevelCheckBox.IsEnabled = false;
            StatusText.Text = "Reindexing...";
            _includeLowLevelContent = IncludeLowLevelCheckBox.IsChecked == true;
            await _indexService.StartOrRebuildIndexAsync(_roots, _includeLowLevelContent);
            await _indexService.SaveCacheAsync(_cachePath);
            _resumeIncompleteIndex = false;
            SaveSettings();
            QueueSearch();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Reindex canceled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Reindex failed: {ex.Message}";
        }
        finally
        {
            _reindexInProgress = false;
            IncludeLowLevelCheckBox.IsEnabled = true;
            ReindexButton.IsEnabled = true;
            _reindexGate.Release();
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

            _trayIcon = new Forms.NotifyIcon
            {
                Text = "Fast File Explorer",
                Visible = true,
                ContextMenuStrip = menu,
                Icon = GetTrayIcon()
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        }
        catch
        {
            _trayIcon = null;
        }
    }

    private static System.Drawing.Icon GetTrayIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("/icon/idemia_new.ico", UriKind.Relative));
            if (streamInfo?.Stream is not null)
            {
                using var stream = streamInfo.Stream;
                var icon = new System.Drawing.Icon(stream);
                if (icon is not null)
                {
                    return icon;
                }
            }

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // Fall back to application icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        SearchBox.Focus();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    public void ShowFromExternalActivation()
    {
        RestoreFromTray();
    }

    private void DisposeTrayIcon()
    {
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        catch
        {
            // Ignore tray shutdown failures.
        }
        finally
        {
            _trayIcon = null;
        }
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        var childrenCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                return result;
            }

            var descendant = FindDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private async Task TrySaveCheckpointAsync()
    {
        if (!_indexService.IsIndexing || _checkpointInProgress)
        {
            return;
        }

        var current = _indexService.ItemCount;
        var enoughNewItems = current - _lastCheckpointItemCount >= 150_000;
        var enoughTimeElapsed = (DateTime.UtcNow - _lastCheckpointUtc) >= TimeSpan.FromMinutes(8);
        if (!enoughNewItems && !enoughTimeElapsed)
        {
            return;
        }

        _checkpointInProgress = true;
        try
        {
            await _indexService.SaveCacheAsync(_cachePath);
            _lastCheckpointItemCount = current;
            _lastCheckpointUtc = DateTime.UtcNow;
        }
        finally
        {
            _checkpointInProgress = false;
        }
    }

    private void ApplyVersionInfo()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
        {
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        }

        VersionText.Text = $"v{version}";
        Title = $"Fast File Explorer v{version}";
    }

    private sealed class SearchResultRow
    {
        public required string FullPath { get; init; }
        public required string Name { get; init; }
        public required string Directory { get; init; }
        public required string Type { get; init; }
        public required string SizeLabel { get; init; }
        public required string ModifiedLabel { get; init; }
        public required IndexedItemKind Kind { get; init; }
        public required ImageSource Icon { get; init; }
    }

    private sealed record CachedQueryResult(string Query, SearchOptions Options, IReadOnlyList<IndexedItem> Results);

    private sealed record SearchWorkItem(
        string Query,
        SearchOptions Options,
        int Version,
        string? SelectedPath,
        double ScrollOffset);

    private sealed class SearchRenderState
    {
        public int LastRenderedVersion { get; set; }
        public string LastStableQuery { get; set; } = string.Empty;
        public SearchOptions LastStableOptions { get; set; } = new();
        public IReadOnlyList<IndexedItem> LastStableResults { get; set; } = Array.Empty<IndexedItem>();
    }
}
