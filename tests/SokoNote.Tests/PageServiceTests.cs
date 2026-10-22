using System.Windows.Documents;
using SokoNote.Services;

namespace SokoNote.Tests;

public class PageServiceTests
{
    private static string Text(FlowDocument d) => new TextRange(d.ContentStart, d.ContentEnd).Text;

    [Theory]
    [InlineData(2026, 10, 22, "10월 22일 (목)")]
    [InlineData(2026, 10, 25, "10월 25일 (일)")]
    [InlineData(2027, 1, 1, "1월 1일 (금)")]
    public void Day_label_uses_the_chosen_korean_format(int y, int m, int d, string expected)
    {
        Assert.Equal(expected, PageService.DayLabel(new DateTime(y, m, d), "ko"));
    }

    [Fact]
    public void Ensure_day_adds_year_and_day_dividers_once() => Sta.Run(() =>
    {
        var doc = new FlowDocument(new Paragraph());
        var p1 = PageService.EnsureDay(doc, new DateTime(2026, 10, 21), "ko")!;
        p1.Inlines.Add(new Run("배포 일정 확정"));
        var p2 = PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko")!;
        var again = PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko")!;

        Assert.Same(p2, again);                                  // no duplicate day
        Assert.Equal(3, PageService.CountDividers(doc));          // 2026년 + 2 days
        Assert.Equal(new[] { new DateTime(2026, 10, 21), new DateTime(2026, 10, 22) }, PageService.Days(doc).Select(d => d.Day));
        Assert.Same(doc.Blocks.LastBlock, p2);
    });

    [Fact]
    public void New_year_gets_its_own_year_divider() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2026, 12, 31), "ko");
        PageService.EnsureDay(doc, new DateTime(2027, 1, 1), "ko");
        var plain = PageService.GetPlainText(doc, "ko");
        Assert.Contains("# 2026년", plain);
        Assert.Contains("# 2027년", plain);
        Assert.Equal(4, PageService.CountDividers(doc));
    });

    [Fact]
    public void Older_missing_day_is_not_inserted() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko");
        Assert.Null(PageService.EnsureDay(doc, new DateTime(2026, 10, 1), "ko"));
        Assert.Single(PageService.Days(doc));
    });

    [Fact]
    public void Cursor_target_is_the_end_of_that_days_section() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        var a = PageService.EnsureDay(doc, new DateTime(2026, 10, 21), "ko")!;
        a.Inlines.Add(new Run("첫 줄"));
        var second = new Paragraph(new Run("둘째 줄"));
        doc.Blocks.Add(second);
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko");

        Assert.Same(second, PageService.EnsureDay(doc, new DateTime(2026, 10, 21), "ko"));
    });

    [Fact]
    public void Dividers_survive_save_and_load() => Sta.Run(() =>
    {
        // the page is stored as a XamlPackage like every entry; the dates must come back as dividers
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2026, 10, 21), "ko")!.Inlines.Add(new Bold(new Run("굵은 메모")));
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko");

        var back = new FlowDocument();
        PageService.Load(back, PageService.Save(doc), "ko");

        Assert.Equal(PageService.Days(doc).Select(d => d.Day), PageService.Days(back).Select(d => d.Day));
        Assert.Equal(3, PageService.CountDividers(back));
        Assert.Contains("굵은 메모", Text(back));
        Assert.DoesNotContain("[[soko", Text(back));                      // markers never shown
        Assert.Contains(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines), i => i.FontWeight == System.Windows.FontWeights.Bold);
        Assert.Equal(3, PageService.CountDividers(doc));                  // saving did not change the live page
    });

    [Fact]
    public void Text_that_looks_like_a_marker_stays_text() => Sta.Run(() =>
    {
        // regression: typing/pasting "[[soko-day:…]]" turned into a locked, out-of-order divider after reopening
        var doc = new FlowDocument();
        var p = PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko")!;
        p.Inlines.Add(new Run("[[soko-day:2020-01-01]]"));
        doc.Blocks.Add(new Paragraph(new Run("[[soko-year:1999]]")));

        var back = new FlowDocument();
        PageService.Load(back, PageService.Save(doc), "ko");

        Assert.Equal(2, PageService.CountDividers(back));                     // 2026년 + 10월 22일 only
        var text = new TextRange(back.ContentStart, back.ContentEnd).Text;
        Assert.Contains("[[soko-day:2020-01-01]]", text);
        Assert.Contains("[[soko-year:1999]]", text);

        // and it stays text across further saves
        var again = new FlowDocument();
        PageService.Load(again, PageService.Save(back), "ko");
        Assert.Equal(2, PageService.CountDividers(again));
    });

    [Fact]
    public void Plain_documents_saved_without_markers_load_unchanged() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.Load(doc, DocumentService.FromPlainText("그냥 메모" + Environment.NewLine + "[[soko 아님]]"), "ko");
        Assert.Equal(0, PageService.CountDividers(doc));
        Assert.Equal(2, doc.Blocks.Count);
    });

    [Theory]
    [InlineData("2026-10-22", 2026, 10, 22)]
    [InlineData("2026.10.22", 2026, 10, 22)]
    [InlineData("2026/10/22", 2026, 10, 22)]
    [InlineData("10월 22일", null, 10, 22)]
    [InlineData("10월 22일 (목)", null, 10, 22)]
    [InlineData("2026년 10월 22일", 2026, 10, 22)]
    [InlineData(" 10/22 ", null, 10, 22)]
    [InlineData("10.22", null, 10, 22)]
    public void Date_queries_are_understood(string q, int? y, int m, int d)
    {
        Assert.True(PageService.TryParseDateQuery(q, out var yy, out var mm, out var dd));
        Assert.Equal((y, m, d), (yy, mm, dd));
    }

    [Theory]
    [InlineData("배포")]
    [InlineData("13월 1일")]
    [InlineData("10/32")]
    [InlineData("")]
    [InlineData("2026")]
    public void Non_date_queries_are_rejected(string q)
    {
        Assert.False(PageService.TryParseDateQuery(q, out _, out _, out _));
    }

    [Fact]
    public void Find_day_prefers_exact_year_and_otherwise_the_latest() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2025, 10, 22), "ko");
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko");
        Assert.Equal(new DateTime(2026, 10, 22), PageService.DayOf(PageService.FindDay(doc, "10월 22일")));
        Assert.Equal(new DateTime(2025, 10, 22), PageService.DayOf(PageService.FindDay(doc, "2025-10-22")));
        Assert.Null(PageService.FindDay(doc, "10월 23일"));
        Assert.Null(PageService.FindDay(doc, "회의"));
    });

    [Fact]
    public void Plain_text_marks_days_and_can_be_filtered_by_range() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2026, 10, 20), "ko")!.Inlines.Add(new Run("A일"));
        PageService.EnsureDay(doc, new DateTime(2026, 10, 21), "ko")!.Inlines.Add(new Run("B일"));
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko")!.Inlines.Add(new Run("C일"));
        var plain = PageService.GetPlainText(doc, "ko");
        Assert.Contains("## 2026-10-21 10월 21일 (수)", plain);

        var part = PageService.FilterDays(plain, new DateTime(2026, 10, 21), new DateTime(2026, 10, 22));
        Assert.DoesNotContain("A일", part);
        Assert.Contains("B일", part);
        Assert.Contains("C일", part);
        Assert.DoesNotContain("# 2026년", part);
    });

    [Fact]
    public void Export_copy_turns_dividers_into_text() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        PageService.EnsureDay(doc, new DateTime(2026, 10, 22), "ko")!.Inlines.Add(new Run("내용"));
        var copy = PageService.CloneForExport(doc, "ko");
        Assert.Equal(0, PageService.CountDividers(copy));
        var text = Text(copy);
        Assert.Contains("2026년", text);
        Assert.Contains("10월 22일 (목)", text);
        Assert.Contains("내용", text);
        Assert.Equal(2, PageService.CountDividers(doc)); // original untouched
    });
}
