using System.Windows;
using SokoNote.Services;
using SokoNote.Views;

namespace SokoNote.Tests;

/// <summary>
/// Every dialog is opened for real (off-screen, never activated) so XAML wiring/resource errors surface in tests,
/// not in the user's hands — the main window once failed exactly that way.
/// </summary>
[Collection("UI")]
public class DialogSmokeTests
{
    private static void ShowAndClose(Window dialog, List<Exception> errors)
    {
        dialog.ShowActivated = false;
        dialog.ShowInTaskbar = false;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -20000;
        dialog.Top = -20000;
        dialog.Show();
        UiThread.Pump(TimeSpan.FromSeconds(3), () => dialog.IsLoaded);
        UiThread.Pump(TimeSpan.FromMilliseconds(150));
        Assert.True(dialog.IsLoaded, $"{dialog.GetType().Name} did not load");
        dialog.Close();
        Assert.Empty(errors);
    }

    [Fact]
    public void All_dialogs_open_without_errors() => UiThread.Run(() =>
    {
        var (main, repo, errors) = UiThread.OpenMainWindow(r => r.AddTopic("A", "#5B8DEF"));
        try
        {
            ShowAndClose(new TopicDialog(main, null, null), errors);
            ShowAndClose(new TopicDialog(main, "A", "#5B8DEF", _ => false), errors);
            ShowAndClose(new LinkDialog(main, "보고서", "https://example.com/", false), errors);
            ShowAndClose(new LinkDialog(main, "", null, true), errors);
            ShowAndClose(new SettingsDialog(main, repo), errors);
            // the Claude web window is only constructed: showing it would start the embedded browser and go online
            _ = new ClaudeWebWindow(main, (_, q) => (q, 0), _ => true);
            Assert.Empty(errors);
        }
        finally { main.Close(); }
    });

    [Fact]
    public void Settings_dialog_saves_what_it_shows() => UiThread.Run(() =>
    {
        var (main, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            r.SetSetting("editor.fontFamily", "Malgun Gothic");
            r.SetSetting("editor.fontSize", "12");
            r.SetSetting("claude.model", "claude-sonnet-5-5");
        });
        try
        {
            var dlg = new SettingsDialog(main, repo) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
            dlg.Show();
            UiThread.Pump(TimeSpan.FromSeconds(2), () => dlg.IsLoaded);
            // pressing Save without changes must not alter anything
            try
            {
                typeof(SettingsDialog).GetMethod("Save_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(dlg, new object?[] { dlg, new RoutedEventArgs() });
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                // DialogResult can only be set on a ShowDialog() window; everything is saved before that line
            }
            Assert.Equal("Malgun Gothic", repo.GetSetting("editor.fontFamily"));
            Assert.Equal("12", repo.GetSetting("editor.fontSize"));
            Assert.Equal("claude-sonnet-5-5", repo.GetSetting("claude.model"));
            Assert.False(dlg.EditorDefaultsChanged);
            Assert.Empty(errors);
        }
        finally { main.Close(); }
    });
}
