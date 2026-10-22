using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using SokoNote.Data;
using SokoNote.Models;
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
        PageService.EnsureDay(doc, TwoDaysAgo, "ko").Inlines.Add(new Run("그저께 메모"));
        PageService.EnsureDay(doc, Yesterday, "ko").Inlines.Add(new Run("어제 메모"));
        var page = repo.GetOrCreatePage(topic.Id);
        page.Content = PageService.Save(doc);
        page.PlainText = PageService.GetPlainText(doc, "ko");
        repo.UpdateEntry(page);
    }

    private static (MainWindow w, MainViewModel vm, WorkLogRepository repo, List<Exception> errors) OpenPage(Action<WorkLogRepository>? seed = null)
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(seed ?? SeedPage);
        var vm = (MainViewModel)w.DataContext;
        vm.SelectedTopic = vm.RealTopics.First();
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

    private static string DocText(MainWindow w) => new TextRange(w.Editor.Document.ContentStart, w.Editor.Document.ContentEnd).Text;

    // ------------------------------------------------------------ default: a dateless page

    [Fact]
    public void App_starts_on_the_first_topics_page_by_default() => UiThread.Run(() =>
    {
        var (w, _, errors) = UiThread.OpenMainWindow(r => { r.AddTopic("첫 주제", "#5B8DEF"); r.AddTopic("둘째", "#111111"); });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            Assert.True(vm.IsPageMode);
            Assert.Equal("첫 주제", vm.SelectedTopic?.Name);
            Assert.True(vm.CurrentEntry?.IsPage);
            Assert.Equal(0, PageService.CountDividers(w.Editor.Document));    // no date was added on its own
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void View_and_topic_are_remembered() => UiThread.Run(() =>
    {
        var (w, _, _) = UiThread.OpenMainWindow(r =>
        {
            r.AddTopic("A", "#5B8DEF");
            var b = r.AddTopic("B", "#111111");
            r.SetSetting("ui.viewMode", "all");
            r.SetSetting("ui.lastTopic", b.Id.ToString());
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            Assert.False(vm.IsPageMode);
            Assert.False(vm.IsDateMode);
            Assert.Equal("B", vm.SelectedTopic?.Name);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Opening_a_page_adds_no_date_and_puts_the_caret_at_the_end() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            Assert.Equal(new[] { TwoDaysAgo, Yesterday }, PageService.Days(w.Editor.Document).Select(d => d.Day));
            Assert.Equal(0, w.Editor.CaretPosition.GetOffsetToPosition(w.Editor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)));
            Assert.Equal(2, vm.PageDays.Count);
            Assert.Equal(Visibility.Collapsed, w.EntryMetaPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, w.DeleteEntryButton.Visibility);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Insert_today_adds_the_date_once_and_moves_below_it() => UiThread.Run(() =>
    {
        var (w, vm, repo, errors) = OpenPage();
        try
        {
            AppCommands.InsertToday.Execute(null, w);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Today, DayAtCaret(w));
            AppCommands.InsertToday.Execute(null, w);                         // second time: just goes there
            Assert.Equal(3, PageService.Days(w.Editor.Document).Count);
            Assert.True(vm.PageDays[0].IsToday);

            w.Editor.CaretPosition.InsertTextInRun("오늘 회의 정리");
            w.SaveNow();
            var plain = repo.GetEntry(vm.CurrentEntry!.Id)!.PlainText.Replace("\r\n", "\n");
            var todayLine = plain.IndexOf($"## {Today:yyyy-MM-dd}", StringComparison.Ordinal);
            Assert.True(todayLine >= 0 && plain.IndexOf("오늘 회의 정리", StringComparison.Ordinal) > todayLine, plain);

            // reopen: the dividers come back from storage, still nothing added
            vm.IsPageMode = false;
            vm.IsPageMode = true;
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(3, PageService.Days(w.Editor.Document).Count);
            Assert.Contains("오늘 회의 정리", DocText(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void New_entry_in_page_mode_inserts_today_instead_of_a_hidden_card() => UiThread.Run(() =>
    {
        // regression: Ctrl+N in page mode inserted a dated entry the user never saw
        var (w, vm, repo, errors) = OpenPage();
        try
        {
            var cards = repo.CountEntries();
            AppCommands.NewEntry.Execute(null, w);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(cards, repo.CountEntries());
            Assert.True(vm.CurrentEntry?.IsPage);
            Assert.Equal(Today, DayAtCaret(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Day_change_while_open_adds_no_date() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.SelectedDate = Yesterday;
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            typeof(MainWindow).GetField("_today", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, Yesterday);
            typeof(MainWindow).GetMethod("CheckDayChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Today, vm.SelectedDate.Date);                         // the selected day follows
            Assert.Equal(2, PageService.Days(w.Editor.Document).Count);         // but the page gets no new date
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    // ------------------------------------------------------------ dividers are locked

    [Fact]
    public void Backspace_and_delete_next_to_a_divider_are_refused() => UiThread.Run(() =>
    {
        var (w, _, _, errors) = OpenPage();
        try
        {
            var doc = w.Editor.Document;
            var count = PageService.CountDividers(doc);
            var yesterday = PageService.Days(doc).Single(d => d.Day == Yesterday).Divider;
            var after = (Paragraph)yesterday.NextBlock;                         // "어제 메모"
            w.Editor.CaretPosition = after.ContentStart;
            Assert.True(PressKey(w, w.Editor, Key.Back, Keyboard.PreviewKeyDownEvent));

            var before = (Paragraph)yesterday.PreviousBlock;                    // "그저께 메모"
            w.Editor.CaretPosition = before.ContentEnd;
            Assert.True(PressKey(w, w.Editor, Key.Delete, Keyboard.PreviewKeyDownEvent));

            Assert.Equal(count, PageService.CountDividers(doc));
            Assert.Equal(Loc.T("S.PageDividerLocked"), w.SaveStatus.Text);

            w.Editor.CaretPosition = before.ContentEnd;                         // ordinary backspace inside text still works
            Assert.False(PressKey(w, w.Editor, Key.Back, Keyboard.PreviewKeyDownEvent));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Backspace_below_an_empty_date_removes_it() => UiThread.Run(() =>
    {
        // a date added by mistake (or by the previous version automatically) can be taken back while it is empty
        var (w, vm, _, errors) = OpenPage();
        try
        {
            AppCommands.InsertToday.Execute(null, w);
            Assert.Equal(3, PageService.Days(w.Editor.Document).Count);
            Assert.Equal(Today, DayAtCaret(w));

            Assert.True(PressKey(w, w.Editor, Key.Back, Keyboard.PreviewKeyDownEvent));
            UiThread.Pump(TimeSpan.FromMilliseconds(200));

            Assert.Equal(new[] { TwoDaysAgo, Yesterday }, PageService.Days(w.Editor.Document).Select(d => d.Day));
            Assert.Equal(2, vm.PageDays.Count);
            Assert.Contains("어제 메모", DocText(w));                            // nothing else touched
            Assert.Equal(Loc.F("S.PageDayRemoved", PageService.DayLabel(Today, Loc.Language)), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Old_page_starting_with_a_date_gets_a_dateless_line_on_top() => UiThread.Run(() =>
    {
        var (w, _, _, errors) = OpenPage(r =>
        {
            var t = r.AddTopic("옛 페이지", "#5B8DEF");
            var doc = new FlowDocument();
            PageService.EnsureDay(doc, Today, "ko");
            doc.Blocks.Remove(doc.Blocks.FirstBlock);                         // starts with "2026년" like the old version saved it
            var page = r.GetOrCreatePage(t.Id);
            page.Content = PageService.Save(doc);
            r.UpdateEntry(page);
        });
        try
        {
            var first = Assert.IsType<Paragraph>(w.Editor.Document.Blocks.FirstBlock);
            w.Editor.CaretPosition = first.ContentStart;
            w.Editor.CaretPosition.InsertTextInRun("날짜 없는 메모");
            Assert.Null(DayAtCaret(w));                                         // belongs to no date
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

            w.Editor.Focus();
            w.Editor.Selection.Select(doc.ContentStart, doc.ContentEnd.GetPositionAtOffset(-2) ?? doc.ContentEnd);
            EditingCommands.Delete.Execute(null, w.Editor);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(count, PageService.CountDividers(doc));

            var y = PageService.Days(doc).Single(d => d.Day == Yesterday).Divider;
            w.Editor.Selection.Select(y.ElementStart.GetPositionAtOffset(-3) ?? y.ElementStart, y.ElementEnd.GetPositionAtOffset(2) ?? y.ElementEnd);
            w.Editor.Selection.Text = "x";
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(count, PageService.CountDividers(doc));
            Assert.Contains("어제 메모", DocText(w));
            Assert.Equal(Loc.T("S.PageDividerLocked"), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Inserting_a_link_over_a_date_is_refused_before_the_dialog() => UiThread.Run(() =>
    {
        // regression: Ctrl+K over a selection with a date asked for a URL, then the divider protection undid it all
        var (w, _, _, errors) = OpenPage();
        var dialogOpened = false;
        // if the dialog does open, close it so the test fails instead of hanging on a modal window
        var closer = new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(1500), System.Windows.Threading.DispatcherPriority.Normal, (s, _) =>
        {
            foreach (var d in Application.Current.Windows.OfType<LinkDialog>().ToList()) { dialogOpened = true; d.Close(); }
        }, w.Dispatcher);
        try
        {
            var doc = w.Editor.Document;
            var y = PageService.Days(doc).Single(d => d.Day == Yesterday).Divider;
            w.Editor.Selection.Select(y.ElementStart.GetPositionAtOffset(-3) ?? y.ElementStart, y.ElementEnd.GetPositionAtOffset(2) ?? y.ElementEnd);

            AppCommands.InsertLink.Execute(null, w);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));

            Assert.False(dialogOpened);
            Assert.Equal(3, PageService.CountDividers(doc));
            Assert.Equal(Loc.T("S.PageDividerLocked"), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { closer.Stop(); w.Close(); }
    });

    // ------------------------------------------------------------ moving between days

    [Fact]
    public void Day_navigation_moves_to_existing_days_only() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage();
        try
        {
            vm.ShiftDay(-1);                                                    // today -> yesterday
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(Yesterday, DayAtCaret(w));
            vm.ShiftDay(-1);
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));

            vm.ShiftDay(-5);                                                    // no notes that day: caret stays, user is told
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));
            Assert.Equal(Loc.F("S.PageDateNotFound", PageService.DayLabel(TwoDaysAgo.AddDays(-5), Loc.Language)), w.SaveStatus.Text);

            vm.GoToday();                                                       // today is not on the page: nothing is added
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.Equal(2, PageService.Days(w.Editor.Document).Count);

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
            Assert.True(vm.CurrentEntry?.IsPage);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    // ------------------------------------------------------------ search puts the caret on the hit

    [Fact]
    public void Search_box_selects_each_hit_in_turn() => UiThread.Run(() =>
    {
        var (w, vm, _, errors) = OpenPage(r =>
        {
            var t = r.AddTopic("검색", "#5B8DEF");
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph(new Run("첫 줄 배포 계획")));
            var p = new Paragraph(new Run("둘째 줄 "));
            p.Inlines.Add(new Bold(new Run("배")));                              // a hit split by formatting
            p.Inlines.Add(new Run("포 완료"));
            doc.Blocks.Add(p);
            var page = r.GetOrCreatePage(t.Id);
            page.Content = PageService.Save(doc);
            r.UpdateEntry(page);
        });
        try
        {
            vm.SearchText = "배포";
            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal("배포", w.Editor.Selection.Text);
            Assert.StartsWith("첫 줄", new TextRange(w.Editor.Selection.Start.Paragraph!.ContentStart, w.Editor.Selection.Start.Paragraph.ContentEnd).Text);

            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);           // Enter again: next hit
            Assert.Equal("배포", w.Editor.Selection.Text);
            Assert.StartsWith("둘째 줄", new TextRange(w.Editor.Selection.Start.Paragraph!.ContentStart, w.Editor.Selection.Start.Paragraph.ContentEnd).Text);

            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);           // and wraps around
            Assert.StartsWith("첫 줄", new TextRange(w.Editor.Selection.Start.Paragraph!.ContentStart, w.Editor.Selection.Start.Paragraph.ContentEnd).Text);
            Assert.True(w.Editor.IsInactiveSelectionHighlightEnabled);           // visible while the search box has focus

            vm.SearchText = "없는 말";
            PressKey(w, w.SearchBox, Key.Enter, Keyboard.KeyDownEvent);
            Assert.Equal(Loc.F("S.PageTextNotFound", "없는 말"), w.SaveStatus.Text);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Opening_a_card_from_search_selects_the_match() => UiThread.Run(() =>
    {
        // regression: a search result opened with the caret at the top, not on the searched text
        var (w, _, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("A", "#5B8DEF");
            var doc = new FlowDocument();
            for (int i = 0; i < 40; i++) doc.Blocks.Add(new Paragraph(new Run($"줄 {i}")));
            doc.Blocks.Add(new Paragraph(new Run("여기에 배포 일정")));
            r.InsertEntry(new Entry
            {
                TopicId = t.Id, EntryDate = DateTime.Today, Title = "긴 일지",
                Content = DocumentService.Save(doc), PlainText = DocumentService.GetPlainText(doc),
            });
            r.SetSetting("ui.viewMode", "all");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SearchText = "배포";
            vm.RefreshEntries();
            UiThread.Pump(TimeSpan.FromMilliseconds(300));

            Assert.Equal("긴 일지", vm.CurrentEntry?.Title);
            Assert.Equal("배포", w.Editor.Selection.Text);
            Assert.StartsWith("여기에", new TextRange(w.Editor.Selection.Start.Paragraph!.ContentStart, w.Editor.Selection.Start.Paragraph.ContentEnd).Text);
            Assert.True(w.Editor.VerticalOffset > 0, "scrolled to the hit");
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Normal_search_result_on_a_page_opens_the_page_at_the_match() => UiThread.Run(() =>
    {
        var (w, _, errors) = UiThread.OpenMainWindow(r => { SeedPage(r); r.SetSetting("ui.viewMode", "all"); });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SearchText = "그저께";
            vm.RefreshEntries();
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            var hit = Assert.Single(vm.Entries);

            w.EntryList.SelectedItem = hit;
            UiThread.Pump(TimeSpan.FromMilliseconds(300));

            Assert.True(vm.IsPageMode);
            Assert.True(vm.CurrentEntry?.IsPage);
            Assert.Equal("그저께", w.Editor.Selection.Text);
            Assert.Equal(TwoDaysAgo, DayAtCaret(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    // ------------------------------------------------------------ other

    [Fact]
    public void Claude_gets_only_the_days_in_scope_from_the_page() => UiThread.Run(() =>
    {
        var (w, vm, _, _) = OpenPage();
        try
        {
            AppCommands.InsertToday.Execute(null, w);
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
    public void Claude_answer_goes_to_today_if_dated_otherwise_to_the_end() => UiThread.Run(() =>
    {
        var (w, _, _, errors) = OpenPage();
        try
        {
            var insert = typeof(MainWindow).GetMethod("InsertTextIntoEntry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True((bool)insert.Invoke(w, new object[] { "끝에 붙는 답변" })!);
            Assert.Equal(2, PageService.Days(w.Editor.Document).Count);         // no date added for it
            Assert.EndsWith("끝에 붙는 답변", DocText(w).TrimEnd());

            AppCommands.InsertToday.Execute(null, w);
            Assert.True((bool)insert.Invoke(w, new object[] { "오늘 답변" })!);
            Assert.Equal(Today, DayAtCaret(w));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Page_with_a_later_day_gets_today_in_order() => UiThread.Run(() =>
    {
        var tomorrow = Today.AddDays(1);                                        // the PC clock was set back
        var (w, _, _, errors) = OpenPage(r =>
        {
            var topic = r.AddTopic("시계", "#5B8DEF");
            var doc = new FlowDocument();
            PageService.EnsureDay(doc, Yesterday, "ko").Inlines.Add(new Run("어제"));
            PageService.EnsureDay(doc, tomorrow, "ko").Inlines.Add(new Run("내일"));
            var page = r.GetOrCreatePage(topic.Id);
            page.Content = PageService.Save(doc);
            r.UpdateEntry(page);
        });
        try
        {
            AppCommands.InsertToday.Execute(null, w);
            Assert.Equal(new[] { Yesterday, Today, tomorrow }, PageService.Days(w.Editor.Document).Select(d => d.Day));
            Assert.Equal(Today, DayAtCaret(w));
            Assert.Empty(errors);
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
