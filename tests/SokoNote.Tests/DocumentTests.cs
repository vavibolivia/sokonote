using System.Windows;
using System.Windows.Documents;
using SokoNote.Services;

namespace SokoNote.Tests;

public class DocumentTests
{
    private static (FlowDocument doc, Paragraph para) Doc(params Inline[] inlines)
    {
        var para = new Paragraph();
        para.Inlines.AddRange(inlines);
        return (new FlowDocument(para), para);
    }

    private static List<Hyperlink> Links(FlowDocument doc) =>
        doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>()).ToList();

    private static string Text(TextElement e) => new TextRange(e.ContentStart, e.ContentEnd).Text;

    [Fact]
    public void Rich_content_round_trips_formatting_and_links() => Sta.Run(() =>
    {
        var link = new Hyperlink(new Run("site")) { NavigateUri = new Uri("https://example.com/") };
        var (doc, _) = Doc(new Bold(new Run("굵게")), new Run(" 보통 "), link);
        var bytes = DocumentService.Save(doc);

        var back = new FlowDocument();
        DocumentService.Load(back, bytes);
        var p = (Paragraph)back.Blocks.FirstBlock;
        Assert.Equal("굵게 보통 site", DocumentService.GetPlainText(back));
        Assert.Contains(p.Inlines, i => i.FontWeight == FontWeights.Bold);
        Assert.Equal("https://example.com/", Links(back).Single().NavigateUri.ToString());
    });

    [Fact]
    public void Corrupt_content_falls_back_to_plain_text() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        DocumentService.Load(doc, System.Text.Encoding.UTF8.GetBytes("not a package"));
        Assert.Equal("not a package", DocumentService.GetPlainText(doc));
    });

    [Fact]
    public void Plain_text_import_keeps_lines() => Sta.Run(() =>
    {
        var doc = new FlowDocument();
        DocumentService.Load(doc, DocumentService.FromPlainText("a\r\nb\nc"));
        Assert.Equal(3, doc.Blocks.Count);
    });

    [Fact]
    public void Append_concatenates_documents() => Sta.Run(() =>
    {
        var target = new FlowDocument(new Paragraph(new Run("one")));
        DocumentService.Append(target, DocumentService.FromPlainText("two"));
        var text = DocumentService.GetPlainText(target);
        Assert.Contains("one", text);
        Assert.Contains("two", text);
    });

    [Theory]
    [InlineData("https://anthropic.com", "https://anthropic.com", false)]
    [InlineData("see www.example.com/docs", "www.example.com/docs", false)]
    [InlineData("https://a.b/c/d?e=1", "https://a.b/c/d?e=1", false)]
    [InlineData("(https://x.com/y).", "https://x.com/y", true)]
    public void Auto_link_wraps_url_before_caret(string line, string expectedLinkText, bool trailing) => Sta.Run(() =>
    {
        var (doc, para) = Doc(new Run(line));
        var link = DocumentService.AutoLinkBefore(para.ContentEnd, out var hasTrailing);
        Assert.NotNull(link);
        Assert.Equal(expectedLinkText, Text(link!));
        Assert.Equal(trailing, hasTrailing);
        Assert.Equal(line, DocumentService.GetPlainText(doc));
    });

    [Fact]
    public void Auto_link_spans_runs_split_by_formatting() => Sta.Run(() =>
    {
        // regression: the URL was cut at "/docs" because only the last run was inspected
        var (doc, para) = Doc(new Bold(new Run("Bold ")), new Run("See www.example.com"), new Run("/docs"));
        var link = DocumentService.AutoLinkBefore(para.ContentEnd, out _);
        Assert.NotNull(link);
        Assert.Equal("www.example.com/docs", Text(link!));
        Assert.Equal("https://www.example.com/docs", link!.NavigateUri.ToString());
        Assert.Equal("Bold See www.example.com/docs", DocumentService.GetPlainText(doc));
    });

    [Theory]
    [InlineData("just words")]
    [InlineData("www.")]
    [InlineData("example.com")] // bare domains without www/scheme are not auto-linked
    [InlineData("")]
    public void Auto_link_ignores_non_urls(string line) => Sta.Run(() =>
    {
        var (doc, para) = Doc(new Run(line));
        Assert.Null(DocumentService.AutoLinkBefore(para.ContentEnd, out _));
        Assert.Empty(Links(doc));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Auto_link_skips_existing_links(bool caretAfterLink) => Sta.Run(() =>
    {
        // regression: a space typed right after a link wrapped its text in a second link
        var existing = new Hyperlink(new Run("https://a.com")) { NavigateUri = new Uri("https://a.com") };
        var (doc, _) = Doc(new Run("see "), existing);
        Assert.Null(DocumentService.AutoLinkBefore(caretAfterLink ? existing.ElementEnd : existing.ContentEnd, out _));
        Assert.Single(Links(doc));
    });

    [Fact]
    public void Auto_link_only_looks_at_last_word() => Sta.Run(() =>
    {
        var (doc, para) = Doc(new Run("https://first.com then text"));
        Assert.Null(DocumentService.AutoLinkBefore(para.ContentEnd, out _));
        Assert.Empty(Links(doc));
    });

    [Fact]
    public void Insert_hyperlink_inside_existing_link_goes_after_it() => Sta.Run(() =>
    {
        // regression: pasting/dropping a URL inside a link threw "Hyperlink cannot be nested"
        var existing = new Hyperlink(new Run("https://a.com")) { NavigateUri = new Uri("https://a.com") };
        var (doc, para) = Doc(new Run("x "), existing, new Run(" y"));
        var inside = existing.ContentStart.GetPositionAtOffset(3)!;

        var added = DocumentService.InsertHyperlink(inside, "new", new Uri("https://b.com"));

        Assert.Same(para, added.Parent);
        Assert.Equal(new[] { "https://a.com", "new" }, Links(doc).Select(l => Text(l)));
        Assert.Equal("x https://a.comnew y", DocumentService.GetPlainText(doc));
    });

    [Fact]
    public void Insert_hyperlink_in_plain_text() => Sta.Run(() =>
    {
        var (doc, para) = Doc(new Run("ab"));
        var mid = para.ContentStart.GetPositionAtOffset(2)!; // between a and b
        DocumentService.InsertHyperlink(mid, "L", new Uri("https://b.com"));
        Assert.Equal("aLb", DocumentService.GetPlainText(doc));
        Assert.Single(Links(doc));
    });

    [Theory]
    [InlineData("https://a.com", "https://a.com/")]
    [InlineData("www.a.com", "https://www.a.com/")]
    [InlineData("  a.co/x  ", "https://a.co/x")]
    [InlineData("mailto:me@a.com", "mailto:me@a.com")]
    public void Link_normalizer_accepts(string input, string expected)
    {
        Assert.True(LinkNormalizer.TryNormalize(input, out var uri));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData(@"C:\docs\보고서.docx", "C:\\docs\\보고서.docx")]
    [InlineData(@"D:\공유 폴더\회의록.pdf", "D:\\공유 폴더\\회의록.pdf")]
    [InlineData("\"C:\\docs\\a b.xlsx\"", "C:\\docs\\a b.xlsx")]            // "Copy as path" adds quotes
    public void Link_normalizer_turns_windows_paths_into_file_links(string input, string expectedPath)
    {
        // regression: "C:\docs\a.docx" became "https://c/docs/a.docx", a broken web link
        Assert.True(LinkNormalizer.TryNormalize(input, out var uri));
        Assert.True(uri.IsFile);
        Assert.Equal(expectedPath, uri.LocalPath);
    }

    [Theory]
    [InlineData(@"\\server\share\doc.docx")]     // network paths are never opened (credential leak), so refuse them up front
    [InlineData(@"C:relative\path.txt")]
    public void Link_normalizer_rejects_unusable_paths(string input)
    {
        Assert.False(LinkNormalizer.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a b.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:display")]
    [InlineData("ftp://example.com")]
    public void Link_normalizer_rejects(string? input)
    {
        Assert.False(LinkNormalizer.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("#FFF", true)]
    [InlineData("#5B8DEF", true)]
    [InlineData("#805B8DEF", true)]
    [InlineData("nope", false)]
    [InlineData(null, false)]
    public void Color_parsing(string? hex, bool ok)
    {
        Assert.Equal(ok, DocumentService.TryParseColor(hex, out _));
    }

    [Fact]
    public void Read_all_is_capped_and_loads_only_shown_entries() => Sta.Run(() =>
    {
        // regression: read-all loaded every entry's content (up to 5000) on each list refresh, freezing the UI
        var entries = Enumerable.Range(1, 10).Select(i => new Models.Entry
        {
            Id = i, Title = $"e{i}", TopicName = "T", EntryDate = new DateTime(2026, 10, 1).AddDays(-i),
        }).ToList();
        var loaded = new List<long>();
        var doc = DocumentService.BuildCombined(entries, id => { loaded.Add(id); return DocumentService.FromPlainText($"body{id}"); }, "Malgun Gothic", maxEntries: 3);

        Assert.Equal(new long[] { 1, 2, 3 }, loaded);
        var text = DocumentService.GetPlainText(doc);
        Assert.Contains("body3", text);
        Assert.DoesNotContain("body4", text);
        // the notice names the shown and total counts (wording depends on whether app resources are loaded)
        var notice = new TextRange(doc.Blocks.LastBlock.ContentStart, doc.Blocks.LastBlock.ContentEnd).Text;
        Assert.DoesNotContain("body", notice);
        Assert.True(notice.Contains("S.ReadAllTruncated") || (notice.Contains('3') && notice.Contains("10")), notice);
    });

    [Fact]
    public void Automatic_color_clears_the_color_instead_of_fixing_one() => Sta.Run(() =>
    {
        // regression: "automatic" baked the current ink (light gray on dark paper) into the text,
        // which became invisible after switching back to light paper
        var red = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
        var run = new Run("0123456789") { Foreground = red };
        var (doc, para) = Doc(run);
        var range = new TextRange(run.ContentStart.GetPositionAtOffset(3)!, run.ContentStart.GetPositionAtOffset(7)!);

        DocumentService.ClearForeground(range);

        var runs = para.Inlines.OfType<Run>().ToList();
        Assert.Equal("0123456789", DocumentService.GetPlainText(doc));
        Assert.Equal(new[] { "012", "3456", "789" }, runs.Select(r => r.Text));
        Assert.Same(red, runs[0].ReadLocalValue(TextElement.ForegroundProperty));
        Assert.Equal(System.Windows.DependencyProperty.UnsetValue, runs[1].ReadLocalValue(TextElement.ForegroundProperty));
        Assert.Same(red, runs[2].ReadLocalValue(TextElement.ForegroundProperty));
    });

    [Fact]
    public void Automatic_color_on_whole_paragraph() => Sta.Run(() =>
    {
        var (doc, para) = Doc(new Run("a") { Foreground = System.Windows.Media.Brushes.Blue }, new Bold(new Run("b") { Foreground = System.Windows.Media.Brushes.Green }));
        DocumentService.ClearForeground(new TextRange(para.ContentStart, para.ContentEnd));
        var colored = para.Inlines.SelectMany(Flatten).Where(i => i.ReadLocalValue(TextElement.ForegroundProperty) != System.Windows.DependencyProperty.UnsetValue);
        Assert.Empty(colored);
        Assert.Equal("ab", DocumentService.GetPlainText(doc));
    });

    private static IEnumerable<Inline> Flatten(Inline i) =>
        i is Span s ? new[] { i }.Concat(s.Inlines.SelectMany(Flatten)) : new[] { i };

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#GGG")]
    [InlineData("#1234567890")]
    [InlineData("sc#1")]
    [InlineData("ContextColor bogus")]
    [InlineData("ContextColor file://x 1,1,1,1")]
    [InlineData("red")]
    [InlineData("  #fff  ")]
    public void Color_parsing_never_throws_on_stored_values(string value) => Sta.Run(() =>
    {
        // colors come back from the database as text; a bad value must fall back, never crash the list or editor
        var brush = DocumentService.BrushFromHex(value, System.Windows.Media.Colors.Gray);
        Assert.NotNull(brush);
        DocumentService.TryParseColor(value, out _);
        foreach (var style in Enum.GetValues<Models.PaperStyle>())
            Assert.NotNull(DocumentService.CreatePaperPattern(style, brush.Color));
    });

    [Fact]
    public void Export_names_a_page_by_topic_and_export_day() => UiThread.Run(() => // real strings
    {
        // regression: a page exported as "<creation date> (제목 없음)" with a "· 메모" status header
        var page = new Models.Entry { IsPage = true, TopicName = "프로젝트/A", EntryDate = new DateTime(2026, 1, 3) };
        var today = new DateTime(2026, 10, 22);
        Assert.Equal("2026-10-22 프로젝트_A — 한 장", ExportNaming.FileName(page, today));
        var header = ExportNaming.TextHeader(page);
        Assert.StartsWith("프로젝트/A — 한 장", header);
        Assert.DoesNotContain("2026", header);                                     // no single date for a whole page
        Assert.DoesNotContain(Loc.T("S.Status.Note"), header);
    });

    [Fact]
    public void Export_names_an_entry_by_date_and_title() => UiThread.Run(() =>
    {
        var entry = new Models.Entry { TopicName = "A", Title = "회의: 결과?", EntryDate = new DateTime(2026, 10, 21), Status = Models.EntryStatus.Done };
        Assert.Equal("2026-10-21 회의_ 결과_", ExportNaming.FileName(entry, DateTime.Today));
        var header = ExportNaming.TextHeader(entry);
        Assert.Contains("회의: 결과?", header);
        Assert.Contains(Loc.T("S.Status.Done"), header);
    });

    [Fact]
    public void Search_finds_text_across_formatting_and_wraps() => Sta.Run(() =>
    {
        var p1 = new Paragraph(new Run("Alpha 배포 one"));
        var p2 = new Paragraph(new Run("둘째 "));
        p2.Inlines.Add(new Bold(new Run("배")));
        p2.Inlines.Add(new Hyperlink(new Run("포")) { NavigateUri = new Uri("https://a.com") });
        var doc = new FlowDocument(p1);
        doc.Blocks.Add(p2);

        var first = DocumentSearch.Find(doc, "배포")!;
        Assert.Equal("배포", first.Text);
        Assert.Same(p1, first.Start.Paragraph);

        var second = DocumentSearch.Find(doc, "배포", first.End)!;            // across bold + link
        Assert.Equal("배포", second.Text);
        Assert.Same(p2, second.Start.Paragraph);

        Assert.Same(p1, DocumentSearch.Find(doc, "배포", second.End)!.Start.Paragraph); // wraps around
        Assert.Equal("Alpha", DocumentSearch.Find(doc, "ALPHA")!.Text);                // ignores case
        Assert.Null(DocumentSearch.Find(doc, "one둘째"));                               // not across paragraphs
        Assert.Null(DocumentSearch.Find(doc, "없음"));
        Assert.Null(DocumentSearch.Find(doc, "  "));
    });

    [Fact]
    public void Word_count() => Assert.Equal(4, DocumentService.CountWords(" 오늘  회의\n정리\t끝 "));
}
