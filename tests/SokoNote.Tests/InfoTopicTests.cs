using System.IO;
using Microsoft.Data.Sqlite;
using SokoNote.Data;
using SokoNote.Models;
using SokoNote.Services;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

/// <summary>
/// A topic is either a dated journal or an "information" topic that just lists notes (server info, contacts…):
/// its entries have no date, are sorted by title and stay out of every date-based view.
/// </summary>
public sealed class InfoTopicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));
    private readonly WorkLogRepository _repo;

    public InfoTopicTests() => _repo = new WorkLogRepository(Path.Combine(_dir, "info.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private Entry Add(long topicId, string title, DateTime? date = null)
    {
        var e = new Entry { TopicId = topicId, Title = title, EntryDate = date ?? DateTime.Today, PlainText = title + " 내용" };
        _repo.InsertEntry(e);
        return e;
    }

    [Fact]
    public void Info_topic_entries_have_no_date_and_stay_out_of_date_views()
    {
        var journal = _repo.AddTopic("업무", "#111111");
        var info = _repo.AddTopic("서버 정보", "#222222", isInfo: true);
        Add(journal.Id, "회의");
        Add(info.Id, "DB 서버");

        Assert.True(_repo.GetTopics().Single(t => t.Id == info.Id).IsInfo);
        Assert.False(_repo.GetTopics().Single(t => t.Id == journal.Id).IsInfo);

        var today = _repo.QueryEntries(new EntryQuery { Date = DateTime.Today });
        Assert.Equal(new[] { "회의" }, today.Select(e => e.Title));               // date view: dated journals only
        Assert.Single(_repo.QueryEntries(new EntryQuery { From = DateTime.Today.AddDays(-7), To = DateTime.Today }));
        Assert.Single(_repo.GetEntryDates(null));                                  // calendar dots: journal day only…
        Assert.Empty(_repo.GetEntryDates(info.Id));                                // …none for the info topic

        var all = _repo.QueryEntries(new EntryQuery());
        Assert.False(all.Single(e => e.Title == "DB 서버").HasDate);
        Assert.True(all.Single(e => e.Title == "회의").HasDate);
        Assert.False(_repo.GetEntry(all.Single(e => e.Title == "DB 서버").Id)!.HasDate);
    }

    [Fact]
    public void Info_topic_lists_by_title()
    {
        var info = _repo.AddTopic("연락처", "#222222", isInfo: true);
        Add(info.Id, "하나", DateTime.Today.AddDays(-3));
        Add(info.Id, "가나", DateTime.Today);
        Add(info.Id, "다라", DateTime.Today.AddDays(-1));
        var list = _repo.QueryEntries(new EntryQuery { TopicId = info.Id, TitleOrder = true });
        Assert.Equal(new[] { "가나", "다라", "하나" }, list.Select(e => e.Title));
    }

    [Fact]
    public void Switching_the_type_back_restores_the_dates()
    {
        var t = _repo.AddTopic("전환", "#111111");
        var e = Add(t.Id, "기록", new DateTime(2026, 10, 1));
        _repo.SetTopicKind(t.Id, isInfo: true);
        Assert.False(_repo.GetEntry(e.Id)!.HasDate);
        Assert.Empty(_repo.QueryEntries(new EntryQuery { Date = new DateTime(2026, 10, 1) }));

        _repo.SetTopicKind(t.Id, isInfo: false);
        var back = _repo.GetEntry(e.Id)!;
        Assert.True(back.HasDate);
        Assert.Equal(new DateTime(2026, 10, 1), back.EntryDate);
    }

    [Fact]
    public void View_model_lists_an_info_topic_without_date_filter_even_in_date_view() => Sta.Run(() =>
    {
        // regression: only dated journals were possible — an info topic in "날짜별" showed only today's entries
        var info = _repo.AddTopic("서버 정보", "#222222", isInfo: true);
        Add(info.Id, "웹 서버", DateTime.Today.AddDays(-30));
        Add(info.Id, "DB 서버", DateTime.Today.AddDays(-2));
        var vm = new MainViewModel(_repo);
        vm.Initialize("date");
        vm.SelectedTopic = vm.Topics.Single(x => x.Id == info.Id);

        Assert.True(vm.SelectedTopicIsInfo);
        Assert.Equal(new[] { "DB 서버", "웹 서버" }, vm.Entries.Select(e => e.Title));  // all of them, by title
        Assert.False(vm.CalendarHasDates);
    });

    [Fact]
    public void Claude_and_exports_do_not_invent_dates_for_info_entries()
    {
        var e = new Entry { TopicName = "서버 정보", Title = "DB 서버", HasDate = false, EntryDate = new DateTime(2026, 1, 3), PlainText = "10.0.0.5" };
        var p = ClaudePromptBuilder.Build("q", new[] { e }, "all", Array.Empty<ChatMessage>(), DateTime.Today, "ko");
        Assert.DoesNotContain("2026-01-03", p);
        Assert.Contains("### [서버 정보] DB 서버", p);
        Assert.DoesNotContain("2026", ExportNaming.FileName(e, new DateTime(2026, 10, 22)));
    }

    [Fact]
    public void Existing_database_gets_the_topic_type_column()
    {
        var path = Path.Combine(_dir, "old.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE topics(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, color TEXT NOT NULL DEFAULT '#5B8DEF',
                    sort_order INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
                INSERT INTO topics(name, created_at) VALUES('옛 주제', '2026-01-01');
                """;
            cmd.ExecuteNonQuery();
        }
        var repo = new WorkLogRepository(path);
        Assert.False(repo.GetTopics().Single().IsInfo);
        repo.SetTopicKind(1, isInfo: true);
        Assert.True(repo.GetTopics().Single().IsInfo);
    }
}
