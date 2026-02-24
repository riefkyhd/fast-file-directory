using FastFileExplorer.Models;
using FastFileExplorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FastFileExplorer.Tests;

[TestClass]
public sealed class UnitTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Search_PrioritizesFolderForSamePrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerUnit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "project"));
            File.WriteAllText(Path.Combine(root, "project.txt"), "x");

            using var service = new FileIndexService();
            var cachePath = TestDataHelper.CreateTempCachePath();
            await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var results = service.Search("project", new SearchOptions
            {
                ItemFilter = ItemFilter.All,
                DateFilter = DateFilter.All
            }, limit: 10);

            Assert.IsTrue(results.Count >= 2, "Expected both folder and file in results.");
            Assert.AreEqual(IndexedItemKind.Folder, results[0].Kind, "Folder should rank first for same prefix.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Search_AppliesTypeAndDateFilters()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerUnit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var recentDoc = Path.Combine(root, "recent.docx");
            var oldDoc = Path.Combine(root, "old.docx");
            File.WriteAllText(recentDoc, "recent");
            File.WriteAllText(oldDoc, "old");
            File.SetLastWriteTimeUtc(oldDoc, DateTime.UtcNow.AddDays(-40));

            using var service = new FileIndexService();
            var cachePath = TestDataHelper.CreateTempCachePath();
            await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var results = service.Search("doc", new SearchOptions
            {
                ItemFilter = ItemFilter.Document,
                DateFilter = DateFilter.Last30Days
            }, limit: 20);

            Assert.AreEqual(1, results.Count, "Only recent document should pass Last30Days filter.");
            Assert.IsTrue(results[0].Name.Contains("recent", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [Timeout(6000)]
    public async Task Search_RapidQueries_RemainsStable()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 1500, folderCount: 30);
        var cachePath = TestDataHelper.CreateTempCachePath();

        try
        {
            using var service = new FileIndexService();
            await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            for (var i = 0; i < 40; i++)
            {
                var query = i % 2 == 0 ? "file 1" : "file 2";
                var results = service.Search(query, new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 60);

                Assert.IsTrue(results.Count > 0, "Expected search to remain responsive across rapid repeated queries.");
            }
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    [Timeout(10000)]
    public async Task SearchFastCandidates_ShortQuery_ReturnsWithinBudget()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 4000, folderCount: 40);
        var cachePath = TestDataHelper.CreateTempCachePath();

        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var started = DateTime.UtcNow;
            var results = service.SearchFastCandidates("f", new SearchOptions
            {
                ItemFilter = ItemFilter.File,
                DateFilter = DateFilter.All
            }, limit: 80);
            var elapsed = DateTime.UtcNow - started;

            Assert.IsTrue(results.Count > 0, "Expected fast candidates to return results for short query.");
            Assert.IsTrue(elapsed <= TimeSpan.FromMilliseconds(350),
                $"Fast candidate query exceeded budget: {elapsed.TotalMilliseconds:N0} ms.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task SearchFullRelevance_IsDeterministic_ForSameQuery()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 3000, folderCount: 20);
        var cachePath = TestDataHelper.CreateTempCachePath();

        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var baseline = service.SearchFullRelevance("file", new SearchOptions
            {
                ItemFilter = ItemFilter.File,
                DateFilter = DateFilter.All
            }, limit: 120).Select(item => item.FullPath).ToArray();

            Assert.IsTrue(baseline.Length > 0, "Expected baseline results.");

            for (var i = 0; i < 10; i++)
            {
                var current = service.SearchFullRelevance("file", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 120).Select(item => item.FullPath).ToArray();

                CollectionAssert.AreEqual(baseline, current, $"Result ordering changed at iteration {i}.");
            }
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Search_FallbackContains_ReturnsInnerSubstring()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerUnit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachePath = TestDataHelper.CreateTempCachePath();

        try
        {
            File.WriteAllText(Path.Combine(root, "hygarde.txt"), "x");

            using var service = new FileIndexService();
            await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var results = service.Search("gard", new SearchOptions
            {
                ItemFilter = ItemFilter.File,
                DateFilter = DateFilter.All
            }, limit: 10);

            Assert.IsTrue(results.Any(r => r.Name.Equals("hygarde.txt", StringComparison.OrdinalIgnoreCase)),
                "Expected substring fallback match when FTS token-prefix does not match.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void Settings_Roundtrip_PersistsResumeFlag()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastFileExplorer");
        var settingsPath = Path.Combine(settingsDirectory, "settings.json");
        Directory.CreateDirectory(settingsDirectory);

        var backupPath = settingsPath + ".bak_test";
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }

        if (File.Exists(settingsPath))
        {
            File.Move(settingsPath, backupPath, overwrite: true);
        }

        try
        {
            SettingsService.Save(new AppSettings
            {
                IncludeLowLevelContent = true,
                CachePath = SettingsService.GetDefaultCachePath(),
                ResumeIncompleteIndex = true
            });

            var loaded = SettingsService.Load();
            Assert.IsTrue(loaded.ResumeIncompleteIndex, "Expected ResumeIncompleteIndex to persist in settings.");
            Assert.IsTrue(loaded.IncludeLowLevelContent);
        }
        finally
        {
            if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }

            if (File.Exists(backupPath))
            {
                File.Move(backupPath, settingsPath, overwrite: true);
            }
        }
    }

}
