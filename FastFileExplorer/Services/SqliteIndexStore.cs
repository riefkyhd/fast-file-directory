using System.IO;
using Microsoft.Data.Sqlite;
using FastFileExplorer.Models;

namespace FastFileExplorer.Services;

public sealed class SqliteIndexStore
{
    public sealed record StoreMetadata(bool IncludeLowLevelContent, int ItemCount);

    private readonly string _dbPath;
    private readonly string _connectionString;
    private int _ftsHasRows = -1;

    public SqliteIndexStore(string dbPath)
    {
        _dbPath = dbPath;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public bool Exists()
    {
        return File.Exists(_dbPath);
    }

    public void EnsureInitialized()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS items (
                full_path TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                directory TEXT NOT NULL,
                extension TEXT NOT NULL,
                last_write_ticks INTEGER NOT NULL,
                size_bytes INTEGER NOT NULL,
                kind INTEGER NOT NULL,
                normalized_name TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS items_fts USING fts5(
                full_path UNINDEXED,
                normalized_name,
                tokenize='unicode61'
            );
            CREATE INDEX IF NOT EXISTS idx_items_name ON items(name);
            CREATE INDEX IF NOT EXISTS idx_items_directory ON items(directory);
            CREATE INDEX IF NOT EXISTS idx_items_kind ON items(kind);
            CREATE INDEX IF NOT EXISTS idx_items_last_write_ticks ON items(last_write_ticks);
            CREATE INDEX IF NOT EXISTS idx_items_normalized_name ON items(normalized_name);
            CREATE INDEX IF NOT EXISTS idx_items_norm_name_sort ON items(normalized_name, name, last_write_ticks);
            """;
        command.ExecuteNonQuery();
    }

    public int GetItemCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public StoreMetadata? TryReadMetadata()
    {
        using var connection = OpenConnection();
        using var includeCommand = connection.CreateCommand();
        includeCommand.CommandText = "SELECT value FROM metadata WHERE key = 'include_low_level_content' LIMIT 1;";
        var includeValue = includeCommand.ExecuteScalar() as string;
        if (includeValue is null)
        {
            return null;
        }

        var includeLowLevel = string.Equals(includeValue, "1", StringComparison.OrdinalIgnoreCase);

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM items;";
        var count = Convert.ToInt32(countCommand.ExecuteScalar());
        return new StoreMetadata(includeLowLevel, count);
    }

    public List<IndexedItem> LoadAllItems()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT full_path, name, directory, extension, last_write_ticks, size_bytes, kind, normalized_name
            FROM items;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<IndexedItem>(capacity: 64_000);
        while (reader.Read())
        {
            items.Add(new IndexedItem
            {
                FullPath = reader.GetString(0),
                Name = reader.GetString(1),
                Directory = reader.GetString(2),
                Extension = reader.GetString(3),
                LastWriteTimeUtc = new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                SizeBytes = reader.GetInt64(5),
                Kind = (IndexedItemKind)reader.GetInt32(6),
                NormalizedName = reader.GetString(7)
            });
        }

        return items;
    }

    public void ResetIndex(bool includeLowLevelContent)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM items;
            DELETE FROM items_fts;
            INSERT INTO metadata(key, value) VALUES ('include_low_level_content', $include)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO metadata(key, value) VALUES ('updated_utc', $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$include", includeLowLevelContent ? "1" : "0");
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
        Interlocked.Exchange(ref _ftsHasRows, 0);
    }

    public void ApplyChanges(IReadOnlyCollection<IndexedItem> upserts, IReadOnlyCollection<string> deletes, bool includeLowLevelContent)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (upserts.Count > 0)
        {
            using var upsertCommand = connection.CreateCommand();
            upsertCommand.Transaction = transaction;
            upsertCommand.CommandText = """
                INSERT INTO items(full_path, name, directory, extension, last_write_ticks, size_bytes, kind, normalized_name)
                VALUES ($full_path, $name, $directory, $extension, $last_write_ticks, $size_bytes, $kind, $normalized_name)
                ON CONFLICT(full_path) DO UPDATE SET
                    name = excluded.name,
                    directory = excluded.directory,
                    extension = excluded.extension,
                    last_write_ticks = excluded.last_write_ticks,
                    size_bytes = excluded.size_bytes,
                    kind = excluded.kind,
                    normalized_name = excluded.normalized_name;
                """;
            var fullPath = upsertCommand.Parameters.Add("$full_path", SqliteType.Text);
            var name = upsertCommand.Parameters.Add("$name", SqliteType.Text);
            var directory = upsertCommand.Parameters.Add("$directory", SqliteType.Text);
            var extension = upsertCommand.Parameters.Add("$extension", SqliteType.Text);
            var lastWriteTicks = upsertCommand.Parameters.Add("$last_write_ticks", SqliteType.Integer);
            var sizeBytes = upsertCommand.Parameters.Add("$size_bytes", SqliteType.Integer);
            var kind = upsertCommand.Parameters.Add("$kind", SqliteType.Integer);
            var normalizedName = upsertCommand.Parameters.Add("$normalized_name", SqliteType.Text);

            foreach (var item in upserts)
            {
                fullPath.Value = item.FullPath;
                name.Value = item.Name;
                directory.Value = item.Directory;
                extension.Value = item.Extension;
                lastWriteTicks.Value = item.LastWriteTimeUtc.Ticks;
                sizeBytes.Value = item.SizeBytes;
                kind.Value = (int)item.Kind;
                normalizedName.Value = item.NormalizedName;
                upsertCommand.ExecuteNonQuery();
            }

            using var ftsDeleteCommand = connection.CreateCommand();
            ftsDeleteCommand.Transaction = transaction;
            ftsDeleteCommand.CommandText = "DELETE FROM items_fts WHERE full_path = $full_path;";
            var ftsDeletePath = ftsDeleteCommand.Parameters.Add("$full_path", SqliteType.Text);

            using var ftsInsertCommand = connection.CreateCommand();
            ftsInsertCommand.Transaction = transaction;
            ftsInsertCommand.CommandText = """
                INSERT INTO items_fts(full_path, normalized_name)
                VALUES ($full_path, $normalized_name);
                """;
            var ftsPath = ftsInsertCommand.Parameters.Add("$full_path", SqliteType.Text);
            var ftsNormalized = ftsInsertCommand.Parameters.Add("$normalized_name", SqliteType.Text);

            foreach (var item in upserts)
            {
                ftsDeletePath.Value = item.FullPath;
                ftsDeleteCommand.ExecuteNonQuery();

                ftsPath.Value = item.FullPath;
                ftsNormalized.Value = item.NormalizedName;
                ftsInsertCommand.ExecuteNonQuery();
            }

            Interlocked.Exchange(ref _ftsHasRows, 1);
        }

        if (deletes.Count > 0)
        {
            using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM items WHERE full_path = $full_path;";
            var fullPath = deleteCommand.Parameters.Add("$full_path", SqliteType.Text);
            foreach (var path in deletes)
            {
                fullPath.Value = path;
                deleteCommand.ExecuteNonQuery();
            }

            using var ftsDeleteCommand = connection.CreateCommand();
            ftsDeleteCommand.Transaction = transaction;
            ftsDeleteCommand.CommandText = "DELETE FROM items_fts WHERE full_path = $full_path;";
            var ftsPath = ftsDeleteCommand.Parameters.Add("$full_path", SqliteType.Text);
            foreach (var path in deletes)
            {
                ftsPath.Value = path;
                ftsDeleteCommand.ExecuteNonQuery();
            }
        }

        using var metadataCommand = connection.CreateCommand();
        metadataCommand.Transaction = transaction;
        metadataCommand.CommandText = """
            INSERT INTO metadata(key, value) VALUES ('include_low_level_content', $include)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO metadata(key, value) VALUES ('updated_utc', $updated)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        metadataCommand.Parameters.AddWithValue("$include", includeLowLevelContent ? "1" : "0");
        metadataCommand.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        metadataCommand.ExecuteNonQuery();

        transaction.Commit();
    }

    public List<IndexedItem> SearchFastCandidates(string query, SearchOptions options, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var whereParts = new List<string>();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTerm)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        if (terms.Length == 0)
        {
            return [];
        }

        var first = terms[0];
        var shortSingleTerm = terms.Length == 1 && first.Length <= 2;
        command.Parameters.AddWithValue("$limit", limit);

        if (shortSingleTerm)
        {
            command.Parameters.AddWithValue("$prefix", $"{first}%");
            whereParts.Add("i.normalized_name LIKE $prefix");
            ApplyFilters(options, whereParts, command);
            var whereSql = whereParts.Count == 0
                ? string.Empty
                : $"WHERE {string.Join(" AND ", whereParts)}";
            command.CommandText = $"""
                SELECT i.full_path, i.name, i.directory, i.extension, i.last_write_ticks, i.size_bytes, i.kind, i.normalized_name
                FROM items i
                {whereSql}
                LIMIT $limit;
                """;
            using var shortReader = command.ExecuteReader();
            return ReadItems(shortReader);
        }

        ApplyFilters(options, whereParts, command);
        var useFts = HasFtsRows(connection);
        if (useFts)
        {
            var whereSql = whereParts.Count == 0
                ? string.Empty
                : $" AND {string.Join(" AND ", whereParts)}";

            var ftsMatch = string.Join(" AND ", terms.Select(term => $"{term}*"));
            command.Parameters.AddWithValue("$match", ftsMatch);
            command.CommandText = $"""
                SELECT i.full_path, i.name, i.directory, i.extension, i.last_write_ticks, i.size_bytes, i.kind, i.normalized_name
                FROM items i
                INNER JOIN items_fts f ON f.full_path = i.full_path
                WHERE f.normalized_name MATCH $match{whereSql}
                LIMIT $limit;
                """;

            using var ftsReader = command.ExecuteReader();
            var ftsResults = ReadItems(ftsReader);
            if (ftsResults.Count > 0)
            {
                return ftsResults;
            }
        }

        using var fallbackCommand = connection.CreateCommand();
        fallbackCommand.Parameters.AddWithValue("$limit", limit);
        var fallbackWhereParts = new List<string>();
        ApplyFilters(options, fallbackWhereParts, fallbackCommand);

        for (var i = 0; i < terms.Length; i++)
        {
            var paramName = $"$term{i}";
            fallbackWhereParts.Add($"i.normalized_name LIKE {paramName}");
            fallbackCommand.Parameters.AddWithValue(paramName, $"%{terms[i]}%");
        }

        var fallbackWhereSql = fallbackWhereParts.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", fallbackWhereParts)}";

        fallbackCommand.CommandText = $"""
            SELECT i.full_path, i.name, i.directory, i.extension, i.last_write_ticks, i.size_bytes, i.kind, i.normalized_name
            FROM items i
            {fallbackWhereSql}
            LIMIT $limit;
            """;

        using var fallbackReader = fallbackCommand.ExecuteReader();
        return ReadItems(fallbackReader);
    }

    public List<IndexedItem> SearchFullRelevance(string query, SearchOptions options, int limit, bool allowContainsFallback = true)
    {
        if (limit <= 0)
        {
            return [];
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var whereParts = new List<string>();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeTerm)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToArray();

        if (terms.Length == 0)
        {
            return [];
        }

        command.Parameters.AddWithValue("$limit", limit);
        var first = terms[0];
        command.Parameters.AddWithValue("$prefix", $"{first}%");
        command.Parameters.AddWithValue("$wordprefix", $"% {first}%");
        ApplyFilters(options, whereParts, command);

        if (HasFtsRows(connection))
        {
            var whereSql = whereParts.Count == 0
                ? string.Empty
                : $" AND {string.Join(" AND ", whereParts)}";
            var ftsMatch = string.Join(" AND ", terms.Select(term => $"{term}*"));
            command.Parameters.AddWithValue("$match", ftsMatch);
            command.CommandText = $"""
                SELECT i.full_path, i.name, i.directory, i.extension, i.last_write_ticks, i.size_bytes, i.kind, i.normalized_name
                FROM items i
                INNER JOIN items_fts f ON f.full_path = i.full_path
                WHERE f.normalized_name MATCH $match{whereSql}
                ORDER BY
                    CASE
                        WHEN i.normalized_name LIKE $prefix THEN 0
                        WHEN i.normalized_name LIKE $wordprefix THEN 1
                        ELSE 2
                    END,
                    CASE WHEN i.kind = 1 THEN 0 ELSE 1 END,
                    i.name COLLATE NOCASE,
                    i.last_write_ticks DESC,
                    i.full_path COLLATE NOCASE
                LIMIT $limit;
                """;

            using var ftsReader = command.ExecuteReader();
            var ftsResults = ReadItems(ftsReader);
            if (ftsResults.Count > 0)
            {
                return ftsResults;
            }
        }

        if (!allowContainsFallback || terms.Any(term => term.Length < 3))
        {
            return [];
        }

        using var fallbackCommand = connection.CreateCommand();
        fallbackCommand.Parameters.AddWithValue("$limit", limit);
        fallbackCommand.Parameters.AddWithValue("$prefix", $"{first}%");
        fallbackCommand.Parameters.AddWithValue("$wordprefix", $"% {first}%");
        var fallbackWhereParts = new List<string>();
        ApplyFilters(options, fallbackWhereParts, fallbackCommand);

        for (var i = 0; i < terms.Length; i++)
        {
            var paramName = $"$term{i}";
            fallbackWhereParts.Add($"i.normalized_name LIKE {paramName}");
            fallbackCommand.Parameters.AddWithValue(paramName, $"%{terms[i]}%");
        }

        var fallbackWhereSql = fallbackWhereParts.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", fallbackWhereParts)}";
        fallbackCommand.CommandText = $"""
            SELECT i.full_path, i.name, i.directory, i.extension, i.last_write_ticks, i.size_bytes, i.kind, i.normalized_name
            FROM items i
            {fallbackWhereSql}
            ORDER BY
                CASE
                    WHEN i.normalized_name LIKE $prefix THEN 0
                    WHEN i.normalized_name LIKE $wordprefix THEN 1
                    ELSE 2
                END,
                CASE WHEN i.kind = 1 THEN 0 ELSE 1 END,
                i.name COLLATE NOCASE,
                i.last_write_ticks DESC,
                i.full_path COLLATE NOCASE
            LIMIT $limit;
            """;

        using var fallbackReader = fallbackCommand.ExecuteReader();
        return ReadItems(fallbackReader);
    }

    public List<IndexedItem> Search(string query, SearchOptions options, int limit, bool allowContainsFallback = true)
    {
        return SearchFullRelevance(query, options, limit, allowContainsFallback);
    }

    private static List<IndexedItem> ReadItems(SqliteDataReader reader)
    {
        var items = new List<IndexedItem>(capacity: 512);
        while (reader.Read())
        {
            items.Add(new IndexedItem
            {
                FullPath = reader.GetString(0),
                Name = reader.GetString(1),
                Directory = reader.GetString(2),
                Extension = reader.GetString(3),
                LastWriteTimeUtc = new DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                SizeBytes = reader.GetInt64(5),
                Kind = (IndexedItemKind)reader.GetInt32(6),
                NormalizedName = reader.GetString(7)
            });
        }

        return items;
    }

    private static string NormalizeTerm(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return new string(chars);
    }

    private static void ApplyFilters(SearchOptions options, List<string> whereParts, SqliteCommand command)
    {
        if (options.ItemFilter != ItemFilter.All)
        {
            switch (options.ItemFilter)
            {
                case ItemFilter.Folder:
                    whereParts.Add("i.kind = 1");
                    break;
                case ItemFilter.File:
                    whereParts.Add("i.kind = 0");
                    break;
                case ItemFilter.Document:
                    whereParts.Add("i.kind = 0 AND i.extension IN ('txt','doc','docx','pdf','rtf','ppt','pptx','xls','xlsx','csv','md')");
                    break;
                case ItemFilter.Picture:
                    whereParts.Add("i.kind = 0 AND i.extension IN ('jpg','jpeg','png','gif','bmp','tiff','webp','heic')");
                    break;
                case ItemFilter.Video:
                    whereParts.Add("i.kind = 0 AND i.extension IN ('mp4','mov','avi','mkv','wmv','flv','webm')");
                    break;
            }
        }

        if (options.DateFilter == DateFilter.All)
        {
            return;
        }

        var minUtc = options.DateFilter switch
        {
            DateFilter.Last1Day => DateTime.UtcNow.AddDays(-1),
            DateFilter.Last7Days => DateTime.UtcNow.AddDays(-7),
            DateFilter.Last30Days => DateTime.UtcNow.AddDays(-30),
            DateFilter.Last365Days => DateTime.UtcNow.AddDays(-365),
            _ => DateTime.MinValue
        };

        if (minUtc == DateTime.MinValue)
        {
            return;
        }

        whereParts.Add("i.last_write_ticks >= $minTicks");
        command.Parameters.AddWithValue("$minTicks", minUtc.Ticks);
    }

    private bool HasFtsRows(SqliteConnection connection)
    {
        var cached = Volatile.Read(ref _ftsHasRows);
        if (cached >= 0)
        {
            return cached == 1;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM items_fts LIMIT 1;";
        var hasRows = command.ExecuteScalar() is not null;
        Interlocked.Exchange(ref _ftsHasRows, hasRows ? 1 : 0);
        return hasRows;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.DefaultTimeout = 3;

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout=2500;
            PRAGMA cache_size=-20000;
            PRAGMA temp_store=MEMORY;
            """;
        command.ExecuteNonQuery();
        return connection;
    }
}
