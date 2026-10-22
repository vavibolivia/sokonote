using System.IO;
using Microsoft.Data.Sqlite;
using SokoNote.Data;
using SokoNote.Models;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private (MainViewModel vm, List<Entry?> opened) Create()
    {
        var vm = new MainViewModel(new WorkLogRepository(Path.Combine(_dir, "vm.db")));
        var opened = new List<Entry?>();
        vm.OpenEntryRequested += (_, e) =>
        {
            opened.Add(e);
            vm.CurrentEntry = e; // what the window does when it loads an entry
        };
        vm.Initialize("date"); // these tests are about the card list; page-mode tests switch explicitly
        return (vm, opened);
    }

    [Fact]
    public void Deleting_the_only_entry_closes_the_editor() => Sta.Run(() =>
    {
        // regression: the deleted entry stayed visible (and editable) when the list became empty
        var (vm, opened) = Create();
        var topic = vm.AddTopic("A", "#111111");
        var entry = vm.CreateEntry(topic.Id)!;
        Assert.Equal(entry.Id, vm.CurrentEntry?.Id);

        opened.Clear();
        vm.DeleteEntry(vm.Entries.Single());

        Assert.Null(vm.CurrentEntry);
        Assert.Equal(new Entry?[] { null }, opened);
        Assert.Empty(vm.Entries);
    });

    [Fact]
    public void Deleting_the_open_entry_opens_the_next_one() => Sta.Run(() =>
    {
        var (vm, opened) = Create();
        var topic = vm.AddTopic("A", "#111111");
        var first = vm.CreateEntry(topic.Id)!;
        var second = vm.CreateEntry(topic.Id)!;
        Assert.Equal(second.Id, vm.CurrentEntry?.Id);

        vm.DeleteEntry(vm.Entries.Single(e => e.Id == second.Id));

        Assert.Equal(first.Id, vm.CurrentEntry?.Id);
        Assert.Single(vm.Entries);
    });

    [Fact]
    public void New_entry_is_visible_even_with_search_and_filters_active() => Sta.Run(() =>
    {
        var (vm, _) = Create();
        var a = vm.AddTopic("A", "#111111");
        vm.AddTopic("B", "#222222");
        vm.SelectedTopic = vm.Topics.Single(t => t.Name == "B");
        vm.StatusFilterIndex = 3;
        vm.SearchText = "nothing matches";

        var created = vm.CreateEntry(a.Id)!;

        Assert.Equal("", vm.SearchText);
        Assert.Equal(0, vm.StatusFilterIndex);
        Assert.Contains(vm.Entries, e => e.Id == created.Id);
        Assert.Equal(created.Id, vm.CurrentEntry?.Id);
    });

    [Fact]
    public void Topic_counts_follow_create_and_delete() => Sta.Run(() =>
    {
        var (vm, _) = Create();
        var a = vm.AddTopic("A", "#111111");
        vm.CreateEntry(a.Id);
        vm.CreateEntry(a.Id);
        Assert.Equal(2, vm.RealTopics.Single().EntryCount);
        Assert.Equal(2, vm.Topics[0].EntryCount); // "all topics"

        vm.DeleteEntry(vm.Entries[0]);
        Assert.Equal(1, vm.RealTopics.Single().EntryCount);
    });

    [Fact]
    public void Truncated_list_says_so() => Sta.Run(() =>
    {
        // regression: past the list limit, older entries silently disappeared from "all" view
        var (vm, _) = Create();
        vm.ListLimit = 3;
        var a = vm.AddTopic("A", "#111111");
        vm.IsDateMode = false;
        for (int i = 0; i < 5; i++) vm.CreateEntry(a.Id);
        vm.IsDateMode = false;
        vm.RefreshEntries();

        Assert.Equal(3, vm.Entries.Count);
        Assert.True(vm.IsListTruncated);
        Assert.Equal(Services.Loc.F("S.ListCountTruncated", "A", 3), vm.ListSubtitle); // same text with or without app resources

        vm.ListLimit = 10;
        vm.RefreshEntries();
        Assert.Equal(5, vm.Entries.Count);
        Assert.False(vm.IsListTruncated);
    });

    [Fact]
    public void Search_finds_text_written_on_a_topic_page_and_opens_it_there() => Sta.Run(() =>
    {
        // regression: notes written in one-page mode never showed up in the normal search
        var (vm, _) = Create();
        var a = vm.AddTopic("A", "#111111");
        var page = vm.Repo.GetOrCreatePage(a.Id);
        page.PlainText = "## 2026-10-21 10월 21일 (수)\n배포 일정 확정";
        vm.Repo.UpdateEntry(page);
        string? jumpedTo = null;
        vm.PageSearchRequested += (_, q) => jumpedTo = q;

        vm.SearchText = "배포";
        vm.RefreshEntries();                       // the debounce timer does this in the app
        var hit = Assert.Single(vm.Entries);
        Assert.True(hit.IsPage);
        Assert.Equal(Services.Loc.F("S.PageTitle", "A"), hit.Title);

        vm.SelectedEntry = hit;                    // clicking the result
        Assert.True(vm.IsPageMode);
        Assert.Equal(a.Id, vm.SelectedTopic?.Id);
        Assert.Equal(page.Id, vm.CurrentEntry?.Id);
        Assert.Equal("배포", jumpedTo);
    });

    [Fact]
    public void Renaming_the_topic_of_the_open_page_updates_the_page_title() => UiThread.Run(() => // real strings: the title must contain the name
    {
        var (vm, _) = Create();
        var a = vm.AddTopic("옛 이름", "#111111");
        vm.IsPageMode = true;
        Assert.Equal(Services.Loc.F("S.PageTitle", "옛 이름"), vm.ListTitle);

        vm.RenameTopic(vm.RealTopics.Single(), "새 이름");

        Assert.Equal(Services.Loc.F("S.PageTitle", "새 이름"), vm.ListTitle);
        Assert.Equal("새 이름", vm.CurrentEntry?.TopicName);
        _ = a;
    });

    [Fact]
    public void Calendar_marks_follow_the_page_days_in_page_mode() => Sta.Run(() =>
    {
        // regression: in page mode the calendar still marked dated-entry days, never the page's days
        var (vm, _) = Create();
        var a = vm.AddTopic("A", "#111111");
        vm.CreateEntry(a.Id);                                     // a dated card today
        var pageDay = DateTime.Today.AddDays(-3);

        vm.IsPageMode = true;
        vm.SetPageDays(new[] { new PageDay(pageDay, "x", false) });
        Assert.True(CalendarMarks.Contains(pageDay));
        Assert.False(CalendarMarks.Contains(DateTime.Today));    // the card is not on the page

        vm.IsPageMode = false;                                    // back to cards: their marks return
        vm.IsDateMode = true;
        Assert.True(CalendarMarks.Contains(DateTime.Today));
        Assert.False(CalendarMarks.Contains(pageDay));
    });

    [Fact]
    public void Date_navigation_filters_the_list() => Sta.Run(() =>
    {
        var (vm, _) = Create();
        var a = vm.AddTopic("A", "#111111");
        vm.IsDateMode = true;
        vm.GoToday();
        vm.CreateEntry(a.Id);
        vm.ShiftDay(-1);
        Assert.Empty(vm.Entries);
        Assert.Null(vm.CurrentEntry);
        vm.ShiftDay(1);
        Assert.Single(vm.Entries);
        vm.IsDateMode = false;
        Assert.Single(vm.Entries);
    });
}
