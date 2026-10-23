using System.IO;
using Microsoft.Data.Sqlite;
using SokoNote.Data;
using SokoNote.Models;
using SokoNote.Services;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

/// <summary>
/// Each entry is either a 📅 dated record or 📌 simple information (no date): server info, contacts… Topics stay as
/// they are; simple-information entries stay out of every date-based view and are listed first by title.
/// </summary>
public sealed class EntryKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkLogRepository _repo;

    public EntryKindTests() => _repo = new WorkLogRepository(Path.Combine(_dir, "kind.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private Entry Add(long topicId, string title, bool hasDate = true, DateTime? date = null)
    {
        var e = new Entry { TopicId = topicId, Title = title, EntryDate = date ?? DateTime.Today, PlainText = title + " 내용", HasDate = hasDate };
        _repo.InsertEntry(e);
        return e;
    }

    [Fact]
    public void Simple_information_has_no_date_and_stays_out_of_date_views()
    {
        var t = _repo.AddTopic("서버", "#111111");
        Add(t.Id, "회의");
        Add(t.Id, "DB 서버", hasDate: false);

        Assert.Equal(new[] { "회의" }, _repo.QueryEntries(new EntryQuery { Date = DateTime.Today }).Select(e => e.Title));
        Assert.Single(_repo.QueryEntries(new EntryQuery { From = DateTime.Today.AddDays(-7), To = DateTime.Today }));
        Assert.Single(_repo.GetEntryDates(t.Id));

        var all = _repo.QueryEntries(new EntryQuery { TopicId = t.Id });
        Assert.Equal("DB 서버", all[0].Title);                                   // simple information listed first
        Assert.False(all[0].HasDate);
        Assert.True(all[1].HasDate);
        Assert.False(_repo.GetEntry(all[0].Id)!.HasDate);
    }

    [Fact]
    public void Simple_information_is_listed_by_title_before_dated_records()
    {
        var t = _repo.AddTopic("연락처", "#222222");
        Add(t.Id, "최근 회의", date: DateTime.Today);
        Add(t.Id, "하나", hasDate: false);
        Add(t.Id, "가나", hasDate: false);
        Add(t.Id, "예전 회의", date: DateTime.Today.AddDays(-3));
        Assert.Equal(new[] { "가나", "하나", "최근 회의", "예전 회의" }, _repo.QueryEntries(new EntryQuery()).Select(e => e.Title));
    }

    [Fact]
    public void Switching_an_entry_back_restores_its_date()
    {
        var t = _repo.AddTopic("전환", "#111111");
        var e = Add(t.Id, "기록", date: new DateTime(2026, 10, 1));
        e.HasDate = false;
        _repo.UpdateEntry(e);
        Assert.Empty(_repo.QueryEntries(new EntryQuery { Date = new DateTime(2026, 10, 1) }));

        e.HasDate = true;
        _repo.UpdateEntry(e);
        var back = _repo.GetEntry(e.Id)!;
        Assert.True(back.HasDate);
        Assert.Equal(new DateTime(2026, 10, 1), back.EntryDate);
    }

    [Fact]
    public void New_simple_information_in_date_view_switches_to_all() => Sta.Run(() =>
    {
        var vm = new MainViewModel(_repo);
        vm.OpenEntryRequested += (_, x) => vm.CurrentEntry = x;               // what the window does
        vm.Initialize("date");
        var t = vm.AddTopic("서버", "#222222");
        var e = vm.CreateEntry(t.Id, hasDate: false)!;

        Assert.False(e.HasDate);
        Assert.True(vm.IsAllView);                                               // the view followed
        Assert.Contains(vm.Entries, x => x.Id == e.Id);
        Assert.Equal(e.Id, vm.CurrentEntry?.Id);
    });

    [Fact]
    public void Toggling_an_entry_to_simple_information_keeps_it_in_sight() => Sta.Run(() =>
    {
        var vm = new MainViewModel(_repo);
        vm.OpenEntryRequested += (_, x) => vm.CurrentEntry = x;               // what the window does
        vm.Initialize("date");
        var t = vm.AddTopic("서버", "#222222");
        var e = vm.CreateEntry(t.Id)!;
        Assert.True(vm.IsByDateView);

        vm.SetEntryHasDate(vm.CurrentEntry!, hasDate: false);
        Assert.True(vm.IsAllView);
        Assert.Contains(vm.Entries, x => x.Id == e.Id && !x.HasDate);
        Assert.False(_repo.GetEntry(e.Id)!.HasDate);                            // saved
        Assert.Equal("all", _repo.GetSetting($"ui.topicView.{t.Id}"));          // the topic now opens in 전체
    });

    [Fact]
    public void Claude_and_exports_do_not_invent_dates_for_simple_information()
    {
        var e = new Entry { TopicName = "서버", Title = "DB 서버", HasDate = false, EntryDate = new DateTime(2026, 1, 3), PlainText = "10.0.0.5" };
        var p = ClaudePromptBuilder.Build("q", new[] { e }, "all", Array.Empty<ChatMessage>(), DateTime.Today, "ko");
        Assert.DoesNotContain("2026-01-03", p);
        Assert.Contains("### [서버] DB 서버", p);
        Assert.DoesNotContain("2026", ExportNaming.FileName(e, new DateTime(2026, 10, 22)));
    }

    [Fact]
    public void A_topic_type_from_the_previous_build_moves_to_its_entries()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE topics(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, color TEXT NOT NULL DEFAULT '#5B8DEF',
                    sort_order INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, kind INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT, topic_id INTEGER NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                    entry_date TEXT NOT NULL, title TEXT NOT NULL DEFAULT '', content BLOB, plain_text TEXT NOT NULL DEFAULT '',
                    paper_color TEXT NOT NULL DEFAULT '#FFFDF5', paper_style INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL, is_page INTEGER NOT NULL DEFAULT 0);
                INSERT INTO topics(name, created_at, kind) VALUES('정보 주제', '2026-01-01', 1), ('일반 주제', '2026-01-01', 0);
                INSERT INTO entries(topic_id, entry_date, title, created_at, updated_at) VALUES(1, '2026-01-02', '정보', 'x', 'x'), (2, '2026-01-02', '기록', 'x', 'x');
                """;
            cmd.ExecuteNonQuery();
        }
        var repo = new WorkLogRepository(path);
        var all = repo.QueryEntries(new EntryQuery());
        Assert.False(all.Single(e => e.Title == "정보").HasDate);
        Assert.True(all.Single(e => e.Title == "기록").HasDate);
    }
}
