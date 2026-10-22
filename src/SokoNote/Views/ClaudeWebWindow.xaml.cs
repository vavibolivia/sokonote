using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SokoNote.Services;

namespace SokoNote.Views;

/// <summary>
/// claude.ai in an embedded browser, signed in with the user's own Claude account (no API key, no CLI).
/// The app only prepares the question together with the journal content and drops it into the message box;
/// the user reviews it and presses Enter. Selected answer text can be copied back into the open entry.
/// </summary>
public partial class ClaudeWebWindow : Window
{
    private readonly Func<ClaudeScope, string, (string prompt, int count)> _buildPrompt;
    private readonly Func<string, bool> _insertIntoEntry;
    private bool _ready;

    /// <param name="buildPrompt">Builds the full prompt (journal content + question) for a scope; returns it with the entry count.</param>
    /// <param name="insertIntoEntry">Appends text to the open entry; false when no entry is open.</param>
    public ClaudeWebWindow(Window owner, Func<ClaudeScope, string, (string prompt, int count)> buildPrompt, Func<string, bool> insertIntoEntry)
    {
        InitializeComponent();
        Owner = owner;
        _buildPrompt = buildPrompt;
        _insertIntoEntry = insertIntoEntry;
        ScopeCombo.ItemsSource = Enum.GetValues<ClaudeScope>().Select(s => Loc.T("S.Claude.Scope." + s)).ToList();
        ScopeCombo.SelectedIndex = (int)ClaudeScope.Everything; // "ask about all my work" by default
        Loaded += async (_, _) => await InitBrowserAsync();
    }

    private async Task InitBrowserAsync()
    {
        try
        {
            // own profile folder so the claude.ai login is remembered between sessions
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "webview2"));
            await Browser.EnsureCoreWebView2Async(env);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.NavigationCompleted += (_, _) => LoadingText.Visibility = Visibility.Collapsed;
            Browser.Source = ClaudeWeb.Home;
            _ready = true;
            QuestionBox.Focus();
        }
        catch (Exception ex)
        {
            Logger.Error("WebView2 could not start", ex);
            LoadingText.Text = Loc.F("S.ClaudeWeb.NoRuntime", ex.Message);
            InsertToEntryButton.IsEnabled = false;
            NewChatButton.IsEnabled = false;
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var question = QuestionBox.Text.Trim();
        if (question.Length == 0)
        {
            QuestionBox.Focus();
            return;
        }
        var (prompt, count) = _buildPrompt((ClaudeScope)ScopeCombo.SelectedIndex, question);
        CopyToClipboard(prompt); // always available as a fallback (Ctrl+V)

        if (!_ready)
        {
            // no embedded browser: open claude.ai in the default browser and let the user paste
            try { Process.Start(new ProcessStartInfo(ClaudeWeb.Home.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Error("Opening claude.ai failed", ex); }
            StatusText.Text = Loc.F("S.ClaudeWeb.CopiedExternal", count);
            return;
        }
        if (!ClaudeWeb.IsClaudePage(Browser.Source))
        {
            // the user followed a link elsewhere: never type the journal into another site
            Browser.CoreWebView2.Navigate(ClaudeWeb.Home.AbsoluteUri);
            StatusText.Text = Loc.T("S.ClaudeWeb.NotClaude");
            return;
        }
        try
        {
            var result = ClaudeWeb.DecodeScriptResult(await Browser.CoreWebView2.ExecuteScriptAsync(ClaudeWeb.BuildInsertScript(prompt)));
            StatusText.Text = result == "ok" ? Loc.F("S.ClaudeWeb.Inserted", count) : Loc.T("S.ClaudeWeb.NoInput");
            if (result == "ok")
            {
                QuestionBox.Clear();
                Browser.Focus();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Inserting into claude.ai failed", ex);
            StatusText.Text = Loc.T("S.ClaudeWeb.NoInput");
        }
    }

    private async void InsertToEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        string text;
        try
        {
            text = ClaudeWeb.DecodeScriptResult(await Browser.CoreWebView2.ExecuteScriptAsync(ClaudeWeb.SelectionScript)).Trim();
        }
        catch (Exception ex)
        {
            Logger.Error("Reading the selection failed", ex);
            text = "";
        }
        if (text.Length == 0)
        {
            StatusText.Text = Loc.T("S.ClaudeWeb.NoSelection");
            return;
        }
        StatusText.Text = _insertIntoEntry(text) ? Loc.T("S.ClaudeWeb.InsertedToEntry") : Loc.T("S.ClaudeWeb.NoEntry");
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) Browser.CoreWebView2.Navigate(ClaudeWeb.Home.AbsoluteUri);
    }

    private static void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Logger.Error("Clipboard failed", ex); }
    }
}
