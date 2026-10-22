using Microsoft.Data.Sqlite;
using SmartInspectConsole.Core.Enums;
using SmartInspectConsole.Core.FileIO;
using SmartInspectConsole.Core.Packets;
using SmartInspectConsole.Core.Parsing;
using SmartInspectTui.Configuration;

namespace SmartInspectTui.Services;

// Access is serialized by LogIngestionService. No collection grows with capture duration.
internal sealed class SqliteLogStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly BinaryPacketWriter _writer = new();
    private readonly BinaryPacketReader _reader = new();
    private readonly string _directory;
    private readonly FileStream _lease;
    private long _nextId;
    private readonly long _maxDatabaseBytes;
    private readonly Dictionary<long, int> _matchCounts = new();
    public string DatabasePath { get; }
    public int Count { get; private set; }
    public long EvictedCount { get; private set; }

    public SqliteLogStore(TuiSettings settings)
    {
        var root =
            settings.StorageDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "smartinspect-tui",
                "captures"
            );
        Directory.CreateDirectory(root);
        // Remove abandoned session caches, but never a cache held by another running instance.
        foreach (var directory in Directory.EnumerateDirectories(root, "session-*"))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddHours(-1))
                    continue;
                using (
                    new FileStream(
                        Path.Combine(directory, "lease"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None
                    )
                ) { }
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _directory = Path.Combine(root, $"session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _lease = new FileStream(
            Path.Combine(_directory, "lease"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );
        DatabasePath = Path.Combine(_directory, "history.sqlite");
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Pooling = false,
            }.ToString()
        );
        try
        {
            _connection.Open();
            // DELETE journal bounds transient disk use by one write transaction. Keep half
            // the quota available for the rollback journal, FTS maintenance, and headroom.
            var pages = Math.Max(
                256,
                Math.Clamp(settings.MaxDiskBytes, 2L * 1024 * 1024, 1L << 40) / 2 / 4096
            );
            _maxDatabaseBytes = pages * 4096;
            Execute(
                $"""
                PRAGMA page_size=4096;
                PRAGMA auto_vacuum=FULL;
                PRAGMA journal_mode=DELETE;
                PRAGMA synchronous=NORMAL;
                PRAGMA cache_size=-8192;
                PRAGMA mmap_size=0;
                PRAGMA temp_store=FILE;
                PRAGMA foreign_keys=ON;
                PRAGMA max_page_count={pages};
                CREATE TABLE logs (
                    id INTEGER PRIMARY KEY, packet BLOB NOT NULL, timestamp INTEGER NOT NULL,
                    elapsed INTEGER NOT NULL, level INTEGER NOT NULL, app TEXT NOT NULL,
                    session TEXT NOT NULL, title TEXT NOT NULL, host TEXT NOT NULL,
                    pid INTEGER NOT NULL, tid INTEGER NOT NULL, ascii_title INTEGER NOT NULL
                );
                CREATE TABLE matches (
                    generation INTEGER NOT NULL, id INTEGER NOT NULL REFERENCES logs(id) ON DELETE CASCADE,
                    PRIMARY KEY(generation,id)
                ) WITHOUT ROWID;
                CREATE VIRTUAL TABLE search USING fts5(title, tokenize='trigram');
                CREATE TRIGGER logs_delete AFTER DELETE ON logs BEGIN
                    DELETE FROM search WHERE rowid=old.id;
                END;
                """
            );
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal sealed record Write(
        LogEntry Entry,
        long Generation,
        bool Matches,
        long SearchGeneration = 0,
        bool SearchMatches = false
    );

    public void AddBatch(IReadOnlyList<Write> entries, int maxEntries)
    {
        if (entries.Count == 0)
            return;
        // Reject an oversized entry before rotating existing history to make room for it.
        if (
            entries.Any(item =>
                Math.Max(
                    item.Entry.EstimatedMemoryBytes,
                    4096L
                        + (item.Entry.Data?.LongLength ?? 0)
                        + 64L * item.Entry.Title.Length
                        + 8L
                            * (
                                item.Entry.AppName.Length
                                + item.Entry.SessionName.Length
                                + item.Entry.HostName.Length
                            )
                )
                > _maxDatabaseBytes / 4
            )
        )
            throw new IOException(
                "Entry exceeds the per-entry limit for this disk quota; increase --disk-mb."
            );
        while (true)
        {
            try
            {
                using var transaction = _connection.BeginTransaction();
                foreach (var item in entries)
                    Insert(item, transaction);
                transaction.Commit();
                foreach (var item in entries)
                {
                    if (item.Matches && item.Generation != 0)
                        _matchCounts[item.Generation] =
                            _matchCounts.GetValueOrDefault(item.Generation) + 1;
                    if (
                        item.SearchMatches
                        && item.SearchGeneration != 0
                        && item.SearchGeneration != item.Generation
                    )
                        _matchCounts[item.SearchGeneration] =
                            _matchCounts.GetValueOrDefault(item.SearchGeneration) + 1;
                }
                Count += entries.Count;
                break;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 13 && Count > 0)
            {
                Evict(Math.Max(1, Count / 10));
            }
        }
        if (Count > Math.Max(1, maxEntries))
            Evict(Count - Math.Max(1, (int)(Math.Max(1, maxEntries) * 0.9)));
    }

    private void Insert(Write item, SqliteTransaction transaction)
    {
        var entry = item.Entry;
        var id = ++_nextId;
        using var cmd = Command(
            """
            INSERT INTO logs VALUES($id,$packet,$time,$elapsed,$level,$app,$session,
                $title,$host,$pid,$tid,$ascii);
            INSERT INTO search(rowid,title) VALUES($id,$fullTitle);
            """
        );
        cmd.Transaction = transaction;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$packet", _writer.SerializePayload(entry));
        cmd.Parameters.AddWithValue("$time", entry.Timestamp.ToBinary());
        cmd.Parameters.AddWithValue("$elapsed", entry.ElapsedTime.Ticks);
        cmd.Parameters.AddWithValue("$level", (int)entry.LogEntryType);
        cmd.Parameters.AddWithValue("$app", Preview(entry.AppName));
        cmd.Parameters.AddWithValue("$session", Preview(entry.SessionName));
        cmd.Parameters.AddWithValue("$title", Preview(entry.SingleLineTitle));
        cmd.Parameters.AddWithValue("$host", Preview(entry.HostName));
        cmd.Parameters.AddWithValue("$pid", entry.ProcessId);
        cmd.Parameters.AddWithValue("$tid", entry.ThreadId);
        cmd.Parameters.AddWithValue("$ascii", entry.Title.All(c => c < 128) ? 1 : 0);
        cmd.Parameters.AddWithValue("$fullTitle", entry.Title);
        cmd.ExecuteNonQuery();
        if (item.Matches && item.Generation != 0)
            InsertMatch(item.Generation, id, transaction);
        if (
            item.SearchMatches
            && item.SearchGeneration != 0
            && item.SearchGeneration != item.Generation
        )
            InsertMatch(item.SearchGeneration, id, transaction);
        entry.SequenceId = id;
    }

    private int InsertMatch(long generation, long id, SqliteTransaction? transaction = null)
    {
        using var cmd = Command(
            "INSERT OR IGNORE INTO matches SELECT $g,id FROM logs WHERE id=$id"
        );
        cmd.Transaction = transaction;
        cmd.Parameters.AddWithValue("$g", generation);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }

    private static string Preview(string value) => value.Length > 512 ? value[..512] : value;

    public long LastId => _nextId;

    public void Clear()
    {
        Execute("DELETE FROM logs; DELETE FROM matches;");
        Count = 0;
        _matchCounts.Clear();
    }

    private void Evict(int count)
    {
        using var cmd = Command(
            "DELETE FROM logs WHERE id IN (SELECT id FROM logs ORDER BY id LIMIT $n)"
        );
        cmd.Parameters.AddWithValue("$n", count);
        var removed = cmd.ExecuteNonQuery();
        Count -= removed;
        EvictedCount += removed;
        foreach (var generation in _matchCounts.Keys.ToArray())
            _matchCounts[generation] = CountMatches(generation, long.MaxValue);
    }

    public void PruneGenerations(long visible, long current)
    {
        using var cmd = Command(
            "DELETE FROM matches WHERE generation<>$visible AND generation<>$current"
        );
        cmd.Parameters.AddWithValue("$visible", visible);
        cmd.Parameters.AddWithValue("$current", current);
        cmd.ExecuteNonQuery();
        foreach (
            var generation in _matchCounts.Keys.Where(g => g != visible && g != current).ToArray()
        )
            _matchCounts.Remove(generation);
    }

    public void AddMatches(long generation, IReadOnlyList<long> ids)
    {
        using var transaction = _connection.BeginTransaction();
        var added = 0;
        foreach (var id in ids)
            added += InsertMatch(generation, id, transaction);
        transaction.Commit();
        _matchCounts[generation] = _matchCounts.GetValueOrDefault(generation) + added;
    }

    public IReadOnlyList<LogEntry> ScanPage(long after, long upper, string? titleMatch)
    {
        using var cmd = Command(
            "SELECT l.id,l.packet,l.timestamp,l.elapsed FROM logs l "
                + "WHERE l.id>$after AND l.id<=$upper "
                + (
                    titleMatch == null
                        ? ""
                        : "AND (l.ascii_title=0 OR l.id IN (SELECT rowid FROM search WHERE search MATCH $fts)) "
                )
                + "ORDER BY l.id LIMIT 128"
        );
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$upper", upper);
        cmd.Parameters.AddWithValue("$fts", titleMatch ?? "");
        var result = new List<LogEntry>();
        long bytes = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = (LogEntry)_reader.ParsePacket(PacketType.LogEntry, (byte[])reader[1])!;
            entry.SequenceId = reader.GetInt64(0);
            entry.Timestamp = DateTime.FromBinary(reader.GetInt64(2));
            entry.ElapsedTime = TimeSpan.FromTicks(reader.GetInt64(3));
            result.Add(entry);
            bytes += entry.EstimatedMemoryBytes;
            if (bytes >= 1024 * 1024)
                break;
        }
        return result;
    }

    public int MatchCount(long generation, long upperId) =>
        upperId >= LastId
            ? generation == 0
                ? Count
                : _matchCounts.GetValueOrDefault(generation)
            : CountMatches(generation, upperId);

    private int CountMatches(long generation, long upperId) =>
        (int)Scalar(
            $"SELECT count(*) FROM {(generation == 0 ? "logs" : "matches")} WHERE id<=$upper"
                + (generation == 0 ? "" : " AND generation=$g"),
            generation,
            upperId
        );

    public IReadOnlyList<LogEntry> ReadPage(
        long generation,
        long upperId,
        int offset,
        int limit,
        bool tail = false,
        long anchorId = 0
    )
    {
        using var cmd = Command(
            """
            SELECT l.id,l.timestamp,l.elapsed,l.level,l.app,l.session,l.title,l.host,l.pid,l.tid
            FROM logs l
            """
                + (generation == 0 ? "" : " JOIN matches m ON m.id=l.id AND m.generation=$g ")
                + " WHERE l.id<=$upper AND l.id>=$anchor ORDER BY l.id "
                + (tail ? "DESC" : "ASC")
                + " LIMIT $limit OFFSET $offset"
        );
        cmd.Parameters.AddWithValue("$g", generation);
        cmd.Parameters.AddWithValue("$upper", upperId);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", tail || anchorId > 0 ? 0 : offset);
        cmd.Parameters.AddWithValue("$anchor", tail ? 0 : anchorId);
        using var reader = cmd.ExecuteReader();
        var entries = new List<LogEntry>();
        while (reader.Read())
            entries.Add(
                new LogEntry
                {
                    SequenceId = reader.GetInt64(0),
                    Timestamp = DateTime.FromBinary(reader.GetInt64(1)),
                    ElapsedTime = TimeSpan.FromTicks(reader.GetInt64(2)),
                    LogEntryType = (LogEntryType)reader.GetInt32(3),
                    AppName = reader.GetString(4),
                    SessionName = reader.GetString(5),
                    Title = reader.GetString(6),
                    HostName = reader.GetString(7),
                    ProcessId = reader.GetInt32(8),
                    ThreadId = reader.GetInt32(9),
                }
            );
        if (tail)
            entries.Reverse();
        return entries;
    }

    public LogEntry? ReadEntry(long id)
    {
        using var cmd = Command("SELECT packet,timestamp,elapsed FROM logs WHERE id=$id");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var entry = (LogEntry)_reader.ParsePacket(PacketType.LogEntry, (byte[])reader[0])!;
        entry.SequenceId = id;
        entry.Timestamp = DateTime.FromBinary(reader.GetInt64(1));
        entry.ElapsedTime = TimeSpan.FromTicks(reader.GetInt64(2));
        return entry;
    }

    // Keyset scans hold only one payload at a time, never a whole history or result set.
    public long NextId(long after, long upper)
    {
        using var cmd = Command(
            "SELECT coalesce(min(id),0) FROM logs WHERE id>$after AND id<=$upper"
        );
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$upper", upper);
        return (long)cmd.ExecuteScalar()!;
    }

    public int OffsetOf(long id, long generation, long upper) =>
        (int)Scalar(
            $"SELECT count(*) FROM {(generation == 0 ? "logs" : "matches")} WHERE id<$id AND id<=$upper"
                + (generation == 0 ? "" : " AND generation=$g"),
            generation,
            upper,
            id
        );

    private long Scalar(string sql, long generation = 0, long upper = long.MaxValue, long id = 0)
    {
        using var cmd = Command(sql);
        cmd.Parameters.AddWithValue("$g", generation);
        cmd.Parameters.AddWithValue("$upper", upper);
        cmd.Parameters.AddWithValue("$id", id);
        return (long)cmd.ExecuteScalar()!;
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _lease.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
