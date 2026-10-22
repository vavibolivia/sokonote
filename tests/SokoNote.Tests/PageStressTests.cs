using System.Diagnostics;
using System.Windows.Documents;
using SokoNote.Data;
using SokoNote.Models;
using SokoNote.Services;
using SokoNote.ViewModels;

namespace SokoNote.Tests;

/// <summary>Large pages: years of daily notes on one page must stay correct and responsive.</summary>
public class PageStressTests
{
    private static FlowDocument BuildPage(DateTime first, int days, int linesPerDay)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < days; i++)
        {
            var day = first.AddDays(i);
            var p = PageService.EnsureDay(doc, day, "ko")!;
            p.Inlines.Add(new Run($"{day:yyyy-MM-dd} 업무 0"));
            for (int l = 1; l < linesPerDay; l++) doc.Blocks.Add(new Paragraph(new Run($"{day:yyyy-MM-dd} 업무 {l} https://example.com/{i}/{l}")));
        }
        return doc;
    }

    [Fact]
    public void Three_years_of_days_round_trip_and_search_quickly() => Sta.Run(() =>
    {
        var first = new DateTime(2024, 1, 1);
        const int days = 1096;
        var sw = Stopwatch.StartNew();
        var doc = BuildPage(first, days, 3);
        var build = sw.Elapsed;

        sw.Restart();
        var bytes = PageService.Save(doc);
        var back = new FlowDocument();
        PageService.Load(back, bytes, "ko");
        var roundTrip = sw.Elapsed;

        Assert.Equal(days, PageService.Days(back).Count);
        Assert.Equal(days + 3, PageService.CountDividers(back));                 // + 2024/2025/2026 year dividers
        Assert.Equal(PageService.Days(doc).Select(d => d.Day), PageService.Days(back).Select(d => d.Day));

        sw.Restart();
        var rnd = new Random(7);
        for (int i = 0; i < 300; i++)
        {
            var d = first.AddDays(rnd.Next(days));
            Assert.Equal(d, PageService.DayOf(PageService.FindDay(back, d.ToString("yyyy-MM-dd"))));
            var para = PageService.LastParagraphOfSection(back, PageService.FindDay(back, $"{d.Year}년 {d.Month}월 {d.Day}일")!);
            Assert.StartsWith($"{d:yyyy-MM-dd} 업무 2", new TextRange(para.ContentStart, para.ContentEnd).Text);
        }
        var search = sw.Elapsed;

        sw.Restart();
        var plain = PageService.GetPlainText(back, "ko");
        var week = PageService.FilterDays(plain, new DateTime(2025, 6, 1), new DateTime(2025, 6, 7));
        var text = sw.Elapsed;
        Assert.Equal(7, week.Split('\n').Count(l => l.StartsWith("## ")));
        Assert.Equal(7 * 3, week.Split('\n').Count(l => l.Contains(" 업무 ")));

        // generous bounds (CI runners are ~2-3x slower than a desktop): catch blow-ups, not machine speed
        Assert.True(build < TimeSpan.FromSeconds(30), $"build {build}");
        Assert.True(roundTrip < TimeSpan.FromSeconds(30), $"save/load {roundTrip}");
        Assert.True(search < TimeSpan.FromSeconds(40), $"300 searches {search}");
        Assert.True(text < TimeSpan.FromSeconds(20), $"plain text/filter {text}");
    });

    [Fact]
    public void Repeated_save_load_cycles_do_not_drift() => Sta.Run(() =>
    {
        var doc = BuildPage(new DateTime(2026, 9, 1), 40, 2);
        var expected = PageService.GetPlainText(doc, "ko");
        var bytes = PageService.Save(doc);
        for (int i = 0; i < 25; i++)
        {
            var next = new FlowDocument();
            PageService.Load(next, bytes, "ko");
            bytes = PageService.Save(next);
            Assert.Equal(expected, PageService.GetPlainText(next, "ko"));
        }
    });

    [Fact]
    public void Large_page_is_stored_and_found_by_date_in_the_database() => Sta.Run(() =>
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sokonote-tests", Guid.NewGuid().ToString("N"));
        var repo = new WorkLogRepository(System.IO.Path.Combine(dir, "big.db"));
        var topic = repo.AddTopic("대형", "#111111");
        var doc = BuildPage(new DateTime(2025, 1, 1), 365, 4);
        var page = repo.GetOrCreatePage(topic.Id);
        page.Content = PageService.Save(doc);
        page.PlainText = PageService.GetPlainText(doc, "ko");
        repo.UpdateEntry(page);

        var hit = repo.QueryEntries(new EntryQuery { IncludePages = true, Search = "2025-07-15" }).Single();
        Assert.True(hit.IsPage);
        var back = new FlowDocument();
        PageService.Load(back, repo.GetEntry(hit.Id)!.Content, "ko");
        Assert.Equal(365, PageService.Days(back).Count);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    });
}

[Collection("UI")]
public class PageUiStressTests
{
    [Fact]
    public void A_year_long_page_in_the_window_jumps_and_saves_reliably() => UiThread.Run(() =>
    {
        var first = DateTime.Today.AddDays(-364);
        var (w, repo, errors) = UiThread.OpenMainWindow(r =>
        {
            var t = r.AddTopic("일년", "#5B8DEF");
            var doc = new FlowDocument();
            for (int i = 0; i < 364; i++)
                PageService.EnsureDay(doc, first.AddDays(i), "ko")!.Inlines.Add(new Run($"메모 {first.AddDays(i):MMdd}"));
            var page = r.GetOrCreatePage(t.Id);
            page.Content = PageService.Save(doc);
            r.UpdateEntry(page);
        });
        try
        {
            var vm = (MainViewModel)w.DataContext;
            vm.SelectedTopic = vm.Topics.Single(t => t.Name == "일년");
            var sw = Stopwatch.StartNew();
            vm.IsPageMode = true;
            UiThread.Pump(TimeSpan.FromMilliseconds(300));
            var open = sw.Elapsed;
            Assert.Equal(365, vm.PageDays.Count);                                  // + today's new divider

            sw.Restart();
            var rnd = new Random(3);
            for (int i = 0; i < 150; i++)
            {
                var d = first.AddDays(rnd.Next(364));
                vm.SelectedDate = d;
                Block? b = w.Editor.CaretPosition.Paragraph;
                while (b is not null && PageService.DayOf(b) is null) b = b.PreviousBlock;
                Assert.Equal(d, PageService.DayOf(b));
            }
            var jumps = sw.Elapsed;

            sw.Restart();
            vm.GoToday();
            for (int i = 0; i < 200; i++)
            {
                w.Editor.CaretPosition.InsertTextInRun($"줄{i} ");
                if (i % 20 == 0) w.SaveNow();
            }
            w.SaveNow();
            var typing = sw.Elapsed;

            var stored = repo.GetEntry(vm.CurrentEntry!.Id)!.PlainText;
            Assert.Contains("줄199", stored);
            Assert.Equal(365, stored.Split('\n').Count(l => l.StartsWith("## ")));
            Assert.Empty(errors);
            Assert.True(open < TimeSpan.FromSeconds(20), $"open {open}");
            Assert.True(jumps < TimeSpan.FromSeconds(30), $"150 jumps {jumps}");
            Assert.True(typing < TimeSpan.FromSeconds(60), $"typing+saves {typing}");
        }
        finally { w.Close(); }
    });
}
