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
        vm.Initialize();
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
