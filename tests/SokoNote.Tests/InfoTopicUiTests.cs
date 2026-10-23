using System.Windows;
using System.Windows.Controls.Primitives;
using SokoNote.Models;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

[Collection("UI")]
public class EntryKindUiTests
{
    private static void Click(ButtonBase b)
    {
        if (b is ToggleButton t) t.IsChecked = !(t.IsChecked == true);
        b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        UiThread.Pump(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void The_toggle_switches_the_open_entry_between_dated_and_simple_information() => UiThread.Run(() =>
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("서버 정보", "#222222");
            r.InsertEntry(new Entry { TopicId = t.Id, Title = "DB 서버", EntryDate = DateTime.Today, PlainText = "10.0.0.5" });
            r.SetSetting("ui.viewMode", "date");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.RealTopics.Single();
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal("DB 서버", vm.CurrentEntry?.Title);
            Assert.False(w.EntryKindToggle.IsChecked);                            // 📅 dated record
            Assert.Equal(Visibility.Visible, w.EntryDatePanel.Visibility);

            Click(w.EntryKindToggle);                                             // → 📌 simple information
            Assert.False(vm.CurrentEntry!.HasDate);
            Assert.False(repo.GetEntry(vm.CurrentEntry.Id)!.HasDate);
            Assert.Equal(Visibility.Collapsed, w.EntryDatePanel.Visibility);
            Assert.True(vm.IsAllView);                                            // the view followed so it stays listed
            Assert.Contains(vm.Entries, e => e.Title == "DB 서버");

            Click(w.EntryKindToggle);                                             // and back
            Assert.True(vm.CurrentEntry!.HasDate);
            Assert.Equal(Visibility.Visible, w.EntryDatePanel.Visibility);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Ctrl_Shift_K_switches_the_kind_from_the_keyboard() => UiThread.Run(() =>
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("서버 정보", "#222222");
            r.InsertEntry(new Entry { TopicId = t.Id, Title = "DB 서버", EntryDate = DateTime.Today, PlainText = "10.0.0.5" });
            r.SetSetting("ui.viewMode", "all");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.RealTopics.Single();
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.Equal("DB 서버", vm.CurrentEntry?.Title);

            SokoNote.Views.AppCommands.ToggleEntryKind.Execute(null, w);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.False(vm.CurrentEntry!.HasDate);
            Assert.True(w.EntryKindToggle.IsChecked);
            Assert.Equal(Visibility.Visible, w.NoDateText.Visibility);
            Assert.Equal(Visibility.Collapsed, w.EntryDatePanel.Visibility);

            SokoNote.Views.AppCommands.ToggleEntryKind.Execute(null, w);
            UiThread.Pump(TimeSpan.FromMilliseconds(200));
            Assert.True(vm.CurrentEntry!.HasDate);
            Assert.Equal(Visibility.Collapsed, w.NoDateText.Visibility);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Opening_a_reference_entry_snaps_the_toggle_to_its_resting_place() => UiThread.Run(() =>
    {
        // regression: the snap used an old knob offset, leaving the knob short of the right segment
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("서버 정보", "#222222");
            r.InsertEntry(new Entry { TopicId = t.Id, Title = "DB 서버", EntryDate = DateTime.Today, PlainText = "10.0.0.5", HasDate = false });
            r.SetSetting("ui.viewMode", "all");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.RealTopics.Single();
            UiThread.Pump(TimeSpan.FromMilliseconds(600));
            Assert.False(vm.CurrentEntry!.HasDate);
            Assert.True(w.EntryKindToggle.IsChecked);

            var tpl = w.EntryKindToggle.Template;
            var knob = (System.Windows.Controls.Border)tpl.FindName("knob", w.EntryKindToggle);
            var move = (System.Windows.Media.TranslateTransform)tpl.FindName("knobMove", w.EntryKindToggle);
            var track = (System.Windows.FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(w.EntryKindToggle, 0);
            Assert.Equal(knob.Width, move.X, 0.01);                                    // sits exactly on the right segment
            Assert.Equal(track.Width, knob.Margin.Left * 2 + knob.Width * 2, 0.01);     // the two segments fill the track
            Assert.Equal("", ((System.Windows.Controls.TextBlock)tpl.FindName("infoIcon", w.EntryKindToggle)).Text); // pin is "in"
            var infoInk = (System.Windows.Media.SolidColorBrush)tpl.FindName("infoInk", w.EntryKindToggle);
            var want = (System.Windows.Media.Color)w.FindResource("KindInfoInkColor");                  // compare bytes: an animated brush ends up scRGB-based
            Assert.Equal((want.R, want.G, want.B), (infoInk.Color.R, infoInk.Color.G, infoInk.Color.B));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void New_entry_button_offers_both_kinds() => UiThread.Run(() =>
    {
        var (w, repo, errors) = UiThread.OpenMainWindow(r => { r.AddTopic("A", "#222222"); r.SetSetting("ui.viewMode", "date"); });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.RealTopics.Single();
            var menu = w.NewEntryButton.ContextMenu!;
            var info = menu.Items.OfType<System.Windows.Controls.MenuItem>().Last();   // 📅 first, 📌 second
            info.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            UiThread.Pump(TimeSpan.FromMilliseconds(200));

            Assert.False(vm.CurrentEntry!.HasDate);
            Assert.True(w.EntryKindToggle.IsChecked);                             // 📌 shown
            Assert.Equal(Visibility.Collapsed, w.EntryDatePanel.Visibility);
            Assert.True(vm.IsAllView);
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });

    [Fact]
    public void Each_topic_reopens_in_the_view_last_used_with_it() => UiThread.Run(() =>
    {
        // the 날짜별 / 전체 / 한 장 buttons adapt per topic: no menu needed
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            r.AddTopic("프로젝트 A", "#111111");
            r.AddTopic("서버 정보", "#222222");
            r.SetSetting("ui.viewMode", "date");
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            var project = vm.RealTopics.Single(t => t.Name == "프로젝트 A");
            var server = vm.RealTopics.Single(t => t.Name == "서버 정보");

            vm.SelectedTopic = server;
            vm.IsPageMode = true;                                                 // 한 장 for server info
            vm.SelectedTopic = project;
            vm.IsAllView = true;                                                  // 전체 for the project
            UiThread.Pump(TimeSpan.FromMilliseconds(100));

            vm.SelectedTopic = server;
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(vm.IsPageMode);
            vm.SelectedTopic = project;
            UiThread.Pump(TimeSpan.FromMilliseconds(100));
            Assert.True(vm.IsAllView);
            Assert.Equal("page", repo.GetSetting($"ui.topicView.{server.Id}"));
            Assert.Empty(errors);
        }
        finally { w.Close(); }
    });
}
