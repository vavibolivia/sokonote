using System.IO;
using Microsoft.Data.Sqlite;
using SokoNote.Data;
using SokoNote.Models;

namespace SokoNote.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkLogRepository _repo;

    public RepositoryTests()
    {
        _repo = new WorkLogRepository(Path.Combine(_dir, "test.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static Entry NewEntry(long topicId, DateTime date, string title, string text = "", EntryStatus status = EntryStatus.Note) =>
        new() { TopicId = topicId, EntryDate = date, Title = title, PlainText = text, Status = status };

    [Fact]
    public void Topics_are_added_in_order_with_counts()
    {
        var a = _repo.AddTopic("  프로젝트 A  ", "#FF0000");
        _repo.AddTopic("B", "#00FF00");
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "t1"));
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "t2"));

        var topics = _repo.GetTopics();
        Assert.Equal(new[] { "프로젝트 A", "B" }, topics.Select(t => t.Name));
        Assert.Equal(2, topics[0].EntryCount);
        Assert.Equal(0, topics[1].EntryCount);
        Assert.Equal(2, _repo.CountEntries());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_topic_name_is_rejected(string name)
    {
        Assert.Throws<ArgumentException>(() => _repo.AddTopic(name, "#000000"));
    }

    [Fact]
    public void Long_topic_name_is_truncated()
    {
        var t = _repo.AddTopic(new string('가', 300), "#000000");
        Assert.Equal(100, t.Name.Length);
    }

    [Fact]
    public void Reorder_rename_recolor_topics()
    {
        var a = _repo.AddTopic("A", "#111111");
        var b = _repo.AddTopic("B", "#222222");
        var c = _repo.AddTopic("C", "#333333");
        _repo.ReorderTopics(new[] { c.Id, a.Id, b.Id });
        _repo.RenameTopic(a.Id, "A2");
        _repo.SetTopicColor(b.Id, "#ABCDEF");

        var topics = _repo.GetTopics();
        Assert.Equal(new[] { "C", "A2", "B" }, topics.Select(t => t.Name));
        Assert.Equal("#ABCDEF", topics[2].Color);
    }

    [Fact]
    public void Deleting_topic_cascades_to_entries()
    {
        var a = _repo.AddTopic("A", "#111111");
        var keep = _repo.AddTopic("Keep", "#111111");
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "gone"));
        _repo.InsertEntry(NewEntry(keep.Id, DateTime.Today, "stays"));
        _repo.DeleteTopic(a.Id);

        var all = _repo.QueryEntries(new EntryQuery());
        Assert.Single(all);
        Assert.Equal("stays", all[0].Title);
    }

    [Fact]
    public void Entry_round_trips_all_fields()
    {
        var t = _repo.AddTopic("A", "#123456");
        var content = new byte[] { 1, 2, 3, 0, 255 };
        var e = NewEntry(t.Id, new DateTime(2026, 10, 21), "제목", "본문 내용\n둘째 줄", EntryStatus.Urgent);
        e.Content = content;
        e.PaperColor = "#EEF6FF";
        e.PaperStyle = PaperStyle.Grid;
        var id = _repo.InsertEntry(e);

        var back = _repo.GetEntry(id)!;
        Assert.Equal("제목", back.Title);
        Assert.Equal("본문 내용\n둘째 줄", back.PlainText);
        Assert.Equal(new DateTime(2026, 10, 21), back.EntryDate);
        Assert.Equal(EntryStatus.Urgent, back.Status);
        Assert.Equal(PaperStyle.Grid, back.PaperStyle);
        Assert.Equal("#EEF6FF", back.PaperColor);
        Assert.Equal(content, back.Content);
        Assert.Equal("A", back.TopicName);
        Assert.Equal("#123456", back.TopicColor);
        Assert.Equal("본문 내용 둘째 줄", back.Preview);
    }

    [Fact]
    public void Update_of_deleted_entry_throws()
    {
        var t = _repo.AddTopic("A", "#123456");
        var e = NewEntry(t.Id, DateTime.Today, "x");
        _repo.InsertEntry(e);
        _repo.DeleteEntry(e.Id);
        Assert.Null(_repo.GetEntry(e.Id));
        Assert.Throws<InvalidOperationException>(() => _repo.UpdateEntry(e));
    }

    [Fact]
    public void Query_filters_by_topic_date_range_status_and_search()
    {
        var a = _repo.AddTopic("Alpha", "#111111");
        var b = _repo.AddTopic("Beta", "#222222");
        var d1 = new DateTime(2026, 10, 1);
        var d2 = new DateTime(2026, 10, 15);
        _repo.InsertEntry(NewEntry(a.Id, d1, "회의록", "배포 일정 확정", EntryStatus.Done));
        _repo.InsertEntry(NewEntry(a.Id, d2, "버그", "100% 재현 a_b", EntryStatus.InProgress));
        _repo.InsertEntry(NewEntry(b.Id, d2, "기타", "nothing"));

        Assert.Equal(2, _repo.QueryEntries(new EntryQuery { TopicId = a.Id }).Count);
        Assert.Equal(2, _repo.QueryEntries(new EntryQuery { Date = d2 }).Count);
        Assert.Equal(3, _repo.QueryEntries(new EntryQuery { From = d1, To = d2 }).Count);
        Assert.Single(_repo.QueryEntries(new EntryQuery { From = d2.AddDays(-1), To = d2, TopicId = b.Id }));
        Assert.Single(_repo.QueryEntries(new EntryQuery { Status = EntryStatus.Done }));
        Assert.Single(_repo.QueryEntries(new EntryQuery { Search = "배포" }));
        Assert.Equal(2, _repo.QueryEntries(new EntryQuery { Search = "alpha" }).Count); // topic name, case-insensitive
        // LIKE wildcards typed by the user are matched literally
        Assert.Single(_repo.QueryEntries(new EntryQuery { Search = "100%" }));
        Assert.Single(_repo.QueryEntries(new EntryQuery { Search = "a_b" }));
        Assert.Empty(_repo.QueryEntries(new EntryQuery { Search = "%없음" }));
        // newest date first
        Assert.Equal(d2, _repo.QueryEntries(new EntryQuery())[0].EntryDate);
    }

    [Fact]
    public void Query_preview_mode_hides_full_text()
    {
        var a = _repo.AddTopic("A", "#111111");
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "t", new string('x', 1000)));
        var light = _repo.QueryEntries(new EntryQuery()).Single();
        var full = _repo.QueryEntries(new EntryQuery { IncludeFullText = true }).Single();
        Assert.Equal("", light.PlainText);
        Assert.Equal(141, light.Preview.Length); // 140 + ellipsis
        Assert.Equal(1000, full.PlainText.Length);
    }

    [Fact]
    public void Entry_dates_per_topic()
    {
        var a = _repo.AddTopic("A", "#111111");
        var b = _repo.AddTopic("B", "#111111");
        _repo.InsertEntry(NewEntry(a.Id, new DateTime(2026, 1, 1), "1"));
        _repo.InsertEntry(NewEntry(a.Id, new DateTime(2026, 1, 1), "2"));
        _repo.InsertEntry(NewEntry(b.Id, new DateTime(2026, 1, 2), "3"));
        Assert.Single(_repo.GetEntryDates(a.Id));
        Assert.Equal(2, _repo.GetEntryDates(null).Count);
    }

    [Fact]
    public void Settings_upsert_and_null()
    {
        Assert.Null(_repo.GetSetting("k"));
        Assert.Equal("fb", _repo.GetSetting("k", "fb"));
        _repo.SetSetting("k", "v1");
        _repo.SetSetting("k", "v2");
        Assert.Equal("v2", _repo.GetSetting("k"));
        _repo.SetSetting("k", null);
        Assert.Null(_repo.GetSetting("k"));
        Assert.Equal("1", _repo.GetSetting("schema.version"));
    }

    [Fact]
    public void Chats_keep_latest_in_chronological_order()
    {
        for (int i = 0; i < 10; i++) _repo.AddChat(i % 2 == 0 ? "user" : "assistant", $"m{i}");
        var last3 = _repo.GetChats(3);
        Assert.Equal(new[] { "m7", "m8", "m9" }, last3.Select(m => m.Content));
        _repo.ClearChats();
        Assert.Empty(_repo.GetChats());
    }

    [Fact]
    public void Backup_is_a_readable_copy()
    {
        var a = _repo.AddTopic("A", "#111111");
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "backed up"));
        var target = Path.Combine(_dir, "backups", "b.db");
        _repo.BackupTo(target);
        _repo.BackupTo(target); // overwriting an existing backup works

        var copy = new WorkLogRepository(target);
        Assert.Equal("backed up", copy.QueryEntries(new EntryQuery()).Single().Title);
    }

    [Fact]
    public void Reopening_existing_database_keeps_data()
    {
        var a = _repo.AddTopic("A", "#111111");
        _repo.InsertEntry(NewEntry(a.Id, DateTime.Today, "persist"));
        var again = new WorkLogRepository(_repo.DbPath);
        Assert.Equal("persist", again.QueryEntries(new EntryQuery()).Single().Title);
    }

    [Fact]
    public async Task Stress_parallel_writers_and_readers()
    {
        var topic = _repo.AddTopic("Stress", "#111111");
        const int writers = 8, perWriter = 60;
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                var e = NewEntry(topic.Id, DateTime.Today.AddDays(-(i % 30)), $"w{w}-{i}", $"본문 {w} {i} " + new string('가', 500));
                _repo.InsertEntry(e);
                e.Title += "-upd";
                _repo.UpdateEntry(e);
                if (i % 10 == 0) _repo.QueryEntries(new EntryQuery { Search = "본문", Limit = 50 });
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(writers * perWriter, _repo.CountEntries());
        Assert.All(_repo.QueryEntries(new EntryQuery()), e => Assert.EndsWith("-upd", e.Title));
    }

    [Fact]
    public void Large_entry_round_trips()
    {
        var t = _repo.AddTopic("Big", "#111111");
        var blob = new byte[5 * 1024 * 1024];
        new Random(1).NextBytes(blob);
        var e = NewEntry(t.Id, DateTime.Today, "big", new string('a', 1_000_000));
        e.Content = blob;
        _repo.InsertEntry(e);
        var back = _repo.GetEntry(e.Id)!;
        Assert.Equal(blob, back.Content);
        Assert.Equal(1_000_000, back.PlainText.Length);
    }

    [Fact]
    public void Like_escaping()
    {
        Assert.Equal(@"a\%b\_c\\d", WorkLogRepository.EscapeLike(@"a%b_c\d"));
    }
}
