using CommunityToolkit.Mvvm.ComponentModel;

namespace SokoNote.Models;

public partial class Topic : ObservableObject
{
    /// <summary>0 is reserved for the virtual "all topics" item.</summary>
    public long Id { get; set; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "#5B8DEF";
    [ObservableProperty] private int _entryCount;

    /// <summary>The view this topic opens in ("date", "all" or "page"); null until one is chosen.</summary>
    [ObservableProperty] private string? _viewMode;

    public int SortOrder { get; set; }
    public bool IsAll => Id == 0;
}

public enum EntryStatus
{
    Note = 0,
    InProgress = 1,
    Done = 2,
    Hold = 3,
    Urgent = 4,
}

public enum PaperStyle
{
    Plain = 0,
    Lined = 1,
    Grid = 2,
    Dots = 3,
}

public partial class Entry : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty] private long _topicId;
    [ObservableProperty] private string _topicName = "";
    [ObservableProperty] private string _topicColor = "#5B8DEF";
    [ObservableProperty] private DateTime _entryDate = DateTime.Today;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _preview = "";
    [ObservableProperty] private EntryStatus _status;
    [ObservableProperty] private string _paperColor = "#FFFDF5";
    [ObservableProperty] private PaperStyle _paperStyle;
    [ObservableProperty] private DateTime _updatedAt = DateTime.Now;

    /// <summary>False for a "simple information" entry: it has no date (the stored one is kept for switching back).</summary>
    [ObservableProperty] private bool _hasDate = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>Rich content serialized as a WPF XamlPackage (keeps formatting, links and images).</summary>
    public byte[]? Content { get; set; }

    /// <summary>Plain text copy used for search, previews and the Claude context.</summary>
    public string PlainText { get; set; } = "";

    /// <summary>The topic's single continuous "one page" document (date dividers inside) rather than a dated entry.</summary>
    public bool IsPage { get; set; }
}

public sealed class ChatMessage
{
    public long Id { get; set; }
    public string Role { get; set; } = "user"; // user | assistant | error
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public bool IsUser => Role == "user";
    public bool IsError => Role == "error";
    public bool IsAssistant => Role == "assistant";
}

public sealed class EntryQuery
{
    public long? TopicId { get; set; }
    public DateTime? Date { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; }
    public EntryStatus? Status { get; set; }
    public int Limit { get; set; } = 5000;
    public bool IncludeFullText { get; set; }

    /// <summary>Also return topic pages (normally only dated entries are listed).</summary>
    public bool IncludePages { get; set; }

}
