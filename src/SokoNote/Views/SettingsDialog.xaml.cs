using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SokoNote.Data;
using SokoNote.Services;

namespace SokoNote.Views;

public partial class SettingsDialog : Window
{
    private readonly WorkLogRepository _repo;
    private readonly ClaudeService _claude = new();
    private readonly string _originalLanguage;
    private bool _apiKeyTouched;

    public bool LanguageChanged { get; private set; }
    public bool EditorDefaultsChanged { get; private set; }

    public SettingsDialog(Window owner, WorkLogRepository repo)
    {
        InitializeComponent();
        Owner = owner;
        _repo = repo;

        _originalLanguage = repo.GetSetting("ui.language", "auto");
        foreach (ComboBoxItem item in LanguageBox.Items)
            if ((string)item.Tag == _originalLanguage) LanguageBox.SelectedItem = item;
        if (LanguageBox.SelectedItem is null) LanguageBox.SelectedIndex = 0;

        FontBox.ItemsSource = MainWindow.SystemFontNames();
        FontBox.Text = repo.GetSetting("editor.fontFamily", MainWindow.DefaultFontFamily);
        FontSizeBox.ItemsSource = MainWindow.FontSizes;
        FontSizeBox.Text = repo.GetSetting("editor.fontSize", MainWindow.DefaultFontSizePt.ToString(CultureInfo.InvariantCulture));

        EmailBox.Text = repo.GetSetting("claude.email", "");
        var apiMode = repo.GetSetting("claude.mode") == nameof(ClaudeAuthMode.ApiKey);
        ModeApiKey.IsChecked = apiMode;
        ModeSubscription.IsChecked = !apiMode;
        var existingKey = SecretProtector.Unprotect(repo.GetSetting("claude.apiKey"));
        if (existingKey.Length > 0) ApiKeyBox.Password = existingKey;
        ApiKeyBox.PasswordChanged += (_, _) => _apiKeyTouched = true;
        CliPathBox.Text = repo.GetSetting("claude.cliPath", "");
        ModelBox.Text = repo.GetSetting("claude.model", "");
        UpdateCliDetected();
        CliPathBox.TextChanged += (_, _) => UpdateCliDetected();

        DbPathBox.Text = repo.DbPath;
        BackupInfo.Text = Loc.F("S.Settings.BackupInfo", BackupService.KeepDays);
        AboutText.Text = Loc.F("S.Settings.AboutText", typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0");
        Mode_Changed(this, new RoutedEventArgs());
    }

    private void UpdateCliDetected()
    {
        var path = ClaudeService.DetectCliPath(CliPathBox.Text);
        CliDetected.Text = path ?? Loc.T("S.Settings.StatusCliMissing");
        CliDetected.Foreground = path is null ? (Brush)FindResource("Danger") : (Brush)FindResource("InkMuted");
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (SubscriptionPanel is null || ApiKeyPanel is null) return;
        var api = ModeApiKey.IsChecked == true;
        SubscriptionPanel.Visibility = api ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyPanel.Visibility = api ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClaudeOptions CurrentOptions() => new()
    {
        CliPath = ClaudeService.DetectCliPath(CliPathBox.Text) ?? "",
        Mode = ModeApiKey.IsChecked == true ? ClaudeAuthMode.ApiKey : ClaudeAuthMode.Subscription,
        ApiKey = ApiKeyBox.Password,
        Model = ModelBox.Text,
        WorkingDirectory = AppPaths.ClaudeWorkDir,
        Timeout = TimeSpan.FromSeconds(30),
    };

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var opt = CurrentOptions();
        if (opt.CliPath.Length == 0)
        {
            SetStatus(Loc.T("S.Settings.StatusCliMissing"), true);
            return;
        }
        try
        {
            ClaudeService.LaunchLogin(opt.CliPath, logoutFirst: false);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var opt = CurrentOptions();
        if (opt.CliPath.Length == 0)
        {
            SetStatus(Loc.T("S.Settings.StatusCliMissing"), true);
            return;
        }
        SetStatus(Loc.T("S.Settings.StatusChecking"), false);
        try
        {
            var st = await _claude.GetAuthStatusAsync(opt, CancellationToken.None);
            if (opt.Mode == ClaudeAuthMode.ApiKey)
            {
                SetStatus(opt.ApiKey.Trim().Length == 0 ? Loc.T("S.Settings.StatusNotLoggedIn") : Loc.T("S.Settings.StatusApiKeyOk"), opt.ApiKey.Trim().Length == 0);
                return;
            }
            if (st.LoggedIn)
            {
                if (!string.IsNullOrWhiteSpace(st.Email) && string.IsNullOrWhiteSpace(EmailBox.Text)) EmailBox.Text = st.Email;
                SetStatus(Loc.F("S.Settings.StatusOk", st.Email ?? st.Method ?? "OK"), false);
            }
            else
            {
                SetStatus(Loc.T("S.Settings.StatusNotLoggedIn"), true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "Danger" : "Ink");
        StatusBorder.Background = (Brush)FindResource(error ? "DangerSoft" : "SurfaceAlt");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "claude (claude.exe;claude.cmd)|claude.exe;claude.cmd|All files|*.*", Title = Loc.T("S.Settings.CliPath") };
        if (dlg.ShowDialog(this) == true) CliPathBox.Text = dlg.FileName;
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = BackupService.BackupNow(_repo);
            BackupInfo.Text = Loc.F("S.Settings.BackupDone", path);
        }
        catch (Exception ex)
        {
            BackupInfo.Text = ex.Message;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("Open folder failed", ex); }
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Error("Open url failed", ex); }
        e.Handled = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var lang = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _repo.SetSetting("ui.language", lang);
        LanguageChanged = lang != _originalLanguage;

        var font = FontBox.Text.Trim();
        if (font.Length == 0) font = MainWindow.DefaultFontFamily;
        var sizeText = FontSizeBox.Text.Trim();
        if (!double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size < 6 || size > 96)
            size = MainWindow.DefaultFontSizePt;
        var oldFont = _repo.GetSetting("editor.fontFamily", MainWindow.DefaultFontFamily);
        var oldSize = _repo.GetSetting("editor.fontSize", MainWindow.DefaultFontSizePt.ToString(CultureInfo.InvariantCulture));
        _repo.SetSetting("editor.fontFamily", font);
        _repo.SetSetting("editor.fontSize", size.ToString(CultureInfo.InvariantCulture));
        EditorDefaultsChanged = oldFont != font || oldSize != size.ToString(CultureInfo.InvariantCulture);

        _repo.SetSetting("claude.email", EmailBox.Text.Trim());
        _repo.SetSetting("claude.mode", ModeApiKey.IsChecked == true ? nameof(ClaudeAuthMode.ApiKey) : nameof(ClaudeAuthMode.Subscription));
        if (_apiKeyTouched) _repo.SetSetting("claude.apiKey", SecretProtector.Protect(ApiKeyBox.Password.Trim()));
        _repo.SetSetting("claude.cliPath", CliPathBox.Text.Trim());
        _repo.SetSetting("claude.model", ModelBox.Text.Trim());
        DialogResult = true;
    }
}
