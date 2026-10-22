using System.Windows;
using SokoNote.Models;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

[Collection("UI")]
public class InfoTopicUiTests
{
    [Fact]
    public void Topic_right_click_menu_switches_the_type() => UiThread.Run(() =>
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(r => r.AddTopic("서버 정보", "#222222"));
        try
        {
            var vm = (MainViewModel)w.DataContext;
            var topic = vm.RealTopics.Single();
            var menu = (System.Windows.Controls.ContextMenu)w.FindResource("TopicMenu");
            menu.DataContext = topic;                                             // what the right-clicked row provides
            UiThread.Pump(TimeSpan.FromMilliseconds(100));                        // bindings update at DataBind priority
            var kind = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => Equals(i.Header, Services.Loc.T("S.TopicKind")));
            var journal = (System.Windows.Controls.MenuItem)kind.Items[0];
            var info = (System.Windows.Controls.MenuItem)kind.Items[1];
            Assert.True(journal.IsChecked);                                       // the current type is ticked
            Assert.False(info.IsChecked);

            info.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(topic.IsInfo);
            Assert.True(repo.GetTopics().Single().IsInfo);                       // saved
            Assert.True(info.IsChecked);

            journal.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert.False(repo.GetTopics().Single().IsInfo);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Switching_the_type_updates_the_open_entry() => UiThread.Run(() =>
    {
        // regression: the open entry stayed open across the switch, so its date field did not follow the new type
        var (w, _, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("업무", "#222222");
            r.InsertEntry(new Entry { TopicId = t.Id, Title = "회의", EntryDate = DateTime.Today, PlainText = "x" });
            r.SetSetting("ui.viewMode", "all");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.Topics.Single(t => t.Name == "업무");
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal("회의", vm.CurrentEntry?.Title);
            Assert.Equal(Visibility.Visible, w.EntryDatePanel.Visibility);

            vm.SetTopicKind(vm.SelectedTopic!, isInfo: true);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal("회의", vm.CurrentEntry?.Title);                        // still the same entry…
            Assert.False(vm.CurrentEntry!.HasDate);
            Assert.False(vm.Entries.Single().HasDate);
            Assert.Equal(Visibility.Collapsed, w.EntryDatePanel.Visibility);      // …now without a date field

            vm.SetTopicKind(vm.SelectedTopic!, isInfo: false);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal(Visibility.Visible, w.EntryDatePanel.Visibility);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

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
