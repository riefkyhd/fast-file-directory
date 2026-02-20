using FastFileExplorer.Models;
using FastFileExplorer.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FastFileExplorer.Tests;

[TestClass]
public sealed class E2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task EndToEnd_WatcherChangeIsPersistedAndReloaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            var firstPath = Path.Combine(root, "first.txt");
            File.WriteAllText(firstPath, "first");

            using (var service = new FileIndexService())
            {
                _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

                var watcherAdded = Path.Combine(root, "from_watcher.txt");
                File.WriteAllText(watcherAdded, "watcher");
                await Task.Delay(1200);

                await service.SaveCacheAsync(cachePath);
            }

            using (var service = new FileIndexService())
            {
                var loaded = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                Assert.IsTrue(loaded, "Expected persisted cache to load.");

                var results = service.Search("from watcher", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 20);
                Assert.IsTrue(results.Any(r => string.Equals(r.Name, "from_watcher.txt", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }
}
