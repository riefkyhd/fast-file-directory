using System.IO;
using System.Text.Json;

namespace FastFileExplorer.Services;

public sealed class AppSettings
{
    public bool IncludeLowLevelContent { get; init; }
    public string CachePath { get; init; } = string.Empty;
    public bool ResumeIncompleteIndex { get; init; }
}

public static class SettingsService
{
    private const string FileName = "settings.json";
    private const string CacheDbFileName = "index-cache-v2.db";
    private const string LegacyCacheFileName = "index-cache-v1.json";

    public static AppSettings Load()
    {
        var path = GetSettingsPath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.CachePath))
                {
                    return new AppSettings
                    {
                        IncludeLowLevelContent = settings.IncludeLowLevelContent,
                        CachePath = NormalizeCachePath(settings.CachePath),
                        ResumeIncompleteIndex = settings.ResumeIncompleteIndex
                    };
                }
            }
        }
        catch
        {
            // Ignore settings corruption; fall back to defaults.
        }

        return new AppSettings
        {
            IncludeLowLevelContent = false,
            CachePath = GetDefaultCachePath(),
            ResumeIncompleteIndex = false
        };
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignore settings save failures.
        }
    }

    public static string GetDefaultCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastFileExplorer",
            CacheDbFileName);
    }

    public static string GetDefaultLegacyJsonPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastFileExplorer",
            LegacyCacheFileName);
    }

    public static string GetLegacyCachePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FastFileExplorer",
            LegacyCacheFileName);
    }

    public static void MigrateLegacyCacheIfNeeded(string cachePath)
    {
        try
        {
            if (File.Exists(cachePath))
            {
                return;
            }

            var localLegacyPath = GetDefaultLegacyJsonPath();
            if (File.Exists(localLegacyPath))
            {
                return;
            }

            var legacyPath = GetLegacyCachePath();
            if (!File.Exists(legacyPath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(legacyPath, localLegacyPath, overwrite: true);
        }
        catch
        {
            // Ignore migration failures.
        }
    }

    private static string NormalizeCachePath(string cachePath)
    {
        var defaultPath = GetDefaultCachePath();
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return defaultPath;
        }

        try
        {
            var legacyPath = GetLegacyCachePath();
            var defaultLegacyPath = GetDefaultLegacyJsonPath();
            if (string.Equals(cachePath, defaultLegacyPath, StringComparison.OrdinalIgnoreCase))
            {
                return defaultPath;
            }

            if (string.Equals(cachePath, legacyPath, StringComparison.OrdinalIgnoreCase))
            {
                return defaultPath;
            }

            var legacyRoot = Path.GetDirectoryName(legacyPath);
            if (!string.IsNullOrWhiteSpace(legacyRoot) &&
                cachePath.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase))
            {
                return defaultPath;
            }
        }
        catch
        {
            // If path parsing fails, fall back to the provided value.
        }

        return cachePath;
    }

    private static string GetSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastFileExplorer",
            FileName);
    }
}
