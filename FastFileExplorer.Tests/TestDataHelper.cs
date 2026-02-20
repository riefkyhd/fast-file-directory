using System.Text.Json;
using FastFileExplorer.Models;
using Microsoft.Data.Sqlite;

namespace FastFileExplorer.Tests;

internal static class TestDataHelper
{
    public static string CreateTestTree(int fileCount, int folderCount)
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        for (var i = 0; i < folderCount; i++)
        {
            Directory.CreateDirectory(Path.Combine(root, "Folder_" + i));
        }

        for (var i = 0; i < fileCount; i++)
        {
            var folder = Path.Combine(root, "Folder_" + (i % Math.Max(1, folderCount)));
            var path = Path.Combine(folder, $"file_{i}.txt");
            File.WriteAllText(path, "test");
        }

        return root;
    }

    public static string CreateTempCachePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "FastFileExplorerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "index-cache-v2.db");
    }

    public static string CreateLegacyJsonCache(string directory, bool includeLowLevelContent, int count)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "index-cache-v1.json");

        var now = DateTime.UtcNow;
        var items = Enumerable.Range(1, count).Select(i => new
        {
            FullPath = Path.Combine(directory, $"legacy_{i}.txt"),
            Name = $"legacy_{i}.txt",
            Directory = directory,
            Extension = "txt",
            LastWriteTimeUtc = now,
            SizeBytes = 32L,
            Kind = IndexedItemKind.File
        }).ToList();

        var payload = new
        {
            Version = 1,
            CreatedUtc = now,
            IncludeLowLevelContent = includeLowLevelContent,
            Items = items
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    public static void DeleteDirectoryWithRetry(string path, int retries = 40, int delayMs = 100)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Exception? lastError = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(delayMs);
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }
    }
}
