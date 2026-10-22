using System.Windows;
using SokoNote.Models;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

[Collection("UI")]
public class InfoTopicUiTests
{
    [Fact]
    public void Information_topic_shows_no_dates_in_the_window() => UiThread.Run(() =>
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            var info = r.AddTopic("서버 정보", "#222222", isInfo: true);
            r.InsertEntry(new Entry { TopicId = info.Id, Title = "DB 서버", EntryDate = DateTime.Today.AddDays(-40), PlainText = "10.0.0.5" });
            r.SetSetting("ui.viewMode", "date");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.Topics.Single(t => t.Name == "서버 정보");
            UiThread.Pump(TimeSpan.FromMilliseconds(300));

            Assert.Equal("DB 서버", Assert.Single(vm.Entries).Title);              // shown although it is 40 days old
            Assert.Equal("DB 서버", vm.CurrentEntry?.Title);
            Assert.Equal(Visibility.Collapsed, w.EntryDatePanel.Visibility);      // no date field
            Assert.Equal(Visibility.Collapsed, w.DateNavPanel.Visibility);        // no ◀ date ▶
            Assert.Equal(Services.Loc.F("S.InfoListTitle", "서버 정보"), vm.ListTitle);

            vm.SetTopicKind(vm.SelectedTopic!, isInfo: false);                    // switch to a dated journal
            UiThread.Pump(TimeSpan.FromMilliseconds(300));
            Assert.Equal(Visibility.Visible, w.DateNavPanel.Visibility);
            Assert.Empty(vm.Entries);                                             // date view: nothing today
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });
}
