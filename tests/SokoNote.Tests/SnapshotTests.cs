using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SokoNote.Models;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

/// <summary>
/// Renders the real main window (off screen, never activated) to PNG files for design review.
/// Runs only when SOKO_SNAPSHOT_DIR is set, so the normal suite stays image-free.
/// </summary>
[Collection("UI")]
public class SnapshotTests
{
    private static readonly string? Dir = Environment.GetEnvironmentVariable("SOKO_SNAPSHOT_DIR");

    private static void Save(Visual v, Size size, string name, Rect? crop = null, double scale = 1.0)
    {
        var bmp = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(v);
        BitmapSource src = bmp;
        if (crop is { } c)
            src = new CroppedBitmap(bmp, new Int32Rect((int)(c.X * scale), (int)(c.Y * scale), (int)(c.Width * scale), (int)(c.Height * scale)));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var f = File.Create(Path.Combine(Dir!, name));
        enc.Save(f);
    }

    [Fact]
    public void Render_editor_header_and_new_entry_menu()
    {
        if (Dir is null) return;
        Directory.CreateDirectory(Dir);
        UiThread.Run(() =>
        {
            var (w, repo, errors) = UiThread.OpenMainWindow(r =>
            {
                var a = r.AddTopic("프로젝트 A", "#5B8DEF");
                var s = r.AddTopic("서버 정보", "#E8A33D");
                r.AddTopic("회의록", "#4CAF7D");
                r.InsertEntry(new Entry { TopicId = a.Id, Title = "배포 일정 점검", EntryDate = DateTime.Today, PlainText = "오늘 배포 전 체크리스트 확인" });
                r.InsertEntry(new Entry { TopicId = a.Id, Title = "성능 이슈 분석", EntryDate = DateTime.Today.AddDays(-1), PlainText = "응답 지연 원인 분석 중" });
                r.InsertEntry(new Entry { TopicId = s.Id, Title = "DB 서버 접속 정보", EntryDate = DateTime.Today, PlainText = "10.0.0.5 / 포트 5432", HasDate = false });
                r.SetSetting("ui.viewMode", "all");
            });
            try
            {
                w.Width = 1280; w.Height = 820;
                UiThread.Pump(TimeSpan.FromMilliseconds(400));
                var vm = (MainViewModel)w.DataContext;
                vm.SelectedTopic = vm.RealTopics.First(t => t.Name == "프로젝트 A");
                UiThread.Pump(TimeSpan.FromMilliseconds(400));
                var size = new Size(w.ActualWidth, w.ActualHeight);
                var content = (Visual)w.Content;
                Save(content, size, "01-main-dated.png");

                // zoom on the editor header (toggle in 📅 state)
                var header = w.EntryMetaPanel.TransformToAncestor(content).TransformBounds(new Rect(0, 0, w.EntryMetaPanel.ActualWidth, w.EntryMetaPanel.ActualHeight));
                var zoom = Rect.Inflate(header, 24, 60);
                zoom.Intersect(new Rect(size));
                Save(content, size, "02-header-dated-2x.png", zoom, 2.0);

                // toggle → 📌 state (settle the animation first)
                w.EntryKindToggle.IsChecked = true;
                w.EntryKindToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                UiThread.Pump(TimeSpan.FromMilliseconds(900));
                Save(content, size, "03-main-info.png");
                header = w.EntryMetaPanel.TransformToAncestor(content).TransformBounds(new Rect(0, 0, w.EntryMetaPanel.ActualWidth, w.EntryMetaPanel.ActualHeight));
                zoom = Rect.Inflate(header, 24, 60);
                zoom.Intersect(new Rect(size));
                Save(content, size, "04-header-info-2x.png", zoom, 2.0);

                // the 새 일지 menu
                var menu = w.NewEntryButton.ContextMenu!;
                menu.PlacementTarget = w.NewEntryButton;
                menu.IsOpen = true;
                UiThread.Pump(TimeSpan.FromMilliseconds(500));
                Save(menu, new Size(menu.ActualWidth, menu.ActualHeight), "05-new-entry-menu-2x.png", null, 2.0);
                menu.IsOpen = false;
                Assert.Empty(errors);
            }
            finally { w.Close(); }
        });
    }
}
