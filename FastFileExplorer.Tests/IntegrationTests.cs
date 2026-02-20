using FastFileExplorer.Models;
using FastFileExplorer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FastFileExplorer.Tests;

[TestClass]
public sealed class IntegrationTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task SqliteCache_PersistsAcrossServiceInstances()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 400, folderCount: 20);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using (var service = new FileIndexService())
            {
                _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
                await service.SaveCacheAsync(cachePath);
                Assert.IsTrue(service.ItemCount >= 420, $"Expected at least 420 indexed entries, got {service.ItemCount}");
            }

            using (var service = new FileIndexService())
            {
                var loaded = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                Assert.IsTrue(loaded, "Expected SQLite cache to load successfully.");
                Assert.IsTrue(service.ItemCount >= 420, $"Expected at least 420 cached entries, got {service.ItemCount}");
            }
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task LegacyJsonCache_ImportsToSqlite()
    {
        var cachePath = TestDataHelper.CreateTempCachePath();
        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        TestDataHelper.CreateLegacyJsonCache(cacheDirectory, includeLowLevelContent: true, count: 30);

        try
        {
            using var service = new FileIndexService();
            var loaded = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);

            Assert.IsTrue(loaded, "Expected legacy JSON to import into SQLite cache.");
            Assert.AreEqual(30, service.ItemCount, "Expected all legacy items imported.");
            Assert.IsTrue(File.Exists(cachePath), "Expected SQLite cache file to be created.");

            var results = service.Search("legacy", new SearchOptions
            {
                ItemFilter = ItemFilter.File,
                DateFilter = DateFilter.All
            }, limit: 10);
            Assert.AreEqual(10, results.Count);
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(cacheDirectory);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Search_FallsBackWhenFtsTableIsEmpty()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 120, folderCount: 8);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using (var service = new FileIndexService())
            {
                _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
                await service.SaveCacheAsync(cachePath);
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = cachePath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM items_fts;";
                command.ExecuteNonQuery();
            }

            using (var service = new FileIndexService())
            {
                var loaded = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                Assert.IsTrue(loaded);

                var results = service.Search("file 1", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 25);
                Assert.IsTrue(results.Count > 0, "Expected fallback query path to return results without FTS rows.");
            }
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }
}
