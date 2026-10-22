using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SokoNote.Data;
using SokoNote.Models;
using SokoNote.Services;

namespace SokoNote.ViewModels;

/// <summary>
/// Navigation state: topics on the left, the filtered entry list in the middle and the entry open in the editor.
/// The rich-text editor itself lives in MainWindow code-behind.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _searchDebounce;
    private bool _suppressRefresh;

    public WorkLogRepository Repo { get; }
    public ObservableCollection<Topic> Topics { get; } = new();
    public ObservableCollection<Topic> RealTopics { get; } = new();
    public ObservableCollection<Entry> Entries { get; } = new();
    public ClaudeViewModel Claude { get; }

    [ObservableProperty] private Topic? _selectedTopic;
    [ObservableProperty] private bool _isDateMode = true;
    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _statusFilterIndex;
    [ObservableProperty] private Entry? _selectedEntry;
    [ObservableProperty] private Entry? _currentEntry;
    [ObservableProperty] private string _listTitle = "";
    [ObservableProperty] private string _listSubtitle = "";
    [ObservableProperty] private bool _isReadAllMode;
    [ObservableProperty] private bool _isListTruncated;
    [ObservableProperty] private bool _isPageMode;

    /// <summary>Date dividers of the open topic page, shown as the middle list in page mode (filled by the window).</summary>
    public ObservableCollection<PageDay> PageDays { get; } = new();

    /// <summary>Raised after a search hit on a topic page was opened: find this text in the page.</summary>
    public event EventHandler<string>? PageSearchRequested;

    /// <summary>Raised in page mode when the user picks a day (list, calendar, previous/next): move the caret there.</summary>
    public event EventHandler<DateTime>? PageDayRequested;

    // three-way view switch: by date | all | one page
    public bool IsByDateView
    {
        get => IsDateMode && !IsPageMode;
        set { if (value) { IsPageMode = false; IsDateMode = true; } }
    }

    public bool IsAllView
    {
        get => !IsDateMode && !IsPageMode;
        set { if (value) { IsPageMode = false; IsDateMode = false; } }
    }

    /// <summary>Most entries the middle list shows at once; more than that is reported in the subtitle.</summary>
    public int ListLimit { get; set; } = 5000;

    /// <summary>Raised when the list selection asks the editor to open another entry (null closes it).</summary>
    public event EventHandler<Entry?>? OpenEntryRequested;

    /// <summary>Raised before the list is reloaded so the editor can flush pending changes.</summary>
    public event EventHandler? BeforeNavigate;

    public MainViewModel(WorkLogRepository repo)
    {
        Repo = repo;
        Claude = new ClaudeViewModel(this);
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RefreshEntries();
        };
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public long? SelectedTopicId => SelectedTopic is { IsAll: false } t ? t.Id : null;
    public string SelectedDateText => Loc.LongDate(SelectedDate);

    public void Initialize()
    {
        LoadTopics(null);
    }

    // ---------------------------------------------------------------- topics

    public void LoadTopics(long? selectId)
    {
        var keep = selectId ?? SelectedTopic?.Id ?? 0;
        var topics = Repo.GetTopics();
        _suppressRefresh = true;
        try
        {
            Topics.Clear();
            RealTopics.Clear();
            Topics.Add(new Topic { Id = 0, Name = Loc.T("S.AllTopics"), Color = "#8B93A7", EntryCount = topics.Sum(t => t.EntryCount) });
            foreach (var t in topics)
            {
                Topics.Add(t);
                RealTopics.Add(t);
            }
            SelectedTopic = Topics.FirstOrDefault(t => t.Id == keep) ?? Topics[0];
        }
        finally
        {
            _suppressRefresh = false;
        }
        RefreshEntries(CurrentEntry?.Id);
    }

    /// <summary>Updates the counters without rebuilding the topic list.</summary>
    public void RefreshTopicCounts()
    {
        var fresh = Repo.GetTopics().ToDictionary(t => t.Id);
        foreach (var t in RealTopics)
            if (fresh.TryGetValue(t.Id, out var f)) t.EntryCount = f.EntryCount;
        if (Topics.Count > 0 && Topics[0].IsAll) Topics[0].EntryCount = fresh.Values.Sum(t => t.EntryCount);
    }

    public Topic AddTopic(string name, string color)
    {
        var t = Repo.AddTopic(name, color);
        LoadTopics(t.Id);
        return t;
    }

    public void RenameTopic(Topic t, string name)
    {
        Repo.RenameTopic(t.Id, name);
        t.Name = name.Trim();
        foreach (var e in Entries.Where(e => e.TopicId == t.Id)) e.TopicName = t.Name;
        if (CurrentEntry?.TopicId == t.Id) CurrentEntry.TopicName = t.Name;
        if (IsPageMode && SelectedTopic?.Id == t.Id) ListTitle = Loc.F("S.PageTitle", t.Name);
        UpdateListCaptionIfListMode();
    }

    /// <summary>The list subtitle names the selected topic; refresh it after a rename (page mode has its own title).</summary>
    private void UpdateListCaptionIfListMode()
    {
        if (!IsPageMode) UpdateListCaption();
    }

    public void SetTopicColor(Topic t, string color)
    {
        Repo.SetTopicColor(t.Id, color);
        t.Color = color;
        foreach (var e in Entries.Where(e => e.TopicId == t.Id)) e.TopicColor = color;
        if (CurrentEntry?.TopicId == t.Id) CurrentEntry.TopicColor = color;
    }

    public void DeleteTopic(Topic t)
    {
        Repo.DeleteTopic(t.Id);
        if (CurrentEntry?.TopicId == t.Id)
        {
            CurrentEntry = null;
            OpenEntryRequested?.Invoke(this, null);
        }
        LoadTopics(0);
    }

    public void MoveTopic(Topic t, int delta)
    {
        var idx = RealTopics.IndexOf(t);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= RealTopics.Count) return;
        var order = RealTopics.Select(x => x.Id).ToList();
        (order[idx], order[target]) = (order[target], order[idx]);
        Repo.ReorderTopics(order);
        LoadTopics(t.Id);
    }

    // ---------------------------------------------------------------- entries

    partial void OnSelectedTopicChanged(Topic? value)
    {
        if (!_suppressRefresh) RefreshEntries();
    }

    partial void OnIsDateModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsByDateView));
        OnPropertyChanged(nameof(IsAllView));
        RefreshEntries();
    }

    partial void OnIsPageModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsByDateView));
        OnPropertyChanged(nameof(IsAllView));
        if (value) IsReadAllMode = false;
        RefreshEntries();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        OnPropertyChanged(nameof(SelectedDateText));
        if (IsPageMode) PageDayRequested?.Invoke(this, value.Date); // same page, other day
        else if (!_suppressRefresh) RefreshEntries();
    }

    partial void OnStatusFilterIndexChanged(int value) => RefreshEntries();

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        if (IsPageMode) return; // in page mode the search box jumps to a date / text on Enter
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnSelectedEntryChanged(Entry? value)
    {
        if (_suppressRefresh) return;
        if (value is null) return; // list cleared or re-filtered; the editor decides what to do
        if (value.IsPage)
        {
            OpenPageForSearchHit(value);
            return;
        }
        if (CurrentEntry?.Id == value.Id) return;
        OpenEntryRequested?.Invoke(this, value);
    }

    public EntryQuery BuildListQuery()
    {
        var q = new EntryQuery { TopicId = SelectedTopicId, Limit = ListLimit + 1 }; // +1 tells whether more exist
        if (IsSearching)
        {
            q.Search = SearchText;
            q.IncludePages = true; // text written in one-page mode must be findable too
        }
        else if (IsDateMode) q.Date = SelectedDate.Date;
        if (StatusFilterIndex > 0) q.Status = (EntryStatus)(StatusFilterIndex - 1);
        return q;
    }

    /// <summary>
    /// Reloads the middle list. Keeps <paramref name="keepId"/> selected when it is still visible,
    /// otherwise opens the first entry, or closes the editor when nothing matches.
    /// </summary>
    public void RefreshEntries(long? keepId = null)
    {
        if (IsPageMode)
        {
            ShowTopicPage();
            return;
        }
        BeforeNavigate?.Invoke(this, EventArgs.Empty);
        keepId ??= CurrentEntry?.Id;

        var list = Repo.QueryEntries(BuildListQuery());
        foreach (var e in list.Where(e => e.IsPage && string.IsNullOrWhiteSpace(e.Title)))
            e.Title = Loc.F("S.PageTitle", e.TopicName);
        IsListTruncated = list.Count > ListLimit;
        if (IsListTruncated) list.RemoveRange(ListLimit, list.Count - ListLimit);
        _suppressRefresh = true;
        try
        {
            Entries.Clear();
            foreach (var e in list) Entries.Add(e);
            SelectedEntry = keepId is long k ? Entries.FirstOrDefault(e => e.Id == k) : null;
        }
        finally
        {
            _suppressRefresh = false;
        }

        if (SelectedEntry is null)
        {
            var next = Entries.FirstOrDefault(e => !e.IsPage); // a page hit only opens when clicked (it switches the view)
            if (next is not null) SelectedEntry = next; // opens it
            else if (CurrentEntry is not null) OpenEntryRequested?.Invoke(this, null);
        }

        UpdateListCaption();
        CalendarMarks.Set(Repo.GetEntryDates(SelectedTopicId));
    }

    /// <summary>Page mode: the editor shows the selected topic's single page; the middle list shows its days.</summary>
    private void ShowTopicPage()
    {
        BeforeNavigate?.Invoke(this, EventArgs.Empty);
        _suppressRefresh = true;
        try
        {
            Entries.Clear();
            SelectedEntry = null;
        }
        finally
        {
            _suppressRefresh = false;
        }
        IsListTruncated = false;
        if (SelectedTopic is { IsAll: false } topic)
        {
            var page = Repo.GetOrCreatePage(topic.Id);
            ListTitle = Loc.F("S.PageTitle", topic.Name);
            if (CurrentEntry?.Id != page.Id) OpenEntryRequested?.Invoke(this, page);
        }
        else
        {
            PageDays.Clear();
            CalendarMarks.Set(new HashSet<DateTime>());
            ListTitle = Loc.T("S.PagePickTopic");
            if (CurrentEntry is not null) OpenEntryRequested?.Invoke(this, null);
        }
        ListSubtitle = Loc.F("S.PageDayCount", PageDays.Count);
    }

    /// <summary>A search result that is a topic page: switch to that topic's page and look for the search text in it.</summary>
    private void OpenPageForSearchHit(Entry pageHit)
    {
        var query = SearchText.Trim();
        _suppressRefresh = true;
        try
        {
            SelectedTopic = Topics.FirstOrDefault(t => t.Id == pageHit.TopicId) ?? SelectedTopic;
            SearchText = "";
        }
        finally
        {
            _suppressRefresh = false;
        }
        _searchDebounce.Stop();
        IsPageMode = true; // opens the page
        if (query.Length > 0) PageSearchRequested?.Invoke(this, query);
    }

    /// <summary>Called by the window after it (re)built the page's day list.</summary>
    public void SetPageDays(IEnumerable<PageDay> days)
    {
        PageDays.Clear();
        foreach (var d in days) PageDays.Add(d);
        if (!IsPageMode) return;
        ListSubtitle = Loc.F("S.PageDayCount", PageDays.Count);
        CalendarMarks.Set(PageDays.Select(d => d.Date.Date).ToHashSet()); // the calendar navigates the page's days
    }

    private void UpdateListCaption()
    {
        if (IsSearching)
        {
            ListTitle = Loc.F("S.SearchResults", SearchText.Trim());
        }
        else if (IsDateMode)
        {
            ListTitle = SelectedDateText;
        }
        else
        {
            ListTitle = Loc.T("S.AllEntries");
        }
        var topic = SelectedTopic is { IsAll: false } t ? t.Name : Loc.T("S.AllTopics");
        ListSubtitle = IsListTruncated
            ? Loc.F("S.ListCountTruncated", topic, Entries.Count)
            : Loc.F("S.ListCount", topic, Entries.Count);
    }

    public Entry? CreateEntry(long topicId)
    {
        var topic = RealTopics.FirstOrDefault(t => t.Id == topicId);
        if (topic is null) return null;
        BeforeNavigate?.Invoke(this, EventArgs.Empty);
        var e = new Entry
        {
            TopicId = topic.Id,
            TopicName = topic.Name,
            TopicColor = topic.Color,
            EntryDate = IsDateMode && !IsSearching ? SelectedDate.Date : DateTime.Today,
            PaperColor = Repo.GetSetting("editor.paperColor", "#FFFDF5"),
            PaperStyle = Enum.TryParse<PaperStyle>(Repo.GetSetting("editor.paperStyle", "Plain"), out var ps) ? ps : PaperStyle.Plain,
            Content = null,
            PlainText = "",
        };
        Repo.InsertEntry(e);

        // make sure the new entry is visible in the list
        _suppressRefresh = true;
        try
        {
            SearchText = "";
            StatusFilterIndex = 0;
            if (SelectedTopic is { IsAll: false } st && st.Id != topic.Id) SelectedTopic = topic;
            if (IsDateMode) SelectedDate = e.EntryDate;
        }
        finally
        {
            _suppressRefresh = false;
        }
        _searchDebounce.Stop();

        var full = Repo.GetEntry(e.Id)!;
        OpenEntryRequested?.Invoke(this, full);
        RefreshEntries(e.Id);
        RefreshTopicCounts();
        return full;
    }

    /// <summary>Called by the editor after a save so the list card reflects the new title/preview without a reload.</summary>
    public void OnEntrySaved(Entry saved)
    {
        var item = Entries.FirstOrDefault(e => e.Id == saved.Id);
        if (item is null) return;
        item.Title = saved.Title;
        item.Preview = saved.Preview;
        item.Status = saved.Status;
        item.TopicId = saved.TopicId;
        item.TopicName = saved.TopicName;
        item.TopicColor = saved.TopicColor;
        item.EntryDate = saved.EntryDate;
        item.UpdatedAt = saved.UpdatedAt;
        item.PaperColor = saved.PaperColor;
    }

    /// <summary>The entry's date or topic moved: follow it so it stays visible.</summary>
    public void FollowEntry(Entry e)
    {
        _suppressRefresh = true;
        try
        {
            if (SelectedTopic is { IsAll: false } st && st.Id != e.TopicId)
                SelectedTopic = Topics.FirstOrDefault(t => t.Id == e.TopicId) ?? Topics[0];
            if (IsDateMode && !IsSearching) SelectedDate = e.EntryDate.Date;
        }
        finally
        {
            _suppressRefresh = false;
        }
        RefreshEntries(e.Id);
        RefreshTopicCounts();
    }

    public void DeleteEntry(Entry e)
    {
        Repo.DeleteEntry(e.Id);
        if (CurrentEntry?.Id == e.Id)
        {
            // close the editor explicitly: when the list ends up empty nothing else would
            CurrentEntry = null;
            OpenEntryRequested?.Invoke(this, null);
        }
        RefreshEntries();
        RefreshTopicCounts();
    }

    public void GoToday()
    {
        if (IsPageMode && SelectedDate.Date == DateTime.Today) PageDayRequested?.Invoke(this, DateTime.Today);
        SelectedDate = DateTime.Today;
    }
    public void ShiftDay(int days) => SelectedDate = SelectedDate.AddDays(days);
}

/// <summary>One day of a topic page, as listed in the middle column.</summary>
public sealed record PageDay(DateTime Date, string Label, bool IsToday);
