using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FastFileExplorer.Tests;

[TestClass]
public sealed class FlaUiE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(120000)]
    public void Startup_EmptyState_NoResultsBeforeTyping()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "startup_probe.txt"), "x");
            File.WriteAllText(Path.Combine(root, "another_probe.txt"), "x");

            using var session = Launch(root, cachePath);
            Assert.IsTrue(
                session.IsVisible("EmptyStatePanel") || session.GetResultRowCount() == 0,
                "Expected no visible results before typing.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(120000)]
    public void Search_FileAndFolder_WithTypeFilterSwitch()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "project_folder"));
            File.WriteAllText(Path.Combine(root, "project_document.txt"), "x");
            File.WriteAllText(Path.Combine(root, "noise.txt"), "x");

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("project", "project_folder");
            Assert.IsTrue(session.ContainsResultToken("project_folder"), "Expected folder in all results.");
            Assert.IsTrue(session.ContainsResultToken("project_document.txt"), "Expected file in all results.");

            session.SelectListItem("TypeFilterList", "Folder");
            Assert.IsTrue(session.WaitUntil(
                () => session.ContainsResultToken("project_folder") && !session.ContainsResultToken("project_document.txt"),
                TimeSpan.FromSeconds(6)),
                "Folder filter did not isolate folder results.");

            session.SelectListItem("TypeFilterList", "File");
            Assert.IsTrue(session.WaitUntil(
                () => session.ContainsResultToken("project_document.txt") && !session.ContainsResultToken("project_folder"),
                TimeSpan.FromSeconds(6)),
                "File filter did not isolate file results.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(120000)]
    public void Search_DateFilter_Last30Days_Behavior()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            var recent = Path.Combine(root, "recent_report.docx");
            var old = Path.Combine(root, "old_report.docx");
            File.WriteAllText(recent, "recent");
            File.WriteAllText(old, "old");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-50));

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("report", "recent_report.docx");
            session.SelectListItem("TypeFilterList", "Document");
            session.SelectListItem("DateFilterList", "Last 30 days");

            Assert.IsTrue(session.WaitUntil(() => session.ContainsResultToken("recent_report.docx"), TimeSpan.FromSeconds(6)));
            Assert.IsTrue(
                session.WaitUntil(() => !session.ContainsResultToken("old_report.docx"), TimeSpan.FromSeconds(6)),
                "Old document should be filtered out in Last 30 days.");

            session.SelectListItem("DateFilterList", "All");
            Assert.IsTrue(session.WaitUntil(() => session.ContainsResultToken("old_report.docx"), TimeSpan.FromSeconds(6)),
                "Old document should reappear after clearing date filter.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(120000)]
    public void Search_ClearAndRetype_NoStaleFlash()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "fcode_target.txt"), "x");
            File.WriteAllText(Path.Combine(root, "zeta_target.txt"), "x");
            for (var i = 0; i < 1000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"bulk_{i:D4}.txt"), "bulk");
            }

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("fcode", "fcode_target.txt");
            session.SetQuery(string.Empty);
            session.SetQuery("z");

            var staleAppeared = session.ContainsResultToken("fcode_target.txt");
            Thread.Sleep(220);
            staleAppeared = staleAppeared || session.ContainsResultToken("fcode_target.txt");
            Assert.IsFalse(staleAppeared, "Previous query result flashed after switching to a new query.");
            Assert.IsTrue(session.WaitUntil(() => session.ContainsResultToken("zeta_target.txt"), TimeSpan.FromSeconds(8)));
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [TestCategory("Performance")]
    [Timeout(180000)]
    public void Search_WhileIndexing_ReturnsResultsWithinBudget()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "live_anchor_root.txt"), "x");
            for (var i = 0; i < 12000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"loadfile_{i:D5}.txt"), "x");
            }

            using var session = Launch(root, cachePath);
            Assert.IsTrue(session.WaitUntilStatusContains("Indexing", TimeSpan.FromSeconds(25)),
                "Expected indexing to be active for this scenario.");

            var latencyMs = session.MeasureQueryLatencyMs("live anchor root", "live_anchor_root.txt");
            Assert.IsTrue(latencyMs <= 1500, $"Search latency while indexing too high: {latencyMs} ms.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [TestCategory("Performance")]
    [Timeout(180000)]
    public void Search_FirstLetter_Cold_RendersWithin150ms()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "x_target_probe.txt"), "x");
            for (var i = 0; i < 3000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"bulk_firstletter_{i:D5}.txt"), "x");
            }

            using (var warmup = Launch(root, cachePath))
            {
                Assert.IsTrue(warmup.WaitUntilIdleAndCached(TimeSpan.FromSeconds(40)),
                    "Warmup run did not complete indexing/cache sync in time.");
            }

            using var session = Launch(root, cachePath);
            var latencyMs = session.MeasureQueryLatencyMs("x", "x_target_probe.txt", TimeSpan.FromSeconds(2));
            Assert.IsTrue(latencyMs <= 150, $"Cold first-letter latency exceeded 150ms: {latencyMs} ms.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(180000)]
    public void Search_WhileIndexing_ResultsStayDeterministic()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "live_anchor_root.txt"), "x");
            for (var i = 0; i < 10000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"indexing_bulk_{i:D5}.txt"), "x");
            }

            using var session = Launch(root, cachePath);
            Assert.IsTrue(session.WaitUntilStatusContains("Indexing", TimeSpan.FromSeconds(25)),
                "Expected indexing to be active for deterministic-while-indexing test.");

            session.SetQuery("live anchor root");
            Assert.IsTrue(session.WaitUntil(() => session.ContainsResultToken("live_anchor_root.txt"), TimeSpan.FromSeconds(10)),
                "Expected anchor result while indexing.");
            var counts = new List<int>();
            for (var i = 0; i < 12; i++)
            {
                session.SetQuery("live anchor root");
                Thread.Sleep(30);
                Assert.IsTrue(session.ContainsResultToken("live_anchor_root.txt"),
                    $"Expected anchor result to stay visible at sample {i}.");
                counts.Add(session.GetResultRowCount());
                Thread.Sleep(70);
            }

            Assert.IsTrue(counts.All(c => c == counts[0]),
                $"Result count drifted while indexing for same query: {string.Join(", ", counts)}");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [TestCategory("Performance")]
    [Timeout(180000)]
    public void WarmCache_QueryLatency_P95WithinBudget()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "speedprobe_one.txt"), "x");
            File.WriteAllText(Path.Combine(root, "speedprobe_two.txt"), "x");
            File.WriteAllText(Path.Combine(root, "speedprobe_three.txt"), "x");
            for (var i = 0; i < 6000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"perf_bulk_{i:D5}.txt"), "x");
            }

            using (var warmup = Launch(root, cachePath))
            {
                Assert.IsTrue(warmup.WaitUntilIdleAndCached(TimeSpan.FromSeconds(45)),
                    "Warmup run did not finish indexing/cache sync in time.");
            }

            using var session = Launch(root, cachePath);
            var latencies = new List<long>();
            var probes = new (string Query, string Token)[]
            {
                ("speedprobe one", "speedprobe_one.txt"),
                ("speedprobe two", "speedprobe_two.txt"),
                ("speedprobe three", "speedprobe_three.txt"),
                ("speedprobe one", "speedprobe_one.txt"),
                ("speedprobe two", "speedprobe_two.txt"),
                ("speedprobe three", "speedprobe_three.txt"),
                ("speedprobe one", "speedprobe_one.txt"),
                ("speedprobe two", "speedprobe_two.txt"),
                ("speedprobe three", "speedprobe_three.txt")
            };

            foreach (var probe in probes)
            {
                latencies.Add(session.MeasureQueryLatencyMs(probe.Query, probe.Token));
            }

            var ordered = latencies.OrderBy(x => x).ToArray();
            var p95Index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
            p95Index = Math.Clamp(p95Index, 0, ordered.Length - 1);
            var p95 = ordered[p95Index];
            var max = ordered[^1];

            Assert.IsTrue(p95 <= 900, $"P95 query latency too high: {p95} ms. Latencies: {string.Join(", ", ordered)}");
            Assert.IsTrue(max <= 1600, $"Max query latency too high: {max} ms. Latencies: {string.Join(", ", ordered)}");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(180000)]
    public void Search_FastPhaseThenFullPhase_NoFlicker_NoEmptyFlash()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "fcode_anchor.txt"), "x");
            for (var i = 0; i < 5000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"fcode_bulk_{i:D5}.txt"), "x");
            }

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("fcode", "fcode_anchor.txt", TimeSpan.FromSeconds(4));

            var everVisible = false;
            var emptyAfterVisible = false;
            var sampleStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - sampleStart) < TimeSpan.FromMilliseconds(700))
            {
                var contains = session.ContainsResultToken("fcode_anchor.txt");
                var count = session.GetResultRowCount();
                if (contains)
                {
                    everVisible = true;
                }

                if (everVisible && count == 0)
                {
                    emptyAfterVisible = true;
                    break;
                }

                Thread.Sleep(15);
            }

            Assert.IsTrue(everVisible, "Expected target to become visible.");
            Assert.IsFalse(emptyAfterVisible, "Result list flashed empty after becoming visible.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [TestCategory("Performance")]
    [Timeout(180000)]
    public void Search_F_To_Fcode_EachStep_Instant()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "fcode_target.txt"), "x");
            File.WriteAllText(Path.Combine(root, "fc_probe.txt"), "x");
            File.WriteAllText(Path.Combine(root, "fco_probe.txt"), "x");
            File.WriteAllText(Path.Combine(root, "fcod_probe.txt"), "x");
            File.WriteAllText(Path.Combine(root, "zeta_irrelevant.txt"), "x");
            for (var i = 0; i < 3000; i++)
            {
                File.WriteAllText(Path.Combine(root, $"bulk_fcase_{i:D5}.txt"), "x");
            }

            using (var warmup = Launch(root, cachePath))
            {
                Assert.IsTrue(warmup.WaitUntilIdleAndCached(TimeSpan.FromSeconds(40)),
                    "Warmup run did not complete indexing/cache sync in time.");
            }

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("zeta", "zeta_irrelevant.txt", TimeSpan.FromSeconds(3));

            var fLatency = session.MeasureTransitionLatencyMs("f", "fcode_target.txt", "zeta_irrelevant.txt", TimeSpan.FromSeconds(2));
            var fcLatency = session.MeasureTransitionLatencyMs("fc", "fc_probe.txt", "zeta_irrelevant.txt", TimeSpan.FromSeconds(2));
            var fcoLatency = session.MeasureTransitionLatencyMs("fco", "fco_probe.txt", "zeta_irrelevant.txt", TimeSpan.FromSeconds(2));
            var fcodLatency = session.MeasureTransitionLatencyMs("fcod", "fcod_probe.txt", "zeta_irrelevant.txt", TimeSpan.FromSeconds(2));
            var fcodeLatency = session.MeasureTransitionLatencyMs("fcode", "fcode_target.txt", "zeta_irrelevant.txt", TimeSpan.FromSeconds(2));

            Console.WriteLine(
                $"Step latencies (ms): f={fLatency}, fc={fcLatency}, fco={fcoLatency}, fcod={fcodLatency}, fcode={fcodeLatency}");

            Assert.IsTrue(fLatency <= 100, $"f step exceeded 100ms: {fLatency}ms");
            Assert.IsTrue(fcLatency <= 100, $"fc step exceeded 100ms: {fcLatency}ms");
            Assert.IsTrue(fcoLatency <= 100, $"fco step exceeded 100ms: {fcoLatency}ms");
            Assert.IsTrue(fcodLatency <= 100, $"fcod step exceeded 100ms: {fcodLatency}ms");
            Assert.IsTrue(fcodeLatency <= 100, $"fcode step exceeded 100ms: {fcodeLatency}ms");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [TestCategory("Performance")]
    [Timeout(180000)]
    public void Search_PerKeystroke_Warm_RendersWithin100ms()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "fxcode_target.txt"), "x");
            File.WriteAllText(Path.Combine(root, "alpha_probe.txt"), "x");
            File.WriteAllText(Path.Combine(root, "beta_probe.txt"), "x");

            using (var warmup = Launch(root, cachePath))
            {
                Assert.IsTrue(warmup.WaitUntilIdleAndCached(TimeSpan.FromSeconds(20)));
            }

            using var session = Launch(root, cachePath);
            const string query = "fxcode";

            var coldStartLatency = session.MeasureQueryLatencyMs(query[..1], "fxcode_target.txt", TimeSpan.FromSeconds(2));

            var typeLatencies = new List<long>();
            for (var i = 2; i <= query.Length; i++)
            {
                var part = query[..i];
                typeLatencies.Add(session.MeasureQueryLatencyMs(part, "fxcode_target.txt", TimeSpan.FromSeconds(2)));
            }

            var deleteLatencies = new List<long>();
            for (var i = query.Length - 1; i >= 1; i--)
            {
                var part = query[..i];
                deleteLatencies.Add(session.MeasureQueryLatencyMs(part, "fxcode_target.txt", TimeSpan.FromSeconds(2)));
            }

            var worstType = typeLatencies.Max();
            var worstDelete = deleteLatencies.Max();
            Assert.IsTrue(coldStartLatency <= 150, $"Cold-start first-letter latency too high: {coldStartLatency} ms.");
            Assert.IsTrue(worstType <= 100, $"Typing latency exceeded 100ms: {worstType} ms. Samples: {string.Join(", ", typeLatencies)}");
            Assert.IsTrue(worstDelete <= 100, $"Delete latency exceeded 100ms: {worstDelete} ms. Samples: {string.Join(", ", deleteLatencies)}");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(180000)]
    public void Reindex_DoesNotBreakLiveSearch()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            File.WriteAllText(Path.Combine(root, "reindex_anchor.txt"), "x");
            for (var i = 0; i < 2500; i++)
            {
                File.WriteAllText(Path.Combine(root, $"reidx_{i:D5}.txt"), "x");
            }

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("reindex anchor", "reindex_anchor.txt");
            session.ClickButton("ReindexButton");
            Assert.IsTrue(session.WaitUntil(
                    () =>
                    {
                        var status = session.GetStatusText();
                        return status.Contains("Reindexing", StringComparison.OrdinalIgnoreCase) ||
                               status.Contains("Indexing", StringComparison.OrdinalIgnoreCase);
                    },
                    TimeSpan.FromSeconds(12)),
                "Expected reindex action to start.");

            var latencyMs = session.MeasureQueryLatencyMs("reindex anchor", "reindex_anchor.txt", TimeSpan.FromSeconds(12));
            Assert.IsTrue(latencyMs <= 4000, $"Search took too long after reindex click: {latencyMs} ms.");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory("UI")]
    [Timeout(180000)]
    public void Search_SameQuery_ResultCountRemainsStable_WhenIdle()
    {
        if (!ShouldRunFlaUi())
        {
            return;
        }

        var root = CreateTempRoot();
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            for (var i = 0; i < 2500; i++)
            {
                File.WriteAllText(Path.Combine(root, $"stable_case_{i:D5}.txt"), "x");
            }

            using (var warmup = Launch(root, cachePath))
            {
                Assert.IsTrue(warmup.WaitUntilIdleAndCached(TimeSpan.FromSeconds(35)));
            }

            using var session = Launch(root, cachePath);
            _ = session.MeasureQueryLatencyMs("stable case", "stable_case_00001.txt", TimeSpan.FromSeconds(4));
            Assert.IsTrue(session.WaitUntilStatusContains("Ready", TimeSpan.FromSeconds(8)));

            var counts = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                session.SetQuery("stable case");
                Thread.Sleep(120);
                counts.Add(session.GetResultRowCount());
            }

            Assert.IsTrue(counts.All(c => c == counts[0]), $"Result count changed for same query: {string.Join(", ", counts)}");
        }
        finally
        {
            Cleanup(root, cachePath);
        }
    }

    private static bool ShouldRunFlaUi()
    {
        if (!System.OperatingSystem.IsWindows())
        {
            return false;
        }

        return string.Equals(Environment.GetEnvironmentVariable("RUN_FLAUI_E2E"), "1", StringComparison.Ordinal);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerFlaUi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root, string cachePath)
    {
        TestDataHelper.DeleteDirectoryWithRetry(root);
        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            TestDataHelper.DeleteDirectoryWithRetry(cacheDirectory);
        }
    }

    private static UiSession Launch(string root, string cachePath)
    {
        var appExe = Path.Combine(AppContext.BaseDirectory, "FastFileExplorer.exe");
        Assert.IsTrue(File.Exists(appExe), $"FastFileExplorer.exe not found at {appExe}");

        var args = $"--test-mode --no-tray --root=\"{root}\" --cache=\"{cachePath}\"";
        var app = Application.Launch(new ProcessStartInfo(appExe, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(appExe) ?? AppContext.BaseDirectory
        });

        var automation = new UIA3Automation();
        var window = Retry.WhileNull(
            () => app.GetMainWindow(automation),
            timeout: TimeSpan.FromSeconds(20),
            throwOnTimeout: false).Result;
        Assert.IsNotNull(window, "Main window did not open.");

        var searchBox = Retry.WhileNull(
            () => window!.FindFirstDescendant(cf => cf.ByAutomationId("SearchBox"))?.AsTextBox(),
            timeout: TimeSpan.FromSeconds(12),
            throwOnTimeout: false).Result;
        Assert.IsNotNull(searchBox, "SearchBox not found.");

        return new UiSession(app, automation, window!, searchBox!, cachePath);
    }

    private sealed class UiSession : IDisposable
    {
        private readonly Application _app;
        private readonly UIA3Automation _automation;
        private readonly Window _window;
        private readonly TextBox _searchBox;
        private readonly string _cachePath;

        public UiSession(Application app, UIA3Automation automation, Window window, TextBox searchBox, string cachePath)
        {
            _app = app;
            _automation = automation;
            _window = window;
            _searchBox = searchBox;
            _cachePath = cachePath;
        }

        public void SetQuery(string query)
        {
            _searchBox.Focus();
            _searchBox.Text = query;
        }

        public long MeasureQueryLatencyMs(string query, string expectedToken, TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
            var sw = Stopwatch.StartNew();
            SetQuery(query);
            var found = WaitUntil(() => ContainsResultToken(expectedToken), effectiveTimeout);
            sw.Stop();
            Assert.IsTrue(found, $"Expected token '{expectedToken}' for query '{query}'.");
            return sw.ElapsedMilliseconds;
        }

        public long MeasureTransitionLatencyMs(string query, string expectedToken, string forbiddenToken, TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(8);
            var sw = Stopwatch.StartNew();
            SetQuery(query);
            var found = WaitUntil(
                () => ContainsResultToken(expectedToken) && !ContainsResultToken(forbiddenToken),
                effectiveTimeout);
            sw.Stop();
            Assert.IsTrue(found,
                $"Expected token '{expectedToken}' for query '{query}' and forbidden token '{forbiddenToken}' to be absent.");
            return sw.ElapsedMilliseconds;
        }

        public bool ContainsResultToken(string token)
        {
            var list = _window.FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"));
            if (list is null)
            {
                return false;
            }

            var texts = list.FindAllDescendants(cf => cf.ByControlType(ControlType.Text));
            return texts.Any(text => (text.Name ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsVisible(string automationId)
        {
            var element = _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is null)
            {
                return false;
            }

            return !element.Properties.IsOffscreen.ValueOrDefault;
        }

        public int GetResultRowCount()
        {
            var list = _window.FindFirstDescendant(cf => cf.ByAutomationId("ResultsList"));
            if (list is null)
            {
                return 0;
            }

            return list.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)).Length;
        }

        public void SelectListItem(string listAutomationId, string itemText)
        {
            var list = _window.FindFirstDescendant(cf => cf.ByAutomationId(listAutomationId));
            Assert.IsNotNull(list, $"List '{listAutomationId}' not found.");

            var item = list!.FindFirstDescendant(cf =>
                cf.ByControlType(ControlType.ListItem).And(cf.ByName(itemText)));
            Assert.IsNotNull(item, $"List item '{itemText}' not found in '{listAutomationId}'.");

            var selectPattern = item!.Patterns.SelectionItem.PatternOrDefault;
            if (selectPattern is not null)
            {
                selectPattern.Select();
                return;
            }

            var clickablePoint = item.GetClickablePoint();
            Mouse.MoveTo(clickablePoint);
            Mouse.Click();
        }

        public void ClickButton(string automationId)
        {
            var button = _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            Assert.IsNotNull(button, $"Button '{automationId}' not found.");

            var invoke = button!.Patterns.Invoke.PatternOrDefault;
            if (invoke is not null)
            {
                invoke.Invoke();
                return;
            }

            var clickablePoint = button.GetClickablePoint();
            Mouse.MoveTo(clickablePoint);
            Mouse.Click();
        }

        public bool WaitUntilStatusContains(string token, TimeSpan timeout)
        {
            return WaitUntil(() => GetStatusText().Contains(token, StringComparison.OrdinalIgnoreCase), timeout);
        }

        public bool WaitUntilIdleAndCached(TimeSpan timeout)
        {
            return WaitUntil(() =>
            {
                var status = GetStatusText();
                var notIndexing = !status.Contains("Indexing", StringComparison.OrdinalIgnoreCase) &&
                                  !status.Contains("Reindexing", StringComparison.OrdinalIgnoreCase);
                var cacheReady = File.Exists(_cachePath) && new FileInfo(_cachePath).Length > 0;
                return notIndexing && cacheReady;
            }, timeout);
        }

        public bool WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            return WaitUntilInternal(condition, timeout);
        }

        public string GetStatusText()
        {
            var status = _window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"));
            return status?.Name ?? string.Empty;
        }

        public void Dispose()
        {
            try
            {
                _window.Close();
            }
            catch
            {
                // Ignore close failures.
            }

            Thread.Sleep(300);

            if (!_app.HasExited)
            {
                try
                {
                    _app.Kill();
                }
                catch
                {
                    // Ignore force-close failures.
                }
            }

            _automation.Dispose();
            _app.Dispose();
        }

        private static bool WaitUntilInternal(Func<bool> condition, TimeSpan timeout)
        {
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started) < timeout)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return false;
        }
    }
}
