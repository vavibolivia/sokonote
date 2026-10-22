using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using SokoNote.Data;
using SokoNote.Services;
using SokoNote.ViewModels;
using SokoNote.Views;

namespace SokoNote.Tests;

/// <summary>Drives the real main window in "one page" mode the way a user would (commands, keys, search box).</summary>
[Collection("UI")]
public class PageModeUiTests
{
    private static readonly DateTime Today = DateTime.Today;
    private static readonly DateTime Yesterday = DateTime.Today.AddDays(-1);
    private static readonly DateTime TwoDaysAgo = DateTime.Today.AddDays(-2);

    /// <summary>A topic whose page already has notes for the two previous days.</summary>
    private static void SeedPage(WorkLogRepository repo)
    {
        var topic = repo.AddTopic("프로젝트 A", "#5B8DEF");
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, TwoDaysAgo, "ko")!.Inlines.Add(new Run("그저께 메모"));
        PageService.EnsureDay(doc, Yesterday, "ko")!.Inlines.Add(new Run("어제 메모"));
        var page = repo.GetOrCreatePage(topic.Id);
        page.Content = PageService.Save(doc);
        page.PlainText = PageService.GetPlainText(doc, "ko");
        repo.UpdateEntry(page);
    }

    private static (MainWindow w, MainViewModel vm, WorkLogRepository repo, List<Exception> errors) OpenPage()
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(SeedPage);
        var vm = (MainViewModel)w.DataContext;
        vm.SelectedTopic = vm.Topics.Single(t => t.Name == "프로젝트 A");
        vm.IsPageMode = true;
        UiThread.Pump(TimeSpan.FromMilliseconds(300));
        return (w, vm, repo, errors);
    }

    private static DateTime? DayAtCaret(MainWindow w)
    {
        Block? b = w.Editor.CaretPosition.Paragraph;
        while (b is not null && PageService.DayOf(b) is null) b = b.PreviousBlock;
        return PageService.DayOf(b);
    }

    private static bool PressKey(MainWindow w, UIElement target, Key key, RoutedEvent evt)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w)!, 0, key) { RoutedEvent = evt };
        target.RaiseEvent(args);
        return args.Handled;
    }

    [Fact]
    public void Page_opens_on_today_with_a_new_divider_and_lists_the_days() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            Assert.True(vm.CurrentEntry?.IsPage);
            var days = PageService.Days(w.Editor.Document).Select(d => d.Day).ToList();
            Assert.Equal(new[] { TwoDaysAgo, Yesterday, Today }, days);       // today's divider was added
            Assert.Equal(Today, DayAtCaret(w));                                 // caret in today's section
            Assert.Equal(3, vm.PageDays.Count);
            Assert.True(vm.PageDays[0].IsToday);                                // newest first
            Assert.Equal(PageService.DayLabel(Today, "ko"), vm.PageDays[0].Label);
            Assert.Equal(Visibility.Collapsed, w.EntryMetaPanel.Visibility);   // no date/status fields on a page
            Assert.Equal(Visibility.Collapsed, w.DeleteEntryButton.Visibility);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Typing_today_is_saved_under_todays_date() => UiThread.Run(() =>
    {
        var (w, vm, repo, errors) = OpenPage();
        try
        {
            w.Editor.CaretPosition.InsertTextInRun("오늘 회의 정리");
            w.SaveNow();
            var stored = repo.GetEntry(vm.CurrentEntry!.Id)!;
            var plain = stored.PlainText.Replace("\r\n", "\n");
            var todayLine = plain.IndexOf($"## {Today:yyyy-MM-dd}", StringComparison.Ordinal);
            Assert.True(todayLine >= 0, plain);
            Assert.True(plain.IndexOf("오늘 회의 정리", StringComparison.Ordinal) > todayLine, plain);
            Assert.True(plain.IndexOf("어제 메모", StringComparison.Ordinal) < todayLine, plain);

            // reopen: dividers come back from storage
            vm.IsPageMode = false;
            vm.IsPageMode = true;
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(3, PageService.Days(w.Editor.Document).Count);
            Assert.Contains("오늘 회의 정리", new TextRange(w.Editor.Document.ContentStart, w.Editor.Document.ContentEnd).Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Backspace_and_delete_next_to_a_divider_are_refused() => UiThread.Run(() =>
    {
        var (w, _, _, errors) = OpenPage();
        try
        {
            var doc = w.Editor.Document;
            var count = PageService.CountDividers(doc);
            var todayDivider = PageService.Days(doc).Single(d => d.Day == Today).Divider;
            var after = (Paragraph)todayDivider.NextBlock;
            w.Editor.CaretPosition = after.ContentStart;
            Assert.True(PressKey(w, w.Editor, Key.Back, Keyboard.PreviewKeyDownEvent));

            var before = (Paragraph)todayDivider.PreviousBlock; // last line of yesterday
            w.Editor.CaretPosition = before.ContentEnd;
            Assert.True(PressKey(w, w.Editor, Key.Delete, Keyboard.PreviewKeyDownEvent));

            Assert.Equal(count, PageService.CountDividers(doc));
            Assert.Equal(Loc.T("S.PageDividerLocked"), w.SaveStatus.Text);

            // ordinary backspace inside text still works
            w.Editor.CaretPosition = before.ContentEnd;
            Assert.False(PressKey(w, w.Editor, Key.Back, Keyboard.PreviewKeyDownEvent));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Deleting_or_overtyping_a_selection_with_a_divider_is_undone() => UiThread.Run(() =>
    {
        var (w, _, _, errors) = OpenPage();
        try
        {
            var doc = w.Editor.Document;
            var count = PageService.CountDividers(doc);
            var text = new TextRange(doc.ContentStart, doc.ContentEnd).Text;

            // 1) Delete command over a range spanning yesterday's divider
            w.Editor.Focus();
            w.Editor.Selection.Select(doc.ContentStart, doc.ContentEnd.GetPositionAtOffset(-2) ?? doc.ContentEnd);
            EditingCommands.Delete.Execute(null, w.Editor);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(count, PageService.CountDividers(doc));

            // 2) replacing the selection (what typing over it does)
            var y = PageService.Days(doc).Single(d => d.Day == Yesterday).Divider;
            w.Editor.Selection.Select(y.ElementStart.GetPositionAtOffset(-3) ?? y.ElementStart, y.ElementEnd.GetPositionAtOffset(2) ?? y.ElementEnd);
            w.Editor.Selection.Text = "x";
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(count, PageService.CountDividers(doc));
            Assert.Contains("어제 메모", new TextRange(doc.ContentStart, doc.ContentEnd).Text);
            Assert.Equal(Loc.T("S.PageDividerLocked"), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Previous_and_next_day_move_the_caret_to_that_day() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.ShiftDay(-1);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Yesterday, DayAtCaret(w));
            vm.ShiftDay(-1);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));

            vm.ShiftDay(-5); // no notes that day: caret stays, user is told
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));
            Assert.Equal(Loc.F("S.PageDateNotFound", PageService.DayLabel(TwoDaysAgo.AddDays(-5), Loc.Language)), w.SaveStatus.Text);

            vm.GoToday();
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Today, DayAtCaret(w));

            // clicking a day in the middle list
            w.PageDayList.SelectedItem = vm.PageDays.Single(d => d.Date == Yesterday);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Yesterday, DayAtCaret(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Theory]
    [InlineData("korean")]
    [InlineData("iso")]
    [InlineData("slash")]
    public void Search_box_jumps_to_a_date(string format) => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.SearchText = format switch
            {
                "korean" => $"{TwoDaysAgo.Month}월 {TwoDaysAgo.Day}일",
                "iso" => TwoDaysAgo.ToString("yyyy-MM-dd"),
                _ => $"{TwoDaysAgo.Month}/{TwoDaysAgo.Day}",
            };
            Assert.True(PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent));
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));
            Assert.Equal(TwoDaysAgo, vm.SelectedDate.Date);
            Assert.True(vm.CurrentEntry?.IsPage);                              // still on the page, no list search
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Search_box_finds_text_in_the_page() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.SearchText = "어제 메모";
            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal("어제 메모", w.Editor.Selection.Text);
            vm.SearchText = "없는 말";
            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal(Loc.F("S.PageTextNotFound", "없는 말"), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Day_change_while_open_moves_to_the_new_day() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            // simulate "the app was opened yesterday and midnight just passed"
            vm.SelectedDate = Yesterday;
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            typeof(MainWindow).GetField("_today", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, Yesterday);
            typeof(MainWindow).GetMethod("CheckDayChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Today, vm.SelectedDate.Date);
            Assert.Equal(Today, DayAtCaret(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Claude_gets_only_the_days_in_scope_from_the_page() => UiThread.Run(() =>
    {
        var (w, vm, _, _) = OpenPage();
        try
        {
            w.Editor.CaretPosition.InsertTextInRun("오늘 메모");
            w.SaveNow();
            vm.SelectedDate = Yesterday;
            var day = vm.Claude.CollectEntries(ClaudeScope.SelectedDate).Single(e => e.IsPage).PlainText;
            Assert.Contains("어제 메모", day);
            Assert.DoesNotContain("그저께 메모", day);
            Assert.DoesNotContain("오늘 메모", day);
            var all = vm.Claude.CollectEntries(ClaudeScope.Everything).Single(e => e.IsPage).PlainText;
            Assert.Contains("그저께 메모", all);
            Assert.Contains("오늘 메모", all);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void All_topics_in_page_mode_asks_to_pick_a_topic() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.SelectedTopic = vm.Topics[0]; // "all topics"
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Null(vm.CurrentEntry);
            Assert.Equal(Loc.T("S.PagePickTopic"), vm.ListTitle);
            Assert.Empty(vm.PageDays);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });
}
