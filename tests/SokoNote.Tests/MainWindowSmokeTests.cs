using SokoNote.Models;
using SokoNote.Services;

namespace SokoNote.Tests;

[Collection("UI")]
public class MainWindowSmokeTests
{
    /// <summary>
    /// Loads the real main window (off-screen, never activated) with data in every list, then pumps the
    /// dispatcher so templates, context menus and bindings are all instantiated.
    /// Regression: a XAML wiring error only surfaced when list items were created, in a loop of error dialogs.
    /// </summary>
    [Fact]
    public void Main_window_loads_and_renders_lists_without_errors() => UiThread.Run(() =>
    {
        var (window, _, errors) = UiThread.OpenMainWindow(repo =>
        {
            var topic = repo.AddTopic("스모크", "#5B8DEF");
            for (int i = 0; i < 5; i++)
                repo.InsertEntry(new Entry
                {
                    TopicId = topic.Id, EntryDate = DateTime.Today, Title = $"일지 {i}", Status = (EntryStatus)(i % 5),
                    Content = DocumentService.FromPlainText($"본문 {i} https://example.com"), PlainText = $"본문 {i}",
                });
            repo.AddChat("user", "질문");
            repo.AddChat("assistant", "답변");
            repo.SetSetting("ui.claudeOpen", "1"); // render the chat panel and its message template too
            repo.SetSetting("ui.viewMode", "date"); // the card list (the default view is the page)
        });
        try
        {
            UiThread.Pump(TimeSpan.FromSeconds(3), () => window.EntryList.Items.Count > 0
                && window.EntryList.ItemContainerGenerator.ContainerFromIndex(0) is not null);

            Assert.Empty(errors);
            Assert.True(window.IsLoaded, $"window never finished loading (visible={window.IsVisible}, app={System.Windows.Application.Current?.GetType().Name}, " +
                $"shutdownMode={System.Windows.Application.Current?.ShutdownMode}, dispatcherShuttingDown={window.Dispatcher.HasShutdownStarted})");
            Assert.Equal(5, window.EntryList.Items.Count);
            Assert.NotNull(window.EntryList.ItemContainerGenerator.ContainerFromIndex(0)); // item templates + context menus built
            Assert.NotNull(window.TopicList.ItemContainerGenerator.ContainerFromIndex(1));
        }
        finally
        {
            window.Close();
        }
    });
}
