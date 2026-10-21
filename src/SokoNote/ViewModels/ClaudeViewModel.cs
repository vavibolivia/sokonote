using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SokoNote.Models;
using SokoNote.Services;

namespace SokoNote.ViewModels;

public partial class ClaudeViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ClaudeService _service = new();
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _watch = new();
    private CancellationTokenSource? _cts;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private int _scopeIndex = (int)ClaudeScope.Last7Days;
    [ObservableProperty] private string _accountLabel = "";
    [ObservableProperty] private bool _isConfigured;

    /// <summary>Set by the window: flushes unsaved editor changes before the journal is read.</summary>
    public Action? FlushEditor { get; set; }

    /// <summary>Raised after a new message is added so the view can scroll down.</summary>
    public event EventHandler? MessageAdded;

    public ClaudeViewModel(MainViewModel main)
    {
        _main = main;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => BusyText = Loc.F("S.Claude.Thinking", (int)_watch.Elapsed.TotalSeconds);
    }

    public void Load()
    {
        Messages.Clear();
        foreach (var m in _main.Repo.GetChats()) Messages.Add(m);
        RefreshAccount();
    }

    public ClaudeOptions BuildOptions()
    {
        var repo = _main.Repo;
        return new ClaudeOptions
        {
            CliPath = ClaudeService.DetectCliPath(repo.GetSetting("claude.cliPath")) ?? "",
            Mode = repo.GetSetting("claude.mode") == nameof(ClaudeAuthMode.ApiKey) ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Subscription,
            ApiKey = SecretProtector.Unprotect(repo.GetSetting("claude.apiKey")),
            Model = repo.GetSetting("claude.model", ""),
            WorkingDirectory = AppPaths.ClaudeWorkDir,
        };
    }

    public void RefreshAccount()
    {
        var repo = _main.Repo;
        var email = repo.GetSetting("claude.email", "");
        var mode = repo.GetSetting("claude.mode") == nameof(ClaudeAuthMode.ApiKey) ? Loc.T("S.Claude.ModeApiKey") : Loc.T("S.Claude.ModeSubscription");
        IsConfigured = !string.IsNullOrWhiteSpace(email) || repo.GetSetting("claude.mode") == nameof(ClaudeAuthMode.ApiKey);
        AccountLabel = IsConfigured
            ? $"{(string.IsNullOrWhiteSpace(email) ? Loc.T("S.Claude.NoEmail") : email)} · {mode}"
            : Loc.T("S.Claude.NotConfigured");
    }

    [RelayCommand]
    private Task Preset(string? key)
    {
        switch (key)
        {
            case "today":
                ScopeIndex = (int)ClaudeScope.SelectedDate;
                return SendText(Loc.T("S.Claude.Q.Today"));
            case "week":
                ScopeIndex = (int)ClaudeScope.Last7Days;
                return SendText(Loc.T("S.Claude.Q.Week"));
            case "open":
                ScopeIndex = (int)ClaudeScope.Last30Days;
                return SendText(Loc.T("S.Claude.Q.Open"));
            case "next":
                ScopeIndex = (int)ClaudeScope.Last7Days;
                return SendText(Loc.T("S.Claude.Q.Next"));
            case "entry":
                ScopeIndex = (int)ClaudeScope.CurrentEntry;
                return SendText(Loc.T("S.Claude.Q.Entry"));
            default:
                return Task.CompletedTask;
        }
    }

    [RelayCommand]
    private Task Send()
    {
        var text = Input.Trim();
        if (text.Length == 0) return Task.CompletedTask;
        Input = "";
        return SendText(text);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) return;
        _main.Repo.ClearChats();
        Messages.Clear();
    }

    private async Task SendText(string text)
    {
        if (IsBusy) return;
        FlushEditor?.Invoke();

        var scope = (ClaudeScope)ScopeIndex;
        var entries = CollectEntries(scope);
        var history = Messages.ToList();
        var prompt = ClaudePromptBuilder.Build(text, entries, Loc.T("S.Claude.Scope." + scope), history, DateTime.Today, Loc.Language);

        AddMessage("user", text);
        var options = BuildOptions();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _watch.Restart();
        BusyText = Loc.F("S.Claude.Thinking", 0);
        _elapsedTimer.Start();
        try
        {
            if (string.IsNullOrEmpty(options.CliPath)) throw new ClaudeException(Loc.T("S.Claude.CliMissing"));
            var answer = await _service.AskAsync(prompt, options, _cts.Token);
            AddMessage("assistant", string.IsNullOrWhiteSpace(answer) ? Loc.T("S.Claude.EmptyAnswer") : answer);
        }
        catch (OperationCanceledException)
        {
            AddMessage("error", Loc.T("S.Claude.Cancelled"));
        }
        catch (ClaudeException ex)
        {
            AddMessage("error", Loc.F("S.Claude.Error", ex.Message));
        }
        catch (Exception ex)
        {
            Logger.Error("Claude request failed", ex);
            AddMessage("error", Loc.F("S.Claude.Error", ex.Message));
        }
        finally
        {
            _elapsedTimer.Stop();
            _watch.Stop();
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void AddMessage(string role, string content)
    {
        ChatMessage msg;
        try
        {
            msg = _main.Repo.AddChat(role, content);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to persist chat message", ex);
            msg = new ChatMessage { Role = role, Content = content };
        }
        Messages.Add(msg);
        MessageAdded?.Invoke(this, EventArgs.Empty);
    }

    public List<Entry> CollectEntries(ClaudeScope scope)
    {
        var repo = _main.Repo;
        var q = new EntryQuery { IncludeFullText = true, Limit = 2000 };
        switch (scope)
        {
            case ClaudeScope.CurrentEntry:
                if (_main.CurrentEntry is { } cur && repo.GetEntry(cur.Id) is { } full) return new List<Entry> { full };
                return new List<Entry>();
            case ClaudeScope.SelectedDate:
                q.Date = _main.IsDateMode ? _main.SelectedDate.Date : DateTime.Today;
                q.TopicId = _main.SelectedTopicId;
                break;
            case ClaudeScope.Last7Days:
                q.From = DateTime.Today.AddDays(-6);
                q.To = DateTime.Today;
                break;
            case ClaudeScope.Last30Days:
                q.From = DateTime.Today.AddDays(-29);
                q.To = DateTime.Today;
                break;
            case ClaudeScope.CurrentTopic:
                q.TopicId = _main.SelectedTopicId;
                break;
        }
        return repo.QueryEntries(q);
    }
}
