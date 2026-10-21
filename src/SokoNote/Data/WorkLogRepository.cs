using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SokoNote.Models;

namespace SokoNote.Data;

/// <summary>
/// SQLite storage for topics, journal entries, settings and Claude chat history.
/// Every call opens a short-lived pooled connection, so the repository is safe to use from any thread.
/// </summary>
public sealed class WorkLogRepository
{
    public const int SchemaVersion = 1;
    private const string DateFormat = "yyyy-MM-dd";
    private readonly string _connectionString;

    public string DbPath { get; }

    public WorkLogRepository(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Initialize()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS topics(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                color TEXT NOT NULL DEFAULT '#5B8DEF',
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entries(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                topic_id INTEGER NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                entry_date TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                content BLOB,
                plain_text TEXT NOT NULL DEFAULT '',
                paper_color TEXT NOT NULL DEFAULT '#FFFDF5',
                paper_style INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_entries_date ON entries(entry_date);
            CREATE INDEX IF NOT EXISTS ix_entries_topic_date ON entries(topic_id, entry_date);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS chat_messages(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        SetSetting(c, "schema.version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------ topics

    public List<Topic> GetTopics()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.name, t.color, t.sort_order,
                   (SELECT COUNT(*) FROM entries e WHERE e.topic_id = t.id)
            FROM topics t ORDER BY t.sort_order, t.id
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<Topic>();
        while (r.Read())
        {
            list.Add(new Topic
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Color = r.GetString(2),
                SortOrder = r.GetInt32(3),
                EntryCount = r.GetInt32(4),
            });
        }
        return list;
    }

    public int CountEntries()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public Topic AddTopic(string name, string color)
    {
        name = NormalizeName(name);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO topics(name, color, sort_order, created_at)
            VALUES($n, $c, COALESCE((SELECT MAX(sort_order) FROM topics), 0) + 1, $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", color);
        cmd.Parameters.AddWithValue("$t", Now());
        var id = (long)cmd.ExecuteScalar()!;
        return new Topic { Id = id, Name = name, Color = color };
    }

    public void RenameTopic(long id, string name)
    {
        Exec("UPDATE topics SET name=$n WHERE id=$id", ("$n", NormalizeName(name)), ("$id", id));
    }

    public void SetTopicColor(long id, string color)
    {
        Exec("UPDATE topics SET color=$c WHERE id=$id", ("$c", color), ("$id", id));
    }

    public void DeleteTopic(long id)
    {
        Exec("DELETE FROM topics WHERE id=$id", ("$id", id));
    }

    public void ReorderTopics(IReadOnlyList<long> orderedIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (int i = 0; i < orderedIds.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE topics SET sort_order=$o WHERE id=$id";
            cmd.Parameters.AddWithValue("$o", i + 1);
            cmd.Parameters.AddWithValue("$id", orderedIds[i]);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string NormalizeName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Topic name must not be empty.", nameof(name));
        return name.Length > 100 ? name[..100] : name;
    }

    // ----------------------------------------------------------------- entries

    public List<Entry> QueryEntries(EntryQuery q)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var text = q.IncludeFullText ? "e.plain_text" : "substr(e.plain_text, 1, 200)";
        var sql = $"""
            SELECT e.id, e.topic_id, t.name, t.color, e.entry_date, e.title, {text},
                   e.status, e.paper_color, e.paper_style, e.created_at, e.updated_at
            FROM entries e JOIN topics t ON t.id = e.topic_id
            WHERE 1=1
            """;
        if (q.TopicId is long tid && tid > 0)
        {
            sql += " AND e.topic_id=$tid";
            cmd.Parameters.AddWithValue("$tid", tid);
        }
        if (q.Date is DateTime d)
        {
            sql += " AND e.entry_date=$d";
            cmd.Parameters.AddWithValue("$d", d.ToString(DateFormat, CultureInfo.InvariantCulture));
        }
        if (q.From is DateTime from)
        {
            sql += " AND e.entry_date>=$from";
            cmd.Parameters.AddWithValue("$from", from.ToString(DateFormat, CultureInfo.InvariantCulture));
        }
        if (q.To is DateTime to)
        {
            sql += " AND e.entry_date<=$to";
            cmd.Parameters.AddWithValue("$to", to.ToString(DateFormat, CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            sql += @" AND (e.title LIKE $s ESCAPE '\' OR e.plain_text LIKE $s ESCAPE '\' OR t.name LIKE $s ESCAPE '\')";
            cmd.Parameters.AddWithValue("$s", "%" + EscapeLike(q.Search.Trim()) + "%");
        }
        if (q.Status is EntryStatus st)
        {
            sql += " AND e.status=$st";
            cmd.Parameters.AddWithValue("$st", (int)st);
        }
        sql += " ORDER BY e.entry_date DESC, e.updated_at DESC, e.id DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$lim", Math.Max(1, q.Limit));
        cmd.CommandText = sql;

        using var r = cmd.ExecuteReader();
        var list = new List<Entry>();
        while (r.Read())
        {
            var plain = r.GetString(6);
            list.Add(new Entry
            {
                Id = r.GetInt64(0),
                TopicId = r.GetInt64(1),
                TopicName = r.GetString(2),
                TopicColor = r.GetString(3),
                EntryDate = ParseDate(r.GetString(4)),
                Title = r.GetString(5),
                PlainText = q.IncludeFullText ? plain : "",
                Preview = MakePreview(plain),
                Status = (EntryStatus)r.GetInt32(7),
                PaperColor = r.GetString(8),
                PaperStyle = (PaperStyle)r.GetInt32(9),
                CreatedAt = ParseStamp(r.GetString(10)),
                UpdatedAt = ParseStamp(r.GetString(11)),
            });
        }
        return list;
    }

    public Entry? GetEntry(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT e.id, e.topic_id, t.name, t.color, e.entry_date, e.title, e.plain_text,
                   e.status, e.paper_color, e.paper_style, e.created_at, e.updated_at, e.content
            FROM entries e JOIN topics t ON t.id = e.topic_id WHERE e.id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var plain = r.GetString(6);
        return new Entry
        {
            Id = r.GetInt64(0),
            TopicId = r.GetInt64(1),
            TopicName = r.GetString(2),
            TopicColor = r.GetString(3),
            EntryDate = ParseDate(r.GetString(4)),
            Title = r.GetString(5),
            PlainText = plain,
            Preview = MakePreview(plain),
            Status = (EntryStatus)r.GetInt32(7),
            PaperColor = r.GetString(8),
            PaperStyle = (PaperStyle)r.GetInt32(9),
            CreatedAt = ParseStamp(r.GetString(10)),
            UpdatedAt = ParseStamp(r.GetString(11)),
            Content = r.IsDBNull(12) ? null : (byte[])r.GetValue(12),
        };
    }

    public long InsertEntry(Entry e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries(topic_id, entry_date, title, content, plain_text, paper_color, paper_style, status, created_at, updated_at)
            VALUES($tid, $d, $title, $content, $plain, $pc, $ps, $st, $now, $now);
            SELECT last_insert_rowid();
            """;
        var now = DateTime.Now;
        BindEntry(cmd, e);
        cmd.Parameters.AddWithValue("$now", Stamp(now));
        e.Id = (long)cmd.ExecuteScalar()!;
        e.CreatedAt = now;
        e.UpdatedAt = now;
        e.Preview = MakePreview(e.PlainText);
        return e.Id;
    }

    public void UpdateEntry(Entry e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE entries SET topic_id=$tid, entry_date=$d, title=$title, content=$content, plain_text=$plain,
                   paper_color=$pc, paper_style=$ps, status=$st, updated_at=$now
            WHERE id=$id
            """;
        var now = DateTime.Now;
        BindEntry(cmd, e);
        cmd.Parameters.AddWithValue("$now", Stamp(now));
        cmd.Parameters.AddWithValue("$id", e.Id);
        if (cmd.ExecuteNonQuery() == 0) throw new InvalidOperationException($"Entry {e.Id} no longer exists.");
        e.UpdatedAt = now;
        e.Preview = MakePreview(e.PlainText);
    }

    private static void BindEntry(SqliteCommand cmd, Entry e)
    {
        cmd.Parameters.AddWithValue("$tid", e.TopicId);
        cmd.Parameters.AddWithValue("$d", e.EntryDate.ToString(DateFormat, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$title", e.Title ?? "");
        cmd.Parameters.Add("$content", SqliteType.Blob).Value = (object?)e.Content ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$plain", e.PlainText ?? "");
        cmd.Parameters.AddWithValue("$pc", e.PaperColor ?? "#FFFDF5");
        cmd.Parameters.AddWithValue("$ps", (int)e.PaperStyle);
        cmd.Parameters.AddWithValue("$st", (int)e.Status);
    }

    public void DeleteEntry(long id)
    {
        Exec("DELETE FROM entries WHERE id=$id", ("$id", id));
    }

    public HashSet<DateTime> GetEntryDates(long? topicId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = topicId is long t && t > 0
            ? "SELECT DISTINCT entry_date FROM entries WHERE topic_id=$t"
            : "SELECT DISTINCT entry_date FROM entries";
        if (topicId is long t2 && t2 > 0) cmd.Parameters.AddWithValue("$t", t2);
        using var r = cmd.ExecuteReader();
        var set = new HashSet<DateTime>();
        while (r.Read()) set.Add(ParseDate(r.GetString(0)));
        return set;
    }

    // ---------------------------------------------------------------- settings

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public string GetSetting(string key, string fallback) => GetSetting(key) ?? fallback;

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        SetSetting(c, key, value);
    }

    private static void SetSetting(SqliteConnection c, string key, string? value)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------- chat

    public ChatMessage AddChat(string role, string content)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var now = DateTime.Now;
        cmd.CommandText = "INSERT INTO chat_messages(role, content, created_at) VALUES($r, $c, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$t", Stamp(now));
        var id = (long)cmd.ExecuteScalar()!;
        return new ChatMessage { Id = id, Role = role, Content = content, CreatedAt = now };
    }

    public List<ChatMessage> GetChats(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, role, content, created_at FROM (SELECT * FROM chat_messages ORDER BY id DESC LIMIT $l) ORDER BY id";
        cmd.Parameters.AddWithValue("$l", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<ChatMessage>();
        while (r.Read())
            list.Add(new ChatMessage { Id = r.GetInt64(0), Role = r.GetString(1), Content = r.GetString(2), CreatedAt = ParseStamp(r.GetString(3)) });
        return list;
    }

    public void ClearChats() => Exec("DELETE FROM chat_messages");

    // ------------------------------------------------------------------ backup

    /// <summary>Consistent online backup using the SQLite backup API (safe with WAL).</summary>
    public void BackupTo(string destinationPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    // ----------------------------------------------------------------- helpers

    private void Exec(string sql, params (string name, object value)[] args)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    internal static string EscapeLike(string s) =>
        s.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    internal static string MakePreview(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var span = plain.Length > 200 ? plain[..200] : plain;
        var flat = string.Join(' ', span.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    private static string Now() => Stamp(DateTime.Now);
    private static string Stamp(DateTime t) => t.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string s) =>
        DateTime.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;

    private static DateTime ParseStamp(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue;
}
