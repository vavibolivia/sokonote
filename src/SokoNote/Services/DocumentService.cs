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
    /// Inserts a new hyperlink at <paramref name="at"/>. Links cannot nest, so when the position is inside
    /// an existing link the new one goes right after it.
    /// </summary>
    public static Hyperlink InsertHyperlink(TextPointer at, string text, Uri uri)
    {
        if (FindHyperlink(at) is { } enclosing) at = enclosing.ElementEnd;
        return new Hyperlink(new Run(text), at) { NavigateUri = uri };
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
        // Wrapping the existing text with new Hyperlink(start, end) calls Environment.FailFast (kills the app)
        // when the range ends in the middle of a run, e.g. "https://x.com." – so replace the text with a new link instead.
        var range = new TextRange(start, end);
        range.Text = "";
        return new Hyperlink(new Run(trimmed), range.Start) { NavigateUri = uri };
    }

    /// <summary>
    /// "Automatic" text color: removes explicit colors so the text follows the paper's ink again.
    /// ApplyPropertyValue cannot clear a property, so a sentinel brush first makes WPF split the runs exactly at the
    /// range edges; every element carrying the sentinel then gets its local value cleared.
    /// </summary>
    public static void ClearForeground(TextRange range)
    {
        if (range.IsEmpty) return;
        var sentinel = new SolidColorBrush(Color.FromArgb(1, 1, 2, 3));
        range.ApplyPropertyValue(TextElement.ForegroundProperty, sentinel);
        var doc = range.Start.DocumentStart.Parent as FlowDocument;
        if (doc is null) return;
        foreach (var e in Descendants(doc.Blocks))
        {
            if (!ReferenceEquals(e.ReadLocalValue(TextElement.ForegroundProperty), sentinel)) continue;
            e.ClearValue(TextElement.ForegroundProperty);
            // colors set on child elements inside the range would still win over the inherited ink
            if (e is Span s)
                foreach (var child in Descendants(s.Inlines)) child.ClearValue(TextElement.ForegroundProperty);
        }
    }

    private static IEnumerable<TextElement> Descendants(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            yield return b;
            var children = b switch
            {
                Paragraph p => Descendants(p.Inlines),
                Section s => Descendants(s.Blocks),
                List l => l.ListItems.SelectMany(li => Descendants(li.Blocks).Prepend(li)),
                Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => Descendants(c.Blocks).Prepend(c)),
                _ => Enumerable.Empty<TextElement>(),
            };
            foreach (var c in children) yield return c;
        }
    }

    private static IEnumerable<TextElement> Descendants(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i is Span s)
                foreach (var c in Descendants(s.Inlines)) yield return c;
        }
    }

    public static int CountWords(string text) =>
        text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Builds one read-only document out of many entries (used by "read all" and export).</summary>
    public const int ReadAllMaxEntries = 200;

    /// <param name="maxEntries">Content is loaded per entry, so long lists are cut off with a notice instead of freezing the UI.</param>
    public static FlowDocument BuildCombined(IEnumerable<Entry> entries, Func<long, byte[]?> contentLoader, string fontFamily,
        int maxEntries = ReadAllMaxEntries)
    {
        var all = entries as IReadOnlyCollection<Entry> ?? entries.ToList();
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily(fontFamily),
            FontSize = 14.667,
            PagePadding = new Thickness(56, 40, 56, 40),
            Background = Brushes.Transparent,
        };
        DateTime? lastDate = null;
        foreach (var e in all.Take(maxEntries))
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
        if (all.Count > maxEntries)
            doc.Blocks.Add(new Paragraph(new Run(Loc.F("S.ReadAllTruncated", maxEntries, all.Count)))
            {
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 24, 0, 0),
                TextAlignment = TextAlignment.Center,
            });
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph(new Run(Loc.T("S.EmptyList"))) { Foreground = Brushes.Gray });
        return doc;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeColor =
        new(@"^(#?([0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})|[A-Za-z]{3,30})$");

    public static SolidColorBrush BrushFromHex(string? hex, Color? fallback = null)
    {
        var b = new SolidColorBrush(TryParseColor(hex, out var c) ? c : fallback ?? Colors.White);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// Parses "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB" (the "#" is optional) or a named color like "Red".
    /// Everything else is rejected up front: WPF's ColorConverter also understands "sc#…" and
    /// "ContextColor &lt;profile uri&gt; …", which throw non-format exceptions and even load files from a URI.
    /// </summary>
    public static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        var s = (hex ?? "").Trim();
        if (!SafeColor.IsMatch(s)) return false;
        // no named color consists of hex digits only, so a bare "5B8DEF" is a hex value
        if (s[0] != '#' && s.Length is 3 or 4 or 6 or 8 && s.All(char.IsAsciiHexDigit)) s = "#" + s;
        try
        {
            if (ColorConverter.ConvertFromString(s) is Color c)
            {
                color = c;
                return true;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
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
        var s = (input ?? "").Trim().Trim('"').Trim();
        if (s.StartsWith(@"\\", StringComparison.Ordinal) || s.StartsWith("//", StringComparison.Ordinal)) return false; // network paths are never opened
        if (s.Length > 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && (s[2] == '\\' || s[2] == '/'))
        {
            // a local path such as C:\docs\보고서.docx (spaces allowed) becomes a file link
            if (!Path.IsPathFullyQualified(s) || !Uri.TryCreate(s, UriKind.Absolute, out var file) || !file.IsFile) return false;
            uri = file;
            return true;
        }
        if (s.Length == 0 || s.Contains(' ')) return false;
        if (!s.Contains("://", StringComparison.Ordinal) && !s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            if (!s.Contains('.')) return false;
            s = "https://" + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https" or "mailto" or "file")) return false;
        uri = u;
        return true;
    }

    /// <summary>
    /// Local files a click may open directly: documents, images and media only. Everything else (programs, scripts,
    /// shortcuts, HTML, macro-capable legacy Office, unknown types) is only shown in Explorer. An allowlist, because a
    /// blocklist is bypassed by trailing dots ("setup.exe."), alternate data streams and types nobody thought of.
    /// </summary>
    private static readonly HashSet<string> SafeDocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".csv", ".json", ".pdf", ".rtf",
        ".docx", ".xlsx", ".pptx", ".hwp", ".hwpx", ".odt", ".ods", ".odp",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        ".mp3", ".mp4", ".wav", ".m4a", ".mov", ".zip",
    };

    /// <summary>
    /// Decides what a click on a link may do. Rich text pasted from the web can carry links with any scheme,
    /// and the shell would happily launch protocol handlers, remote (UNC) files or executables.
    /// </summary>
    public static LinkOpenAction Classify(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return LinkOpenAction.Block;
        if (uri.Scheme is "http" or "https" or "mailto") return LinkOpenAction.Open;
        if (uri.Scheme != "file" || uri.IsUnc || uri.LocalPath.StartsWith(@"\\", StringComparison.Ordinal)) return LinkOpenAction.Block;
        // query/fragment are not part of LocalPath, so the checked path would not be what the shell receives
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return LinkOpenAction.Block;
        var path = uri.LocalPath;
        if (path.IndexOf(':', 2) >= 0) return LinkOpenAction.Reveal;                   // alternate data stream ("a.txt:x.exe")
        if (Directory.Exists(path)) return LinkOpenAction.Open;                       // a folder opens in Explorer
        var trimmed = path.TrimEnd('.', ' ');
        if (trimmed.Length != path.Length) return LinkOpenAction.Reveal;              // Windows ignores trailing dots/spaces
        return SafeDocumentExtensions.Contains(Path.GetExtension(trimmed)) ? LinkOpenAction.Open : LinkOpenAction.Reveal;
    }

    /// <summary>
    /// The exact string handed to the shell: the same local path <see cref="Classify"/> checked for files
    /// (never the re-encoded URI, which the shell may decode differently), the absolute URI for web/mail links.
    /// </summary>
    public static string LaunchTarget(Uri uri) => uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;
}

public enum LinkOpenAction
{
    /// <summary>Hand the link to the default browser/app.</summary>
    Open,
    /// <summary>Local executable/script: only show it in Explorer, never run it.</summary>
    Reveal,
    /// <summary>Unsupported scheme or network path: do nothing.</summary>
    Block,
}

/// <summary>File names and text headers for exports; a topic page is named by topic, an entry by its date and title.</summary>
public static class ExportNaming
{
    public static string FileName(Entry entry, DateTime today)
    {
        var name = entry.IsPage
            ? $"{today:yyyy-MM-dd} {Loc.F("S.PageTitle", entry.TopicName)}"
            : $"{entry.EntryDate:yyyy-MM-dd} {(string.IsNullOrWhiteSpace(entry.Title) ? Loc.T("S.Untitled") : entry.Title)}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    /// <summary>First lines of a .txt export (a page spans many days, so it has no single date or status).</summary>
    public static string TextHeader(Entry entry)
    {
        var nl = Environment.NewLine;
        return entry.IsPage
            ? Loc.F("S.PageTitle", entry.TopicName) + nl + nl
            : entry.Title + nl + $"[{entry.TopicName}] {Loc.LongDate(entry.EntryDate)} · {Loc.T("S.Status." + entry.Status)}" + nl + nl;
    }
}

/// <summary>Text search inside a rich document; matches may span formatting (bold, links…) but not paragraphs.</summary>
public static class DocumentSearch
{
    /// <summary>
    /// The first match at or after <paramref name="from"/> (wrapping to the start), ignoring case;
    /// null when the text does not occur.
    /// </summary>
    public static TextRange? Find(FlowDocument doc, string? query, TextPointer? from = null)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return null;
        TextRange? first = null;
        foreach (var hit in FindAll(doc, q))
        {
            first ??= hit;
            if (from is null || hit.Start.CompareTo(from) >= 0) return hit;
        }
        return first;
    }

    private static IEnumerable<TextRange> FindAll(FlowDocument doc, string query)
    {
        var text = new StringBuilder();
        var starts = new List<TextPointer>();
        var ends = new List<TextPointer>();
        for (var p = doc.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            var ctx = p.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = p.GetTextInRun(LogicalDirection.Forward);
                for (int i = 0; i < run.Length; i++)
                {
                    starts.Add(p.GetPositionAtOffset(i)!);
                    ends.Add(p.GetPositionAtOffset(i + 1)!);
                }
                text.Append(run);
            }
            else if (ctx is TextPointerContext.ElementStart or TextPointerContext.ElementEnd or TextPointerContext.EmbeddedElement
                     && p.GetAdjacentElement(LogicalDirection.Forward) is Block or LineBreak or InlineUIContainer or BlockUIContainer or null)
            {
                // a paragraph/line boundary or an embedded object ends the searchable stretch
                foreach (var hit in Matches(text, starts, ends, query)) yield return hit;
                text.Clear();
                starts.Clear();
                ends.Clear();
            }
        }
        foreach (var hit in Matches(text, starts, ends, query)) yield return hit;
    }

    private static IEnumerable<TextRange> Matches(StringBuilder text, List<TextPointer> starts, List<TextPointer> ends, string query)
    {
        var s = text.ToString();
        for (int i = s.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); i >= 0;
             i = s.IndexOf(query, i + query.Length, StringComparison.CurrentCultureIgnoreCase))
            yield return new TextRange(starts[i], ends[i + query.Length - 1]);
    }
}
