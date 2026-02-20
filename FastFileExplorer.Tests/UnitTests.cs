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
}
