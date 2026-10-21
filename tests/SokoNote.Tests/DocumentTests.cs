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
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a b.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:display")]
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
    public void Word_count() => Assert.Equal(4, DocumentService.CountWords(" 오늘  회의\n정리\t끝 "));
}
