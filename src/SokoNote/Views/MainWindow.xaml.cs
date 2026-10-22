using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using SokoNote.Controls;
using SokoNote.Models;
using SokoNote.Services;
using SokoNote.ViewModels;

namespace SokoNote.Views;

public static class AppCommands
{
    public static readonly RoutedUICommand NewEntry = new("New entry", nameof(NewEntry), typeof(AppCommands));
    public static readonly RoutedUICommand SaveEntry = new("Save", nameof(SaveEntry), typeof(AppCommands));
    public static readonly RoutedUICommand FocusSearch = new("Search", nameof(FocusSearch), typeof(AppCommands));
    public static readonly RoutedUICommand InsertLink = new("Link", nameof(InsertLink), typeof(AppCommands));
    public static readonly RoutedUICommand PrevDay = new("Previous day", nameof(PrevDay), typeof(AppCommands));
    public static readonly RoutedUICommand NextDay = new("Next day", nameof(NextDay), typeof(AppCommands));
    public static readonly RoutedUICommand Today = new("Today", nameof(Today), typeof(AppCommands));
    public static readonly RoutedUICommand ToggleClaude = new("Claude", nameof(ToggleClaude), typeof(AppCommands));
    public static readonly RoutedUICommand OpenSettings = new("Settings", nameof(OpenSettings), typeof(AppCommands));
    public static readonly RoutedUICommand AddTopic = new("Add topic", nameof(AddTopic), typeof(AppCommands));
    public static readonly RoutedUICommand DeleteEntry = new("Delete entry", nameof(DeleteEntry), typeof(AppCommands));
    public static readonly RoutedUICommand InsertToday = new("Insert today's date", nameof(InsertToday), typeof(AppCommands));
}

public sealed record FontItem(string Name, FontFamily Family)
{
    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    public const string DefaultFontFamily = "Malgun Gothic";
    public const double DefaultFontSizePt = 11;
    public static readonly double[] FontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 };


    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _countTimer;
    private bool _loading;
    private bool _dirty;
    private bool _syncingToolbar;
    private Entry? _subscribed;
    private string _colorTarget = "fore";
    private int _dividerCount;
    private DateTime _today = DateTime.Today;
    private readonly DispatcherTimer _dayTimer;
    private static List<FontItem>? _fontCache;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(App.Repository);
        DataContext = _vm;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _countTimer.Tick += (_, _) =>
        {
            _countTimer.Stop();
            UpdateWordCount();
        };

        _dayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dayTimer.Tick += (_, _) => CheckDayChanged();
        _dayTimer.Start();

        _vm.OpenEntryRequested += (_, e) => LoadEntry(e);
        _vm.PageDayRequested += (_, day) => JumpToDay(day, createIfToday: false);
        _vm.PageSearchRequested += (_, text) => SearchInPage(text, fromStart: true);
        // the open entry may stay open across a new search: highlight the new text in it too
        _vm.SearchResultsShown += (_, text) => { if (_vm.CurrentEntry is { IsPage: false }) SelectSearchHit(text, fromStart: true); };
        _vm.BeforeNavigate += (_, _) => SaveNow();
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.Claude.FlushEditor = SaveNow;
        _vm.Claude.MessageAdded += (_, _) => Dispatcher.BeginInvoke(ChatScroll.ScrollToEnd, DispatcherPriority.Background);

        StatusFilter.ItemsSource = new[] { Loc.T("S.StatusFilterAll") }
            .Concat(Enum.GetValues<EntryStatus>().Select(s => Loc.T("S.Status." + s))).ToList();
        StatusCombo.ItemsSource = Enum.GetValues<EntryStatus>();
        ScopeCombo.ItemsSource = Enum.GetValues<ClaudeScope>().Select(s => Loc.T("S.Claude.Scope." + s)).ToList();
        FontCombo.ItemsSource = SystemFonts();
        SizeCombo.ItemsSource = FontSizes.Select(s => s.ToString(CultureInfo.InvariantCulture)).ToList();
        PaperPalette.Configure(ColorPalette.PaperColors, false, null);

        Editor.IsInactiveSelectionHighlightEnabled = true; // search hits stay visible while the search box has focus
        Editor.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(Hyperlink_RequestNavigate));
        DataObject.AddPastingHandler(Editor, Editor_Pasting);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // ================================================================ startup / shutdown

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestoreWindowState();
        ApplyEditorDefaults();
        _vm.Initialize(_vm.Repo.GetSetting("ui.viewMode"),
            long.TryParse(_vm.Repo.GetSetting("ui.lastTopic"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lastTopic) ? lastTopic : null);
        _vm.Claude.Load();
        if (_vm.Repo.GetSetting("ui.claudeOpen") == "1") SetClaudeOpen(true);
        _ = DetectClaudeAccountAsync();
        if (_vm.CurrentEntry is null && _vm.RealTopics.Count == 0)
            Dispatcher.BeginInvoke(() => TopicList.Focus(), DispatcherPriority.Input);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveNow();
        if (_dirty && _vm.CurrentEntry is not null)
        {
            var r = MessageBox.Show(this, Loc.F("S.CloseUnsaved", SaveStatus.Text), Loc.T("S.AppName"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        SaveWindowState();
    }

    private void RestoreWindowState()
    {
        var repo = _vm.Repo;
        if (double.TryParse(repo.GetSetting("ui.width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w >= MinWidth) Width = w;
        if (double.TryParse(repo.GetSetting("ui.height"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h >= MinHeight) Height = h;
        if (double.TryParse(repo.GetSetting("ui.left"), NumberStyles.Float, CultureInfo.InvariantCulture, out var l)
            && double.TryParse(repo.GetSetting("ui.top"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            && WindowPlacement.IsReachable(l, t, Width, WindowPlacement.VirtualScreen))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        if (repo.GetSetting("ui.maximized") == "1") WindowState = WindowState.Maximized;
        if (double.TryParse(repo.GetSetting("ui.listWidth"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lw) && lw >= ListColumn.MinWidth && lw <= ListColumn.MaxWidth)
            ListColumn.Width = new GridLength(lw);
    }

    private void SaveWindowState()
    {
        try
        {
            var repo = _vm.Repo;
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            repo.SetSetting("ui.width", bounds.Width.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("ui.height", bounds.Height.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("ui.left", bounds.Left.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("ui.top", bounds.Top.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("ui.maximized", WindowState == WindowState.Maximized ? "1" : "0");
            repo.SetSetting("ui.listWidth", ListColumn.ActualWidth.ToString(CultureInfo.InvariantCulture));
            repo.SetSetting("ui.claudeOpen", IsClaudeOpen ? "1" : "0");
            repo.SetSetting("ui.viewMode", _vm.ViewModeSetting);
            repo.SetSetting("ui.lastTopic", (_vm.SelectedTopic?.Id ?? 0).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Logger.Error("Saving window state failed", ex);
        }
    }

    private void ApplyEditorDefaults()
    {
        var family = _vm.Repo.GetSetting("editor.fontFamily", DefaultFontFamily);
        var pt = double.TryParse(_vm.Repo.GetSetting("editor.fontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : DefaultFontSizePt;
        Editor.FontFamily = new FontFamily(family);
        Editor.FontSize = PtToPx(pt);
        Editor.Document.FontFamily = Editor.FontFamily;
        Editor.Document.FontSize = Editor.FontSize;
    }

    private async Task DetectClaudeAccountAsync()
    {
        try
        {
            var repo = _vm.Repo;
            if (!string.IsNullOrWhiteSpace(repo.GetSetting("claude.email"))) return;
            if (repo.GetSetting("claude.mode") == nameof(ClaudeAuthMode.ApiKey)) return;
            var opt = _vm.Claude.BuildOptions();
            if (opt.CliPath.Length == 0) return;
            opt.Timeout = TimeSpan.FromSeconds(20);
            var st = await new ClaudeService().GetAuthStatusAsync(opt, CancellationToken.None);
            if (st.LoggedIn && !string.IsNullOrWhiteSpace(st.Email))
            {
                repo.SetSetting("claude.email", st.Email);
                _vm.Claude.RefreshAccount();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Claude account detection failed", ex);
        }
    }

    public static List<FontItem> SystemFonts()
    {
        if (_fontCache is not null) return _fontCache;
        var lang = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        var list = new List<FontItem>();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var name = f.FamilyNames.TryGetValue(lang, out var localized) ? localized : f.Source;
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new FontItem(name, f));
        }
        _fontCache = list.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return _fontCache;
    }

    public static List<string> SystemFontNames() => SystemFonts().Select(f => f.Name).ToList();

    private static double PtToPx(double pt) => Math.Round(pt * 96.0 / 72.0, 3);
    private static double PxToPt(double px) => Math.Round(px * 72.0 / 96.0, 1);

    // ================================================================ view-model glue

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsReadAllMode):
                UpdateEditorVisibility();
                if (_vm.IsReadAllMode) BuildReadAll();
                break;
            case nameof(MainViewModel.Entries):
            case nameof(MainViewModel.ListSubtitle):
                if (_vm.IsReadAllMode) BuildReadAll();
                break;
            case nameof(MainViewModel.IsDateMode):
            case nameof(MainViewModel.IsPageMode):
            case nameof(MainViewModel.CalendarHasDates):
                DateNavPanel.Visibility = _vm.CalendarHasDates ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(MainViewModel.SelectedDate):
                if (DateCalendar.SelectedDate != _vm.SelectedDate)
                {
                    DateCalendar.SelectedDate = _vm.SelectedDate;
                    DateCalendar.DisplayDate = _vm.SelectedDate;
                }
                break;
        }
    }

    private void UpdateEditorVisibility()
    {
        var readAll = _vm.IsReadAllMode;
        ReadAllPanel.Visibility = readAll ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = !readAll && _vm.CurrentEntry is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = !readAll && _vm.CurrentEntry is null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ entry loading / saving

    private void LoadEntry(Entry? entry)
    {
        SaveNow();
        if (_subscribed is not null) _subscribed.PropertyChanged -= CurrentEntry_PropertyChanged;
        _subscribed = null;

        _loading = true;
        try
        {
            var full = entry is null ? null : (entry.Content is null && entry.PlainText.Length == 0 && entry.Id > 0 ? _vm.Repo.GetEntry(entry.Id) ?? entry : entry);
            _vm.CurrentEntry = full;
            Editor.IsUndoEnabled = false;
            if (full is null)
            {
                Editor.Document = new FlowDocument();
            }
            else
            {
                var doc = new FlowDocument { FontFamily = Editor.FontFamily, FontSize = Editor.FontSize };
                if (full.IsPage) PageService.Load(doc, full.Content, Loc.Language);
                else DocumentService.Load(doc, full.Content);
                if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
                Editor.Document = doc;
                EntryMetaPanel.Visibility = full.IsPage ? Visibility.Collapsed : Visibility.Visible;
                EntryDatePanel.Visibility = full.HasDate ? Visibility.Visible : Visibility.Collapsed; // information notes have no date
                DeleteEntryButton.Visibility = full.IsPage ? Visibility.Collapsed : Visibility.Visible;
                ApplyPaper(full.PaperColor, full.PaperStyle);
                TopicCombo.SelectedValue = full.TopicId;
                EntryDatePicker.SelectedDate = full.EntryDate;
                StatusCombo.SelectedItem = full.Status;
                CreatedText.Text = Loc.F("S.Created", full.CreatedAt.ToString("g", Loc.Culture));
                full.PropertyChanged += CurrentEntry_PropertyChanged;
                _subscribed = full;
                SaveStatus.Text = Loc.F("S.Saved", full.UpdatedAt.ToString("t", Loc.Culture));
            }
            Editor.IsUndoEnabled = true;
            _dirty = false;
            _dividerCount = PageService.CountDividers(Editor.Document);
            UpdateEditorVisibility();
            UpdateWordCount();
            SyncToolbar();
        }
        finally
        {
            _loading = false;
        }
        if (_vm.CurrentEntry is { IsPage: true })
        {
            // pages are dateless by default: open on the selected day if the page has it, otherwise at the end
            if (PageService.Days(Editor.Document).Any(d => d.Day == _vm.SelectedDate.Date))
            {
                JumpToDay(_vm.SelectedDate.Date, createIfToday: false);
            }
            else
            {
                RefreshPageDays();
                Editor.CaretPosition = Editor.Document.ContentEnd;
            }
            return;
        }
        if (_vm.IsSearching && _vm.CurrentEntry is not null && SelectSearchHit(_vm.SearchText, fromStart: true)) return;
        if (entry is not null && entry.PlainText.Length == 0 && string.IsNullOrEmpty(entry.Title) && entry.CreatedAt > DateTime.Now.AddSeconds(-5))
            Dispatcher.BeginInvoke(() => { TitleBox.Focus(); }, DispatcherPriority.Input);
    }

    private void CurrentEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Entry.HasDate) && sender is Entry changed)
            EntryDatePanel.Visibility = changed.HasDate ? Visibility.Visible : Visibility.Collapsed; // topic type switched
        if (_loading) return;
        if (e.PropertyName is nameof(Entry.Title) or nameof(Entry.Status) or nameof(Entry.EntryDate) or nameof(Entry.TopicId))
            MarkDirty();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        SaveStatus.Text = Loc.T("S.Unsaved");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Persists the open entry if it has unsaved changes. Safe to call at any time.</summary>
    public void SaveNow()
    {
        _saveTimer.Stop();
        if (!_dirty || _vm.CurrentEntry is null || _loading) return;
        var entry = _vm.CurrentEntry;
        try
        {
            entry.Content = entry.IsPage ? PageService.Save(Editor.Document) : DocumentService.Save(Editor.Document);
            entry.PlainText = entry.IsPage ? PageService.GetPlainText(Editor.Document, Loc.Language) : DocumentService.GetPlainText(Editor.Document);
            entry.Title = entry.Title.Trim();
            _vm.Repo.UpdateEntry(entry);
            _dirty = false;
            _vm.OnEntrySaved(entry);
            SaveStatus.Text = Loc.F("S.Saved", entry.UpdatedAt.ToString("T", Loc.Culture));
        }
        catch (Exception ex)
        {
            Logger.Error("Save failed", ex);
            SaveStatus.Text = Loc.F("S.SaveFailed", ex.Message);
        }
    }

    private void UpdateWordCount()
    {
        if (_vm.CurrentEntry is null) return;
        var text = DocumentService.GetPlainText(Editor.Document);
        WordCount.Text = Loc.F("S.Words", DocumentService.CountWords(text), text.Count(c => !char.IsWhiteSpace(c)));
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (_vm.CurrentEntry is { IsPage: true })
        {
            var n = PageService.CountDividers(Editor.Document);
            // Undo/Redo only replay the user's own steps (e.g. taking back "insert today"): accept them as they are.
            // Undoing again here would swallow a further step, such as text typed before the date.
            if (n < _dividerCount && e.UndoAction is not (UndoAction.Undo or UndoAction.Redo))
            {
                // a date divider was removed (cut, drag, typing over a selection…): take the edit back
                Dispatcher.BeginInvoke(() =>
                {
                    if (Editor.CanUndo) Editor.Undo();
                    // the refused edit now sits on the redo stack: drop the history so Ctrl+Y cannot replay it
                    Editor.IsUndoEnabled = false;
                    Editor.IsUndoEnabled = true;
                    _dividerCount = PageService.CountDividers(Editor.Document);
                    SaveStatus.Text = Loc.T("S.PageDividerLocked");
                    RefreshPageDays();
                }, DispatcherPriority.Send);
                return;
            }
            if (n != _dividerCount) RefreshPageDays();
            _dividerCount = n;
        }
        MarkDirty();
        _countTimer.Stop();
        _countTimer.Start();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Editor.Focus();
            Editor.CaretPosition = Editor.Document.ContentStart;
            e.Handled = true;
        }
    }

    private void TopicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm.CurrentEntry is null || TopicCombo.SelectedItem is not Topic t || t.Id == _vm.CurrentEntry.TopicId) return;
        var entry = _vm.CurrentEntry;
        entry.TopicId = t.Id;
        entry.TopicName = t.Name;
        entry.TopicColor = t.Color;
        entry.HasDate = !t.IsInfo;
        EntryDatePanel.Visibility = entry.HasDate ? Visibility.Visible : Visibility.Collapsed;
        SaveNow();
        _vm.FollowEntry(entry);
    }

    private void EntryDatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm.CurrentEntry is null) return;
        var entry = _vm.CurrentEntry;
        if (EntryDatePicker.SelectedDate is not DateTime d)
        {
            EntryDatePicker.SelectedDate = entry.EntryDate;
            return;
        }
        if (d.Date == entry.EntryDate.Date) return;
        entry.EntryDate = d.Date;
        SaveNow();
        _vm.FollowEntry(entry);
    }

    private void StatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _vm.CurrentEntry is null || StatusCombo.SelectedItem is not EntryStatus s) return;
        if (_vm.CurrentEntry.Status == s) return;
        _vm.CurrentEntry.Status = s;
        SaveNow();
    }

    // ================================================================ commands

    private void NewEntry_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vm.IsPageMode)
        {
            // a page has no separate entries: "new" means start today's section
            InsertTodayOnPage();
            return;
        }
        var topicId = _vm.SelectedTopicId ?? _vm.CurrentEntry?.TopicId ?? _vm.RealTopics.FirstOrDefault()?.Id;
        if (topicId is null)
        {
            if (AddTopicInteractive() is { } created) topicId = created.Id;
            else return;
        }
        if (_vm.IsReadAllMode) _vm.IsReadAllMode = false;
        _vm.CreateEntry(topicId.Value);
    }

    private void SaveEntry_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _dirty = _dirty || _vm.CurrentEntry is not null;
        SaveNow();
    }

    private void FocusSearch_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void PrevDay_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _vm.IsDateMode = true;
        _vm.ShiftDay(-1);
    }

    private void NextDay_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _vm.IsDateMode = true;
        _vm.ShiftDay(1);
    }

    private void Today_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        _vm.IsDateMode = true;
        _vm.GoToday();
    }

    private void ToggleClaude_Executed(object sender, ExecutedRoutedEventArgs e) => SetClaudeOpen(!IsClaudeOpen);

    private void OpenSettings_Executed(object sender, ExecutedRoutedEventArgs e) => OpenSettings();

    private void AddTopic_Executed(object sender, ExecutedRoutedEventArgs e) => AddTopicInteractive();

    private void DeleteEntry_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vm.CurrentEntry is { IsPage: false } entry) DeleteEntryInteractive(entry);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.IsPageMode && e.Key == Key.Enter && _vm.CurrentEntry is { IsPage: true })
        {
            SearchInPage(_vm.SearchText, fromStart: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _vm.SearchText = "";
            Editor.Focus();
        }
        else if (e.Key == Key.Enter || e.Key == Key.Down)
        {
            if (EntryList.Items.Count > 0)
            {
                EntryList.SelectedIndex = Math.Max(0, EntryList.SelectedIndex);
                (EntryList.ItemContainerGenerator.ContainerFromIndex(EntryList.SelectedIndex) as ListBoxItem)?.Focus();
            }
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => _vm.SearchText = "";

    private void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.SelectedEntry is { } entry)
        {
            DeleteEntryInteractive(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.CurrentEntry is not null)
        {
            Editor.Focus();
            e.Handled = true;
        }
    }

    // ================================================================ topics

    private Topic? AddTopicInteractive()
    {
        var dlg = new TopicDialog(this, null, null, name => _vm.Repo.TopicNameExists(name));
        if (dlg.ShowDialog() != true) return null;
        return _vm.AddTopic(dlg.TopicName, dlg.Color, dlg.IsInfo);
    }

    private static Topic? TopicFromMenu(object sender) => (sender as FrameworkElement)?.DataContext as Topic;

    private void TopicList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TopicList.SelectedItem is Topic { IsAll: false } t) EditTopic(t);
    }

    private void EditTopic(Topic t)
    {
        var dlg = new TopicDialog(this, t.Name, t.Color, name => _vm.Repo.TopicNameExists(name, t.Id), t.IsInfo);
        if (dlg.ShowDialog() != true) return;
        SaveNow();
        _vm.SetTopicKind(t, dlg.IsInfo);
        if (dlg.TopicName != t.Name) _vm.RenameTopic(t, dlg.TopicName);
        if (!string.Equals(dlg.Color, t.Color, StringComparison.OrdinalIgnoreCase)) _vm.SetTopicColor(t, dlg.Color);
    }

    private void TopicRename_Click(object sender, RoutedEventArgs e)
    {
        if (TopicFromMenu(sender) is { IsAll: false } t) EditTopic(t);
    }

    private void TopicColor_Click(object sender, RoutedEventArgs e)
    {
        if (TopicFromMenu(sender) is { IsAll: false } t) EditTopic(t);
    }

    private void TopicUp_Click(object sender, RoutedEventArgs e)
    {
        if (TopicFromMenu(sender) is { IsAll: false } t) _vm.MoveTopic(t, -1);
    }

    private void TopicDown_Click(object sender, RoutedEventArgs e)
    {
        if (TopicFromMenu(sender) is { IsAll: false } t) _vm.MoveTopic(t, 1);
    }

    private void TopicDelete_Click(object sender, RoutedEventArgs e)
    {
        if (TopicFromMenu(sender) is not { IsAll: false } t) return;
        var r = MessageBox.Show(this, _vm.DeleteTopicConfirmation(t), Loc.T("S.DeleteTopic"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        // the open entry may belong to this topic: do not resurrect it; otherwise keep its pending edits
        if (_vm.CurrentEntry?.TopicId == t.Id) _dirty = false;
        else SaveNow();
        _vm.DeleteTopic(t);
    }

    // ================================================================ entries (list)

    private static Entry? EntryFromMenu(object sender) => (sender as FrameworkElement)?.DataContext as Entry;

    private void DeleteEntryInteractive(Entry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.Title) ? Loc.T("S.Untitled") : entry.Title;
        var r = MessageBox.Show(this, Loc.F("S.DeleteEntryConfirm", name), Loc.T("S.DeleteEntry"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r != MessageBoxResult.Yes) return;
        if (_vm.CurrentEntry?.Id == entry.Id) _dirty = false;
        _vm.DeleteEntry(entry);
    }

    private void EntryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFromMenu(sender) is { } entry) DeleteEntryInteractive(entry);
    }

    private void EntryDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFromMenu(sender) is not { } item) return;
        SaveNow();
        var src = _vm.Repo.GetEntry(item.Id);
        if (src is null) return;
        var copy = new Entry
        {
            TopicId = src.TopicId, TopicName = src.TopicName, TopicColor = src.TopicColor,
            EntryDate = src.EntryDate, Title = src.Title, Content = src.Content, PlainText = src.PlainText,
            PaperColor = src.PaperColor, PaperStyle = src.PaperStyle, Status = src.Status,
        };
        _vm.Repo.InsertEntry(copy);
        _vm.RefreshEntries(copy.Id);
        _vm.RefreshTopicCounts();
    }

    private void EntryMoveToday_Click(object sender, RoutedEventArgs e)
    {
        if (EntryFromMenu(sender) is not { } item) return;
        SaveNow();
        var full = _vm.Repo.GetEntry(item.Id);
        if (full is null) return;
        full.EntryDate = DateTime.Today;
        _vm.Repo.UpdateEntry(full);
        if (_vm.CurrentEntry?.Id == full.Id)
        {
            _loading = true;
            _vm.CurrentEntry.EntryDate = full.EntryDate;
            EntryDatePicker.SelectedDate = full.EntryDate;
            _loading = false;
        }
        _vm.FollowEntry(full);
    }

    // ================================================================ calendar

    private void DateCalendar_SelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DateCalendar.SelectedDate is DateTime d && d.Date != _vm.SelectedDate.Date) _vm.SelectedDate = d.Date;
    }

    private void DateCalendar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        // the Calendar keeps mouse capture after a click, which would swallow the next click elsewhere
        if (Mouse.Captured is CalendarItem) Mouse.Capture(null);
        if (e.OriginalSource is FrameworkElement fe && VisualTreeHelperEx.FindParent<CalendarDayButton>(fe) is not null)
            Dispatcher.BeginInvoke(() => CalendarPopup.IsOpen = false, DispatcherPriority.Input);
    }

    // ================================================================ toolbar

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => SyncToolbar();

    private void SyncToolbar()
    {
        if (_syncingToolbar || _vm.CurrentEntry is null) return;
        _syncingToolbar = true;
        try
        {
            var sel = Editor.Selection;
            BoldBtn.IsChecked = Equals(sel.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
            ItalicBtn.IsChecked = Equals(sel.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
            var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            UnderlineBtn.IsChecked = deco is not null && deco.Any(d => d.Location == TextDecorationLocation.Underline);
            StrikeBtn.IsChecked = deco is not null && deco.Any(d => d.Location == TextDecorationLocation.Strikethrough);

            var align = sel.GetPropertyValue(Paragraph.TextAlignmentProperty);
            AlignLeftBtn.IsChecked = Equals(align, TextAlignment.Left);
            AlignCenterBtn.IsChecked = Equals(align, TextAlignment.Center);
            AlignRightBtn.IsChecked = Equals(align, TextAlignment.Right);
            JustifyBtn.IsChecked = Equals(align, TextAlignment.Justify);

            if (sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily ff)
            {
                var item = SystemFonts().FirstOrDefault(f => f.Family.Source == ff.Source || f.Name == ff.Source);
                FontCombo.SelectedItem = item;
                if (item is null) FontCombo.Text = ff.Source;
            }
            else
            {
                FontCombo.SelectedItem = null;
                FontCombo.Text = "";
            }
            FontCombo.ToolTip = FontCombo.Text;
            var sizeText = sel.GetPropertyValue(TextElement.FontSizeProperty) is double px ? PxToPt(px).ToString(CultureInfo.InvariantCulture) : "";
            SizeCombo.SelectedItem = (SizeCombo.ItemsSource as List<string>)?.FirstOrDefault(s => s == sizeText);
            if (SizeCombo.SelectedItem is null) SizeCombo.Text = sizeText;

            var inHyperlink = FindHyperlink(sel.Start) is not null;
            LinkHint.Visibility = inHyperlink ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _syncingToolbar = false;
        }
    }

    private void ApplyToSelection(DependencyProperty prop, object value)
    {
        if (_vm.CurrentEntry is null) return;
        Editor.Selection.ApplyPropertyValue(prop, value);
        MarkDirty();
        Editor.Focus();
    }

    // The combos are only applied on explicit user actions (pick from the list, Enter, leaving the box).
    // Reacting to SelectionChanged would also fire when SyncToolbar mirrors the caret formatting, and an
    // ApplyPropertyValue at that moment wipes pending (spring-loaded) formatting such as a Ctrl+B just pressed.
    private void FontCombo_DropDownClosed(object sender, EventArgs e) => ApplyFontText();

    private void FontCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyFontText();
            e.Handled = true;
        }
    }

    private void FontCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (FontCombo.IsDropDownOpen) return;
        ApplyFontText();
    }

    private void ApplyFontText()
    {
        if (_syncingToolbar) return;
        var name = FontCombo.Text.Trim();
        if (name.Length == 0) return;
        var item = SystemFonts().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));
        var family = item?.Family ?? new FontFamily(name);
        if (Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily cur && cur.Source == family.Source) return;
        ApplyToSelection(TextElement.FontFamilyProperty, family);
    }

    private void SizeCombo_DropDownClosed(object sender, EventArgs e) => ApplySizeText(SizeCombo.Text);

    private void SizeCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplySizeText(SizeCombo.Text);
            e.Handled = true;
        }
    }

    private void SizeCombo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SizeCombo.IsDropDownOpen) return;
        ApplySizeText(SizeCombo.Text);
    }

    private void ApplySizeText(string text)
    {
        if (_syncingToolbar) return;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pt) || pt < 4 || pt > 200) return;
        var px = PtToPx(pt);
        if (Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double cur && Math.Abs(cur - px) < 0.01) return;
        ApplyToSelection(TextElement.FontSizeProperty, px);
    }

    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        var deco = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var has = deco is not null && deco.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        var next = new TextDecorationCollection(deco?.Where(d => d.Location != TextDecorationLocation.Strikethrough) ?? Enumerable.Empty<TextDecoration>());
        if (!has) next.Add(TextDecorations.Strikethrough[0]);
        ApplyToSelection(Inline.TextDecorationsProperty, next);
        SyncToolbar();
    }

    private void FontColor_Click(object sender, RoutedEventArgs e)
    {
        _colorTarget = "fore";
        var cur = Editor.Selection.GetPropertyValue(TextElement.ForegroundProperty) as SolidColorBrush;
        ColorPaletteCtl.Configure(ColorPalette.TextColors, true, cur is null ? null : DocumentService.ToHex(cur.Color));
        ColorPopup.PlacementTarget = FontColorBtn;
        ColorPopup.IsOpen = true;
    }

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        _colorTarget = "back";
        var cur = Editor.Selection.GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
        ColorPaletteCtl.Configure(ColorPalette.HighlightColors, true, cur is null ? null : DocumentService.ToHex(cur.Color));
        ColorPopup.PlacementTarget = HighlightBtn;
        ColorPopup.IsOpen = true;
    }

    private void ColorPalette_ColorPicked(object? sender, string? hex)
    {
        ColorPopup.IsOpen = false;
        if (_colorTarget == "fore")
        {
            var brush = hex is null ? Editor.Foreground : DocumentService.BrushFromHex(hex);
            FontColorBar.Fill = brush;
            if (hex is null && !Editor.Selection.IsEmpty && _vm.CurrentEntry is not null)
            {
                // automatic: drop explicit colors so the text keeps following the paper's ink
                DocumentService.ClearForeground(Editor.Selection);
                MarkDirty();
                Editor.Focus();
            }
            else
            {
                ApplyToSelection(TextElement.ForegroundProperty, brush);
            }
        }
        else
        {
            var brush = hex is null ? Brushes.Transparent : DocumentService.BrushFromHex(hex);
            if (hex is not null) HighlightBar.Fill = brush;
            ApplyToSelection(TextElement.BackgroundProperty, brush);
        }
    }

    private void LineSpacing_Click(object sender, RoutedEventArgs e)
    {
        LineSpacingBtn.ContextMenu!.PlacementTarget = LineSpacingBtn;
        LineSpacingBtn.ContextMenu.Placement = PlacementMode.Bottom;
        LineSpacingBtn.ContextMenu.IsOpen = true;
    }

    private void LineSpacingItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var factor)) return;
        foreach (var p in SelectedParagraphs())
        {
            if (Math.Abs(factor - 1.0) < 0.01)
            {
                p.LineHeight = double.NaN;
            }
            else
            {
                p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                p.LineHeight = Math.Round(p.FontSize * 1.3 * factor, 2);
            }
        }
        MarkDirty();
        Editor.Focus();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Editor.Selection.Start.Paragraph;
        var end = Editor.Selection.End.Paragraph;
        if (start is null) yield break;
        Block? b = start;
        while (b is not null)
        {
            if (b is Paragraph p) yield return p;
            if (b == end) yield break;
            b = b.NextBlock;
        }
    }

    private void ClearFormat_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.Selection.IsEmpty) return;
        Editor.Selection.ClearAllProperties();
        MarkDirty();
        Editor.Focus();
        SyncToolbar();
    }

    // ================================================================ paper

    private void ApplyPaper(string color, PaperStyle style)
    {
        if (!DocumentService.TryParseColor(color, out var c)) c = Color.FromRgb(0xFF, 0xFD, 0xF5);
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        PaperBorder.Background = brush;
        PaperSwatch.Background = brush;
        Editor.Document.Background = DocumentService.CreatePaperPattern(style, c);
        var lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        var ink = lum > 0.5 ? (Brush)FindResource("Ink") : new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF7));
        Editor.Foreground = ink;
        Editor.CaretBrush = ink;
        _syncingToolbar = true;
        PaperPlain.IsChecked = style == PaperStyle.Plain;
        PaperLined.IsChecked = style == PaperStyle.Lined;
        PaperGrid.IsChecked = style == PaperStyle.Grid;
        PaperDots.IsChecked = style == PaperStyle.Dots;
        _syncingToolbar = false;
    }

    private void Paper_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        PaperPalette.Configure(ColorPalette.PaperColors, false, _vm.CurrentEntry.PaperColor);
        PaperPopup.IsOpen = true;
    }

    private void PaperPalette_ColorPicked(object? sender, string? hex)
    {
        if (_vm.CurrentEntry is null || hex is null) return;
        _vm.CurrentEntry.PaperColor = hex;
        ApplyPaper(hex, _vm.CurrentEntry.PaperStyle);
        MarkDirty();
    }

    private void PaperStyle_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar || _loading || _vm.CurrentEntry is null) return;
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<PaperStyle>(tag, out var style) && style != _vm.CurrentEntry.PaperStyle)
        {
            _vm.CurrentEntry.PaperStyle = style;
            ApplyPaper(_vm.CurrentEntry.PaperColor, style);
            MarkDirty();
        }
    }

    private void PaperDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        _vm.Repo.SetSetting("editor.paperColor", _vm.CurrentEntry.PaperColor);
        _vm.Repo.SetSetting("editor.paperStyle", _vm.CurrentEntry.PaperStyle.ToString());
        PaperPopup.IsOpen = false;
    }

    // ================================================================ links

    private static Hyperlink? FindHyperlink(TextPointer? pos) => DocumentService.FindHyperlink(pos);

    private void InsertLink_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        if (_vm.CurrentEntry.IsPage && !Editor.Selection.IsEmpty && SelectionHasDivider(Editor.Selection))
        {
            // the link would replace the selection, date included: refuse before asking for a URL
            SaveStatus.Text = Loc.T("S.PageDividerLocked");
            return;
        }
        var existing = FindHyperlink(Editor.Selection.Start) ?? FindHyperlink(Editor.Selection.End);
        if (existing is not null) EditHyperlink(existing);
        else InsertNewHyperlink();
    }

    private void InsertNewHyperlink()
    {
        var sel = Editor.Selection;
        var selectedText = sel.Text.Trim();
        var dlg = new LinkDialog(this, selectedText, LinkDialog.TryNormalize(selectedText, out var u) ? u.ToString() : null, false);
        if (dlg.ShowDialog() != true || dlg.Url is null) return;

        Editor.BeginChange();
        try
        {
            var text = string.IsNullOrWhiteSpace(dlg.LinkText) ? dlg.Url.ToString() : dlg.LinkText;
            if (!sel.IsEmpty) sel.Text = "";
            var insertAt = sel.Start;
            var link = DocumentService.InsertHyperlink(insertAt, text, dlg.Url);
            Editor.CaretPosition = link.ElementEnd;
            Editor.Selection.Select(link.ElementEnd, link.ElementEnd);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private void EditHyperlink(Hyperlink link)
    {
        var text = new TextRange(link.ContentStart, link.ContentEnd).Text;
        var dlg = new LinkDialog(this, text, link.NavigateUri?.ToString(), true);
        if (dlg.ShowDialog() != true) return;
        Editor.BeginChange();
        try
        {
            if (dlg.RemoveRequested)
            {
                RemoveHyperlink(link);
            }
            else if (dlg.Url is not null)
            {
                link.NavigateUri = dlg.Url;
                if (dlg.LinkText != text)
                {
                    link.Inlines.Clear();
                    link.Inlines.Add(new Run(string.IsNullOrWhiteSpace(dlg.LinkText) ? dlg.Url.ToString() : dlg.LinkText));
                }
            }
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private static void RemoveHyperlink(Hyperlink link)
    {
        var text = new TextRange(link.ContentStart, link.ContentEnd).Text;
        var pos = link.ElementStart;
        if (link.Parent is Paragraph p) p.Inlines.Remove(link);
        else if (link.Parent is Span s) s.Inlines.Remove(link);
        else return;
        pos.InsertTextInRun(text);
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUri(e.Uri);
        e.Handled = true;
    }

    private void OpenUri(Uri? uri)
    {
        if (uri is null) return;
        try
        {
            switch (LinkNormalizer.Classify(uri))
            {
                case LinkOpenAction.Open:
                    Process.Start(new ProcessStartInfo(LinkNormalizer.LaunchTarget(uri)) { UseShellExecute = true });
                    break;
                case LinkOpenAction.Reveal:
                    Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", uri.LocalPath } });
                    break;
                default:
                    SaveStatus.Text = Loc.F("S.LinkBlocked", uri.Scheme == "file" ? uri.LocalPath : uri.Scheme + ":");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Open link failed", ex);
        }
    }

    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var over = ctrl && HyperlinkAt(e.GetPosition(Editor)) is not null;
        Editor.Cursor = over ? Cursors.Hand : Cursors.IBeam;
    }

    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        if (HyperlinkAt(e.GetPosition(Editor)) is { } link)
        {
            OpenUri(link.NavigateUri);
            e.Handled = true;
        }
    }

    private Hyperlink? HyperlinkAt(Point point)
    {
        var pos = Editor.GetPositionFromPoint(point, false);
        return pos is null ? null : FindHyperlink(pos);
    }

    private void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = new ContextMenu();
        var link = e.CursorLeft >= 0
            ? HyperlinkAt(new Point(e.CursorLeft, e.CursorTop))
            : FindHyperlink(Editor.Selection.Start);
        if (link is not null)
        {
            menu.Items.Add(new MenuItem { Header = Loc.T("S.LinkOpen"), Command = new RelayCommandSimple(() => OpenUri(link.NavigateUri)) });
            menu.Items.Add(new MenuItem { Header = Loc.T("S.LinkEdit"), Command = new RelayCommandSimple(() => EditHyperlink(link)) });
            menu.Items.Add(new MenuItem
            {
                Header = Loc.T("S.LinkCopy"),
                Command = new RelayCommandSimple(() => { if (link.NavigateUri is not null) Clipboard.SetText(link.NavigateUri.ToString()); }),
            });
            menu.Items.Add(new MenuItem
            {
                Header = Loc.T("S.LinkRemove"),
                Command = new RelayCommandSimple(() => { RemoveHyperlink(link); MarkDirty(); }),
            });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Cut, CommandTarget = Editor });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy, CommandTarget = Editor });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Paste, CommandTarget = Editor });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("S.Link"), Command = AppCommands.InsertLink, CommandTarget = this });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.SelectAll, CommandTarget = Editor });
        Editor.ContextMenu = menu;
    }

    /// <summary>Typing a space/enter after something that looks like a URL turns it into a link, like Word does.</summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        if (_vm.CurrentEntry.IsPage && WouldRemoveDivider(e.Key))
        {
            e.Handled = true;
            if (!TryRemoveEmptyDayBeforeCaret(e.Key)) SaveStatus.Text = Loc.T("S.PageDividerLocked");
            return;
        }
        if (e.Key is not (Key.Space or Key.Enter) || Keyboard.Modifiers != ModifierKeys.None) return;
        if (!Editor.Selection.IsEmpty) return;
        var caret = Editor.CaretPosition;
        Editor.BeginChange();
        try
        {
            var link = DocumentService.AutoLinkBefore(caret, out var hasTrailing);
            if (link is not null && !hasTrailing) Editor.CaretPosition = link.ElementEnd;
        }
        catch (Exception ex)
        {
            Logger.Error("Auto-link failed", ex);
        }
        finally
        {
            Editor.EndChange();
        }
    }

    private void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (_vm.CurrentEntry is { IsPage: true } && !Editor.Selection.IsEmpty && SelectionHasDivider(Editor.Selection))
        {
            // the paste would replace a date; the divider protection would then undo it and the pasted text would vanish
            e.CancelCommand();
            SaveStatus.Text = Loc.T("S.PageDividerLocked");
            return;
        }
        // Pasted rich content from browsers/Word can be huge: keep it to formatted text, no embedded objects.
        if (e.DataObject.GetDataPresent(DataFormats.Rtf) || e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text is not null && LinkDialog.TryNormalize(text.Trim(), out var uri) && !text.Trim().Contains('\n') && Editor.Selection.IsEmpty)
            {
                e.CancelCommand();
                Editor.BeginChange();
                try
                {
                    var link = DocumentService.InsertHyperlink(Editor.CaretPosition, text.Trim(), uri);
                    Editor.CaretPosition = link.ElementEnd;
                }
                finally
                {
                    Editor.EndChange();
                }
                MarkDirty();
            }
            return;
        }
        if (e.DataObject.GetDataPresent(DataFormats.FileDrop)) e.CancelCommand();
    }

    private void Editor_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || _vm.CurrentEntry is null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor), true) ?? Editor.CaretPosition;
        Editor.BeginChange();
        try
        {
            foreach (var f in files)
            {
                var link = DocumentService.InsertHyperlink(pos, System.IO.Path.GetFileName(f), new Uri(f));
                pos = link.ElementEnd;
                pos.InsertTextInRun(" ");
                pos = pos.GetPositionAtOffset(1) ?? pos;
            }
            Editor.CaretPosition = pos;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        e.Handled = true;
    }

    // ================================================================ export / print / read-all

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        ExportButton.ContextMenu!.PlacementTarget = ExportButton;
        ExportButton.ContextMenu.Placement = PlacementMode.Bottom;
        ExportButton.ContextMenu.IsOpen = true;
    }

    private static string SuggestedFileName(Entry entry) => ExportNaming.FileName(entry, DateTime.Today);

    private void ExportRtf_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        SaveNow();
        var dlg = new SaveFileDialog { Filter = "Rich Text (*.rtf)|*.rtf", FileName = SuggestedFileName(_vm.CurrentEntry) + ".rtf" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            using var fs = System.IO.File.Create(dlg.FileName);
            var source = _vm.CurrentEntry.IsPage ? PageService.CloneForExport(Editor.Document, Loc.Language) : Editor.Document;
            new TextRange(source.ContentStart, source.ContentEnd).Save(fs, DataFormats.Rtf);
            SaveStatus.Text = Loc.F("S.ExportDone", dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("S.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportTxt_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        SaveNow();
        var dlg = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = SuggestedFileName(_vm.CurrentEntry) + ".txt" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var header = ExportNaming.TextHeader(_vm.CurrentEntry);
            var body = _vm.CurrentEntry.IsPage ? DocumentService.GetPlainText(PageService.CloneForExport(Editor.Document, Loc.Language))
                : DocumentService.GetPlainText(Editor.Document);
            System.IO.File.WriteAllText(dlg.FileName, header + body);
            SaveStatus.Text = Loc.F("S.ExportDone", dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("S.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentEntry is null) return;
        SaveNow();
        var source = _vm.CurrentEntry.IsPage ? PageService.CloneForExport(Editor.Document, Loc.Language) : Editor.Document;
        PrintDocument(DocumentService.Save(source), _vm.CurrentEntry.Title);
    }

    private void PrintDocument(byte[] content, string title)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        try
        {
            var copy = new FlowDocument { FontFamily = Editor.FontFamily, FontSize = Editor.FontSize, PagePadding = new Thickness(60), ColumnWidth = double.PositiveInfinity };
            DocumentService.Load(copy, content);
            copy.PageWidth = dlg.PrintableAreaWidth;
            copy.PageHeight = dlg.PrintableAreaHeight;
            dlg.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, string.IsNullOrWhiteSpace(title) ? Loc.T("S.AppName") : title);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("S.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BuildReadAll()
    {
        SaveNow();
        var entries = _vm.Entries.ToList();
        var doc = DocumentService.BuildCombined(entries, id => _vm.Repo.GetEntry(id)?.Content, Editor.FontFamily.Source);
        doc.FontSize = Editor.FontSize;
        ReadAllViewer.Document = doc;
    }

    private void PrintReadAll_Click(object sender, RoutedEventArgs e)
    {
        if (ReadAllViewer.Document is null) return;
        PrintDocument(DocumentService.Save(ReadAllViewer.Document), _vm.ListTitle);
    }

    private void ExportReadAll_Click(object sender, RoutedEventArgs e)
    {
        if (ReadAllViewer.Document is null) return;
        var dlg = new SaveFileDialog { Filter = "Rich Text (*.rtf)|*.rtf", FileName = $"{Loc.T("S.AppName")} {DateTime.Today:yyyy-MM-dd}.rtf" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            using var fs = System.IO.File.Create(dlg.FileName);
            new TextRange(ReadAllViewer.Document.ContentStart, ReadAllViewer.Document.ContentEnd).Save(fs, DataFormats.Rtf);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Loc.T("S.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================================================================ claude panel

    private bool IsClaudeOpen => ClaudeColumn.Width.Value > 0;

    private void SetClaudeOpen(bool open)
    {
        ClaudeColumn.Width = open ? new GridLength(400) : new GridLength(0);
        if (open) Dispatcher.BeginInvoke(() => { ClaudeInput.Focus(); ChatScroll.ScrollToEnd(); }, DispatcherPriority.Input);
    }

    private void ClaudeButton_Click(object sender, RoutedEventArgs e) => SetClaudeOpen(!IsClaudeOpen);
    private void CloseClaude_Click(object sender, RoutedEventArgs e) => SetClaudeOpen(false);
    private void ClaudeAccount_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void ClaudeInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_vm.Claude.SendCommand.CanExecute(null)) _vm.Claude.SendCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            SetClaudeOpen(false);
            Editor.Focus();
        }
    }

    private void ChatCopy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage m)
        {
            try { Clipboard.SetText(m.Content); } catch (Exception ex) { Logger.Error("Clipboard failed", ex); }
        }
    }

    private void ChatInsert_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChatMessage m) InsertTextIntoEntry(m.Content);
    }

    private ClaudeWebWindow? _claudeWeb;

    /// <summary>claude.ai in an embedded browser: ask about the journal with the user's own Claude login, no API key.</summary>
    private void ClaudeWeb_Click(object sender, RoutedEventArgs e)
    {
        if (_claudeWeb is { IsLoaded: true })
        {
            if (_claudeWeb.WindowState == WindowState.Minimized) _claudeWeb.WindowState = WindowState.Normal;
            _claudeWeb.Activate();
            return;
        }
        _claudeWeb = new ClaudeWebWindow(this, (scope, question) =>
        {
            SaveNow();
            var entries = _vm.Claude.CollectEntries(scope);
            var prompt = ClaudePromptBuilder.Build(question, entries, Loc.T("S.Claude.Scope." + scope),
                Array.Empty<ChatMessage>(), DateTime.Today, Loc.Language);
            return (prompt, entries.Count);
        }, InsertTextIntoEntry);
        _claudeWeb.Closed += (_, _) => _claudeWeb = null;
        _claudeWeb.Show();
    }

    /// <summary>Appends plain text as new paragraphs at the end of the open entry. False when no entry is open.</summary>
    private bool InsertTextIntoEntry(string text)
    {
        if (_vm.CurrentEntry is null || _vm.IsReadAllMode) return false;
        if (_vm.CurrentEntry.IsPage)
        {
            Editor.BeginChange();
            try
            {
                // today's section if the page has one, otherwise the end of the page (no date is added for this)
                var todayDivider = PageService.Days(Editor.Document).FirstOrDefault(d => d.Day == DateTime.Today).Divider;
                Paragraph anchor;
                if (todayDivider is not null) anchor = PageService.LastParagraphOfSection(Editor.Document, todayDivider);
                else if (Editor.Document.Blocks.LastBlock is Paragraph last) anchor = last;
                else Editor.Document.Blocks.Add(anchor = new Paragraph());
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    var p = new Paragraph(new Run(line));
                    Editor.Document.Blocks.InsertAfter(anchor, p);
                    anchor = p;
                }
                Editor.CaretPosition = anchor.ContentEnd;
            }
            finally
            {
                Editor.EndChange();
            }
            MarkDirty();
            Editor.Focus();
            return true;
        }
        Editor.BeginChange();
        try
        {
            var doc = Editor.Document;
            var last = doc.Blocks.LastBlock;
            var empty = last is Paragraph lp && new TextRange(lp.ContentStart, lp.ContentEnd).Text.Length == 0;
            if (!empty) doc.Blocks.Add(new Paragraph());
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)));
            Editor.CaretPosition = doc.ContentEnd;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
        Editor.ScrollToEnd();
        return true;
    }

    // ================================================================ one page per topic

    private void InsertToday_Executed(object sender, ExecutedRoutedEventArgs e) => InsertTodayOnPage();

    /// <summary>Adds today's date divider to the page (only on request) and moves the caret below it.</summary>
    internal void InsertTodayOnPage()
    {
        if (_vm.CurrentEntry is not { IsPage: true }) return;
        JumpToDay(DateTime.Today, createIfToday: true);
        if (_vm.SelectedDate.Date != DateTime.Today) _vm.SelectedDate = DateTime.Today; // jumps to the section just made
    }

    /// <summary>Puts the caret at the end of <paramref name="day"/>'s section (creating today's divider when asked).</summary>
    internal bool JumpToDay(DateTime day, bool createIfToday)
    {
        if (_vm.CurrentEntry is not { IsPage: true }) return false;
        var doc = Editor.Document;
        Paragraph? target;
        if (createIfToday && day.Date == DateTime.Today)
        {
            var before = PageService.CountDividers(doc);
            Editor.BeginChange();
            try { target = PageService.EnsureDay(doc, day, Loc.Language); }
            finally { Editor.EndChange(); }
            if (PageService.CountDividers(doc) != before)
            {
                _dividerCount = PageService.CountDividers(doc);
                MarkDirty();
            }
        }
        else
        {
            var divider = PageService.Days(doc).FirstOrDefault(d => d.Day == day.Date).Divider;
            target = divider is null ? null : PageService.LastParagraphOfSection(doc, divider);
        }
        RefreshPageDays();
        if (target is null)
        {
            SaveStatus.Text = Loc.F("S.PageDateNotFound", PageService.DayLabel(day, Loc.Language));
            return false;
        }
        Editor.CaretPosition = target.ContentEnd;
        Dispatcher.BeginInvoke(() =>
        {
            target.BringIntoView();
            if (IsActive) Editor.Focus();
        }, DispatcherPriority.Loaded);
        return true;
    }

    private void SearchInPage(string query, bool fromStart)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        if (PageService.FindDay(Editor.Document, query) is { } divider)
        {
            var day = PageService.DayOf(divider)!.Value;
            if (_vm.SelectedDate.Date == day) JumpToDay(day, createIfToday: false);
            else _vm.SelectedDate = day; // raises PageDayRequested
            return;
        }
        if (PageService.TryParseDateQuery(query, out _, out _, out _))
        {
            SaveStatus.Text = Loc.F("S.PageDateNotFound", query);
            return;
        }
        if (!SelectSearchHit(query, fromStart)) SaveStatus.Text = Loc.F("S.PageTextNotFound", query);
    }

    /// <summary>
    /// Selects the next occurrence of <paramref name="query"/> (the first one when <paramref name="fromStart"/>) and
    /// scrolls to it. Focus is left where it is, so Enter in the search box keeps moving to the next hit; the
    /// selection stays highlighted and becomes the caret position as soon as the editor gets focus.
    /// </summary>
    private bool SelectSearchHit(string query, bool fromStart)
    {
        var hit = DocumentSearch.Find(Editor.Document, query, fromStart ? null : Editor.Selection.End);
        if (hit is null) return false;
        Editor.Selection.Select(hit.Start, hit.End);
        Dispatcher.BeginInvoke(() =>
        {
            var rect = hit.Start.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty) Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - Editor.ViewportHeight / 3));
        }, DispatcherPriority.Loaded);
        return true;
    }

    private void RefreshPageDays()
    {
        if (_vm.CurrentEntry is not { IsPage: true })
        {
            if (_vm.PageDays.Count > 0) _vm.SetPageDays(Array.Empty<PageDay>());
            return;
        }
        var today = DateTime.Today;
        var days = PageService.Days(Editor.Document)
            .Select(d => new PageDay(d.Day, d.Day.Year == today.Year ? PageService.DayLabel(d.Day, Loc.Language)
                : PageService.YearLabel(d.Day.Year, Loc.Language) + " " + PageService.DayLabel(d.Day, Loc.Language), d.Day == today))
            .Reverse() // newest first
            .ToList();
        if (days.SequenceEqual(_vm.PageDays)) return;
        _vm.SetPageDays(days);
    }

    private void PageDayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageDayList.SelectedItem is not PageDay d) return;
        PageDayList.SelectedItem = null; // a list of shortcuts, not a selection
        if (_vm.SelectedDate.Date == d.Date) JumpToDay(d.Date, createIfToday: false);
        else _vm.SelectedDate = d.Date;
    }

    /// <summary>Backspace/Delete next to a divider would delete it; so would typing over a selection that contains one.</summary>
    private bool WouldRemoveDivider(Key key)
    {
        var sel = Editor.Selection;
        if (!sel.IsEmpty)
        {
            // anything that replaces the selection: deletion, Enter, letters/digits, numpad, punctuation, Korean IME input
            var replaces = key is Key.Back or Key.Delete or Key.Enter or Key.Space or Key.ImeProcessed
                || (key >= Key.D0 && key <= Key.Z) || (key >= Key.NumPad0 && key <= Key.Divide) || key >= Key.Oem1;
            return replaces && SelectionHasDivider(sel);
        }
        var para = sel.Start.Paragraph;
        if (para is null) return false;
        // compare by text, not symbol offsets: run boundaries sit between the caret and the paragraph edge
        if (key == Key.Back && new TextRange(para.ContentStart, sel.Start).Text.Length == 0) return PageService.IsDivider(para.PreviousBlock);
        if (key == Key.Delete && new TextRange(sel.Start, para.ContentEnd).Text.Length == 0) return PageService.IsDivider(para.NextBlock);
        return false;
    }

    /// <summary>
    /// Backspace right below a date that has nothing written under it removes that date (e.g. one added by mistake);
    /// dates with notes stay locked.
    /// </summary>
    private bool TryRemoveEmptyDayBeforeCaret(Key key)
    {
        if (key != Key.Back || !Editor.Selection.IsEmpty || Editor.Selection.Start.Paragraph is not { } para) return false;
        if (para.PreviousBlock is not BlockUIContainer divider || PageService.DayOf(divider) is null || !PageService.IsSectionEmpty(divider)) return false;
        var label = PageService.DayLabel(PageService.DayOf(divider)!.Value, Loc.Language);
        Editor.BeginChange();
        try
        {
            var caret = PageService.RemoveEmptyDay(Editor.Document, divider);
            _dividerCount = PageService.CountDividers(Editor.Document); // a deliberate removal, not one to undo
            if (caret is not null) Editor.CaretPosition = caret.ContentEnd;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        RefreshPageDays();
        SaveStatus.Text = Loc.F("S.PageDayRemoved", label);
        return true;
    }

    private bool SelectionHasDivider(TextRange sel) =>
        Editor.Document.Blocks.Any(b => PageService.IsDivider(b) && b.ElementStart.CompareTo(sel.End) < 0 && b.ElementEnd.CompareTo(sel.Start) > 0);

    /// <summary>Midnight passed while the app was open: today's divider appears and the caret follows.</summary>
    private void CheckDayChanged()
    {
        var now = DateTime.Today;
        if (now == _today) return;
        var previous = _today;
        _today = now;
        if (_vm.IsPageMode || _vm.IsDateMode)
        {
            // follow the day (date mode reloads; a page jumps if it already has the day — dates are never added on their own)
            if (_vm.SelectedDate.Date == previous) _vm.SelectedDate = now;
        }
        RefreshPageDays();
    }

    // ================================================================ settings

    private void OpenSettings()
    {
        SaveNow();
        var dlg = new SettingsDialog(this, _vm.Repo);
        if (dlg.ShowDialog() != true) return;
        _vm.Claude.RefreshAccount();
        if (dlg.EditorDefaultsChanged) ApplyEditorDefaults();
        if (dlg.LanguageChanged)
            MessageBox.Show(this, Loc.T("S.Settings.LangRestart"), Loc.T("S.Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

internal sealed class RelayCommandSimple(Action action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
}
