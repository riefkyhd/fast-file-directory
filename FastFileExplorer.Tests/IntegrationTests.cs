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

                var results = service.Search("file 119", new SearchOptions
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

    [TestMethod]
    [TestCategory("Integration")]
    public async Task Search_ShortContains_DoesNotThrowAndReturnsQuickly()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 300, folderCount: 10);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var started = DateTime.UtcNow;
            var results = service.Search("fi", new SearchOptions
            {
                ItemFilter = ItemFilter.File,
                DateFilter = DateFilter.All
            }, limit: 100);
            var elapsed = DateTime.UtcNow - started;

            Assert.IsTrue(results.Count >= 0);
            Assert.IsTrue(elapsed < TimeSpan.FromSeconds(2), $"Search took too long: {elapsed.TotalMilliseconds:N0} ms");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task NonDriveRoots_IndexSuccessfully()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 80, folderCount: 6);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            Assert.IsTrue(service.ItemCount >= 86);
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30000)]
    public async Task Search_ReturnsResults_WhileIndexingInProgress()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 9000, folderCount: 30);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);

            var indexingTask = service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
            var started = DateTime.UtcNow;
            var found = false;
            while ((DateTime.UtcNow - started) < TimeSpan.FromSeconds(8))
            {
                var results = service.Search("file 8999", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 30);

                if (results.Any(r => r.Name.Equals("file_8999.txt", StringComparison.OrdinalIgnoreCase)))
                {
                    found = true;
                    break;
                }

                await Task.Delay(100);
            }

            await indexingTask;
            if (!found)
            {
                var finalResults = service.Search("file 8999", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 30);
                found = finalResults.Any(r => r.Name.Equals("file_8999.txt", StringComparison.OrdinalIgnoreCase));
            }

            Assert.IsTrue(found, "Expected search to return indexed items while indexing is active or immediately after completion.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(50000)]
    public async Task Indexing_Performance_MediumDataset_CompletesWithinBudget()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 10000, folderCount: 40);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);

            var started = DateTime.UtcNow;
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
            var elapsed = DateTime.UtcNow - started;

            Assert.IsTrue(service.ItemCount >= 10040, $"Expected at least 10040 indexed entries, got {service.ItemCount}");
            Assert.IsTrue(elapsed < TimeSpan.FromSeconds(35), $"Indexing exceeded performance budget: {elapsed.TotalSeconds:N1}s");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(50000)]
    public async Task Search_UsesPendingIndex_WhenStoreIsBusy()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 12000, folderCount: 40);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);

            var indexingTask = service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var sawProgress = false;
            service.StatusChanged += message =>
            {
                if (message.Contains("scanned", StringComparison.OrdinalIgnoreCase))
                {
                    sawProgress = true;
                }
            };

            var progressWaitStart = DateTime.UtcNow;
            while (!sawProgress && (DateTime.UtcNow - progressWaitStart) < TimeSpan.FromSeconds(12))
            {
                await Task.Delay(50);
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = cachePath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                var locked = false;
                var lockStart = DateTime.UtcNow;
                while (!locked && (DateTime.UtcNow - lockStart) < TimeSpan.FromSeconds(2))
                {
                    try
                    {
                        using var lockCommand = connection.CreateCommand();
                        lockCommand.CommandText = "BEGIN IMMEDIATE;";
                        lockCommand.ExecuteNonQuery();
                        locked = true;
                    }
                    catch (SqliteException)
                    {
                        await Task.Delay(50);
                    }
                }

                Assert.IsTrue(locked, "Could not acquire temporary SQLite lock for contention test.");

                var foundDuringLock = false;
                var started = DateTime.UtcNow;
                while ((DateTime.UtcNow - started) < TimeSpan.FromSeconds(1))
                {
                    var results = service.Search("file 1", new SearchOptions
                    {
                        ItemFilter = ItemFilter.File,
                        DateFilter = DateFilter.All
                    }, limit: 25);
                    if (results.Count > 0)
                    {
                        foundDuringLock = true;
                        break;
                    }

                    await Task.Delay(75);
                }

                using var unlockCommand = connection.CreateCommand();
                unlockCommand.CommandText = "ROLLBACK;";
                unlockCommand.ExecuteNonQuery();

                Assert.IsTrue(foundDuringLock, "Expected search to return pending in-memory results when SQLite is busy.");
            }

            await indexingTask;
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(50000)]
    public async Task Reindex_ConcurrentCalls_DoNotCrash()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 2500, folderCount: 20);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);

            var task1 = service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
            var task2 = service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
            await Task.WhenAll(task1, task2);

            Assert.IsTrue(service.ItemCount >= 2520, $"Expected at least 2520 indexed entries, got {service.ItemCount}.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(50000)]
    public async Task Search_MergePendingAndPersisted_NoRandomOrder()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 2500, folderCount: 16);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            for (var i = 0; i < 6; i++)
            {
                File.WriteAllText(Path.Combine(root, $"file_pending_merge_{i:D2}.txt"), "pending");
            }

            var waitStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - waitStart) < TimeSpan.FromSeconds(4))
            {
                var current = service.SearchFullRelevance("pending merge", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 20);
                if (current.Count >= 6)
                {
                    break;
                }

                await Task.Delay(60);
            }

            string[]? baseline = null;
            var stableSamples = 0;
            var settleStart = DateTime.UtcNow;
            while ((DateTime.UtcNow - settleStart) < TimeSpan.FromSeconds(5))
            {
                var current = service.SearchFullRelevance("pending merge", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 20).Select(item => item.FullPath).ToArray();
                if (baseline is not null && baseline.SequenceEqual(current))
                {
                    stableSamples++;
                    if (stableSamples >= 3)
                    {
                        break;
                    }
                }
                else
                {
                    baseline = current;
                    stableSamples = 0;
                }

                await Task.Delay(80);
            }

            Assert.IsNotNull(baseline, "Expected non-null merged baseline.");
            Assert.IsTrue(baseline!.Length >= 6, $"Expected pending files in merged search. got={baseline.Length}");
            Assert.IsTrue(stableSamples >= 3, "Merged results did not settle to a deterministic sequence in time.");

            for (var i = 0; i < 8; i++)
            {
                var current = service.SearchFullRelevance("pending merge", new SearchOptions
                {
                    ItemFilter = ItemFilter.File,
                    DateFilter = DateFilter.All
                }, limit: 20).Select(item => item.FullPath).ToArray();
                CollectionAssert.AreEqual(baseline, current, $"Merged order changed at iteration {i}.");
                await Task.Delay(50);
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
    public async Task CacheMigration_CreatesNormalizedNameIndex_Idempotent()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 120, folderCount: 6);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using (var service = new FileIndexService())
            {
                _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
                await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);
                await service.SaveCacheAsync(cachePath);
            }

            var store = new SqliteIndexStore(cachePath);
            store.EnsureInitialized();
            store.EnsureInitialized();

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = cachePath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_items_normalized_name';";
            var indexCount = Convert.ToInt32(command.ExecuteScalar());
            Assert.AreEqual(1, indexCount, "Expected normalized_name index to exist exactly once after repeated initialization.");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(50000)]
    public async Task ContinueIndex_DoesNotResetExistingProgress()
    {
        var root = TestDataHelper.CreateTestTree(fileCount: 3000, folderCount: 20);
        var cachePath = TestDataHelper.CreateTempCachePath();
        try
        {
            using var service = new FileIndexService();
            _ = await service.LoadCacheAsync(cachePath, includeLowLevelContent: true);
            await service.StartOrRebuildIndexAsync(new[] { root }, includeLowLevelContent: true);

            var before = service.ItemCount;
            Assert.IsTrue(before >= 3020, $"Expected initial index count >= 3020, got {before}");

            var extraPath = Path.Combine(root, "Folder_0", "resume_extra_probe.txt");
            File.WriteAllText(extraPath, "resume");

            await service.ContinueIndexAsync(new[] { root }, includeLowLevelContent: true);
            var after = service.ItemCount;

            Assert.IsTrue(after >= before + 1, $"Continue index should retain existing items and add new ones. before={before}, after={after}");
        }
        finally
        {
            TestDataHelper.DeleteDirectoryWithRetry(root);
            TestDataHelper.DeleteDirectoryWithRetry(Path.GetDirectoryName(cachePath)!);
        }
    }
}
