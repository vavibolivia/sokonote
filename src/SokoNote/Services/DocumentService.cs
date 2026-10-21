using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using SokoNote.Models;

namespace SokoNote.Services;

/// <summary>Serialization and helpers for the rich-text journal content.</summary>
public static class DocumentService
{
    public static byte[] Save(FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.XamlPackage);
        return ms.ToArray();
    }

    /// <summary>Replaces the document content. Unknown/corrupt data is shown as plain text instead of being lost.</summary>
    public static void Load(FlowDocument doc, byte[]? data)
    {
        doc.Blocks.Clear();
        if (data is null || data.Length == 0) return;
        try
        {
            var range = new TextRange(doc.ContentStart, doc.ContentEnd);
            using var ms = new MemoryStream(data);
            range.Load(ms, DataFormats.XamlPackage);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load rich content, falling back to plain text", ex);
            doc.Blocks.Clear();
            doc.Blocks.Add(new Paragraph(new Run(Encoding.UTF8.GetString(data))));
        }
    }

    /// <summary>Appends serialized content at the end of <paramref name="target"/>.</summary>
    public static void Append(FlowDocument target, byte[]? data)
    {
        if (data is null || data.Length == 0) return;
        try
        {
            var range = new TextRange(target.ContentEnd, target.ContentEnd);
            using var ms = new MemoryStream(data);
            range.Load(ms, DataFormats.XamlPackage);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to append rich content", ex);
        }
    }

    public static string GetPlainText(FlowDocument doc) =>
        new TextRange(doc.ContentStart, doc.ContentEnd).Text.TrimEnd('\r', '\n', ' ');

    public static byte[] FromPlainText(string text)
    {
        var doc = new FlowDocument();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            doc.Blocks.Add(new Paragraph(new Run(line)));
        return Save(doc);
    }

    private static readonly System.Text.RegularExpressions.Regex UrlToken =
        new(@"^(https?://|www\.)\S+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static Hyperlink? FindHyperlink(TextPointer? pos)
    {
        DependencyObject? d = pos?.Parent;
        while (d is not null)
        {
            if (d is Hyperlink h) return h;
            if (d is FlowDocument) return null;
            d = d is TextElement te ? te.Parent : null;
        }
        return null;
    }

    /// <summary>
    /// Turns the URL-looking word right before <paramref name="caret"/> into a hyperlink (Word-style auto link).
    /// Trailing punctuation stays outside the link; <paramref name="hasTrailing"/> tells whether there was any.
    /// </summary>
    public static Hyperlink? AutoLinkBefore(TextPointer caret, out bool hasTrailing)
    {
        hasTrailing = false;
        caret = caret.GetInsertionPosition(LogicalDirection.Backward);
        if (FindHyperlink(caret) is not null || caret.Paragraph is null) return null;
        // Text of the whole line so far: runs may be split by formatting, GetTextInRun would only see the last one.
        var before = new TextRange(caret.Paragraph.ContentStart, caret).Text;
        if (before.Length == 0) return null;
        var lastSpace = before.LastIndexOfAny(new[] { ' ', '\t', ' ' });
        var token = (lastSpace >= 0 ? before[(lastSpace + 1)..] : before).TrimStart('(', '[', '<', '"', '\'');
        if (token.Length < 6 || !UrlToken.IsMatch(token)) return null;
        var trimmed = token.TrimEnd('.', ',', ';', ')', ']', '>', '"', '\'');
        if (!LinkNormalizer.TryNormalize(trimmed, out var uri)) return null;
        if (before[..^token.Length].Contains('\n')) return null;

        // walk back one insertion position per character (symbol offsets would also count run boundaries)
        var end = caret;
        for (int i = 0; i < token.Length - trimmed.Length; i++)
            end = end.GetNextInsertionPosition(LogicalDirection.Backward) ?? end;
        var start = end;
        for (int i = 0; i < trimmed.Length; i++)
            start = start.GetNextInsertionPosition(LogicalDirection.Backward) ?? start;
        if (new TextRange(start, end).Text != trimmed) return null;
        // the caret may sit just after an existing link: its text must not be wrapped a second time
        if (FindHyperlink(start.GetNextInsertionPosition(LogicalDirection.Forward)) is not null ||
            FindHyperlink(end.GetNextInsertionPosition(LogicalDirection.Backward)) is not null) return null;
        hasTrailing = trimmed.Length != token.Length;
        return new Hyperlink(start, end) { NavigateUri = uri };
    }

    public static int CountWords(string text) =>
        text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Builds one read-only document out of many entries (used by "read all" and export).</summary>
    public static FlowDocument BuildCombined(IEnumerable<Entry> entries, Func<long, byte[]?> contentLoader, string fontFamily)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily(fontFamily),
            FontSize = 14.667,
            PagePadding = new Thickness(56, 40, 56, 40),
            Background = Brushes.Transparent,
        };
        DateTime? lastDate = null;
        foreach (var e in entries)
        {
            if (lastDate != e.EntryDate.Date)
            {
                lastDate = e.EntryDate.Date;
                doc.Blocks.Add(new Paragraph(new Run(Loc.LongDate(e.EntryDate)))
                {
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x30)),
                    Margin = new Thickness(0, 24, 0, 6),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE4)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 4),
                });
            }
            var head = new Paragraph { Margin = new Thickness(0, 14, 0, 4) };
            head.Inlines.Add(new Run("■ ") { Foreground = BrushFromHex(e.TopicColor) });
            head.Inlines.Add(new Run($"[{e.TopicName}] ") { Foreground = BrushFromHex(e.TopicColor), FontWeight = FontWeights.SemiBold });
            head.Inlines.Add(new Run(string.IsNullOrWhiteSpace(e.Title) ? Loc.T("S.Untitled") : e.Title) { FontWeight = FontWeights.Bold, FontSize = 16 });
            head.Inlines.Add(new Run("   " + Loc.T("S.Status." + e.Status)) { Foreground = Brushes.Gray, FontSize = 12 });
            doc.Blocks.Add(head);
            Append(doc, contentLoader(e.Id));
        }
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph(new Run(Loc.T("S.EmptyList"))) { Foreground = Brushes.Gray });
        return doc;
    }

    public static SolidColorBrush BrushFromHex(string? hex, Color? fallback = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
        }
        catch (FormatException)
        {
        }
        return new SolidColorBrush(fallback ?? Colors.White);
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        hex = hex.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch (FormatException)
        {
        }
        return false;
    }

    public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Background pattern painted on the document itself so it scrolls with the text.</summary>
    public static Brush CreatePaperPattern(PaperStyle style, Color paper)
    {
        if (style == PaperStyle.Plain) return Brushes.Transparent;
        var lum = (0.299 * paper.R + 0.587 * paper.G + 0.114 * paper.B) / 255.0;
        var ink = lum > 0.5 ? Color.FromArgb(0x30, 0x3A, 0x6F, 0xB0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
        var pen = new SolidColorBrush(ink);
        var group = new DrawingGroup();
        double w, h;
        switch (style)
        {
            case PaperStyle.Lined:
                w = 8; h = 28;
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
                group.Children.Add(new GeometryDrawing(pen, null, new RectangleGeometry(new Rect(0, h - 1, w, 1))));
                break;
            case PaperStyle.Grid:
                w = 22; h = 22;
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
                group.Children.Add(new GeometryDrawing(pen, null, new RectangleGeometry(new Rect(0, h - 1, w, 1))));
                group.Children.Add(new GeometryDrawing(pen, null, new RectangleGeometry(new Rect(w - 1, 0, 1, h))));
                break;
            default:
                w = 22; h = 22;
                group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
                group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, ink.A * 2), ink.R, ink.G, ink.B)), null,
                    new EllipseGeometry(new Point(w / 2, h / 2), 1.2, 1.2)));
                break;
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}

public static class LinkNormalizer
{
    /// <summary>Accepts "https://…", "mailto:…" or a bare host like "www.example.com" (https is assumed).</summary>
    public static bool TryNormalize(string? input, out Uri uri)
    {
        uri = null!;
        var s = (input ?? "").Trim();
        if (s.Length == 0 || s.Contains(' ')) return false;
        if (!s.Contains("://", StringComparison.Ordinal) && !s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.Contains('.')) return false;
            s = "https://" + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https" or "mailto" or "ftp" or "file")) return false;
        uri = u;
        return true;
    }
}
