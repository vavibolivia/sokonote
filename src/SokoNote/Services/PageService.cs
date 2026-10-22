using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SokoNote.Services;

/// <summary>
/// "One page" per topic: a single continuous document split into days by date dividers.
/// A divider is a <see cref="BlockUIContainer"/> (not text), so it cannot be typed into; the editor additionally
/// refuses edits that would remove one. The date lives in the divider's Tag ("2026-10-22" / "year:2026"),
/// which survives the XamlPackage round trip because it is a plain string on a built-in control.
/// </summary>
public static class PageService
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string YearPrefix = "year:";
    private static readonly string[] KoDays = { "일", "월", "화", "수", "목", "금", "토" };

    /// <summary>Plain-text form of a date divider: searchable by ISO date and readable for Claude.</summary>
    private static readonly Regex PlainDateLine = new(@"^## (\d{4}-\d{2}-\d{2})\b", RegexOptions.Multiline);

    // ------------------------------------------------------------------ labels

    public static string DayLabel(DateTime d, string language) =>
        language == "ko"
            ? $"{d.Month}월 {d.Day}일 ({KoDays[(int)d.DayOfWeek]})"
            : d.ToString("MMM d (ddd)", CultureInfo.GetCultureInfo("en-US"));

    public static string YearLabel(int year, string language) => language == "ko" ? $"{year}년" : year.ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- dividers

    public static BlockUIContainer CreateDayDivider(DateTime day, string language)
    {
        var text = new TextBlock
        {
            Text = DayLabel(day, language),
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x5F, 0xC9)),
        };
        var border = new Border
        {
            Tag = day.ToString(DateFormat, CultureInfo.InvariantCulture),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xB8, 0xE8)),
            BorderThickness = new Thickness(0, 0, 0, 1.5),
            Padding = new Thickness(0, 2, 0, 3),
            Child = text,
            Focusable = false,
        };
        return new BlockUIContainer(border) { Margin = new Thickness(0, 18, 0, 6) };
    }

    public static BlockUIContainer CreateYearDivider(int year, string language)
    {
        var border = new Border
        {
            Tag = YearPrefix + year.ToString(CultureInfo.InvariantCulture),
            Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFA)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = YearLabel(year, language),
                FontWeight = FontWeights.Bold,
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x30)),
            },
            Focusable = false,
        };
        return new BlockUIContainer(border) { Margin = new Thickness(0, 26, 0, 4) };
    }

    public static bool IsDivider(Block? block) =>
        block is BlockUIContainer { Child: Border { Tag: string tag } } &&
        (tag.StartsWith(YearPrefix, StringComparison.Ordinal) || TryParseIso(tag, out _));

    public static DateTime? DayOf(Block? block) =>
        block is BlockUIContainer { Child: Border { Tag: string tag } } && TryParseIso(tag, out var d) ? d : null;

    public static int? YearOf(Block? block) =>
        block is BlockUIContainer { Child: Border { Tag: string tag } } && tag.StartsWith(YearPrefix, StringComparison.Ordinal)
        && int.TryParse(tag[YearPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    public static int CountDividers(FlowDocument doc) => doc.Blocks.Count(IsDivider);

    /// <summary>All day dividers in document order.</summary>
    public static List<(DateTime Day, BlockUIContainer Divider)> Days(FlowDocument doc) =>
        doc.Blocks.OfType<BlockUIContainer>().Select(b => (Day: DayOf(b), Divider: b))
            .Where(x => x.Day is not null).Select(x => (x.Day!.Value, x.Divider)).ToList();

    /// <summary>
    /// Makes sure <paramref name="day"/> has a divider (adding a year divider when the year changes) and returns the
    /// paragraph where typing for that day continues: the last paragraph of its section.
    /// Normally the day is appended; if later days already exist (e.g. the PC clock was set back) it is inserted in
    /// date order instead.
    /// </summary>
    public static Paragraph EnsureDay(FlowDocument doc, DateTime day, string language)
    {
        day = day.Date;
        var days = Days(doc);
        var existing = days.FirstOrDefault(d => d.Day == day).Divider;
        if (existing is not null) return LastParagraphOfSection(doc, existing);
        var nextIndex = days.FindIndex(d => d.Day > day);
        if (nextIndex >= 0) return InsertDayBefore(doc, days, nextIndex, day, language);

        // drop a trailing empty paragraph so the new day follows the text directly
        if (doc.Blocks.LastBlock is Paragraph last && IsEmpty(last) && doc.Blocks.Count > 1) doc.Blocks.Remove(last);
        var lastYear = days.Count > 0 ? days[^1].Day.Year : (int?)null;
        if (lastYear != day.Year) doc.Blocks.Add(CreateYearDivider(day.Year, language));
        doc.Blocks.Add(CreateDayDivider(day, language));
        var p = new Paragraph();
        doc.Blocks.Add(p);
        return p;
    }

    private static Paragraph InsertDayBefore(FlowDocument doc, List<(DateTime Day, BlockUIContainer Divider)> days, int nextIndex,
        DateTime day, string language)
    {
        var next = days[nextIndex];
        var previousYear = nextIndex > 0 ? days[nextIndex - 1].Day.Year : (int?)null;
        // the next day's own year divider stays with it, so the new day goes in front of that divider
        Block anchor = next.Divider.PreviousBlock is { } before && YearOf(before) == next.Day.Year ? before : next.Divider;
        if (previousYear != day.Year) doc.Blocks.InsertBefore(anchor, CreateYearDivider(day.Year, language));
        doc.Blocks.InsertBefore(anchor, CreateDayDivider(day, language));
        var p = new Paragraph();
        doc.Blocks.InsertBefore(anchor, p);
        if (YearOf(anchor) is null && next.Day.Year != day.Year) doc.Blocks.InsertBefore(next.Divider, CreateYearDivider(next.Day.Year, language));
        return p;
    }

    /// <summary>The last paragraph before the next divider (creates one when the section is empty).</summary>
    public static Paragraph LastParagraphOfSection(FlowDocument doc, BlockUIContainer divider)
    {
        Paragraph? lastPara = null;
        var b = divider.NextBlock;
        while (b is not null && !IsDivider(b))
        {
            if (b is Paragraph p) lastPara = p;
            b = b.NextBlock;
        }
        if (lastPara is not null) return lastPara;
        var created = new Paragraph();
        if (b is null) doc.Blocks.Add(created);
        else doc.Blocks.InsertBefore(b, created);
        return created;
    }

    private static bool IsEmpty(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length == 0;

    // ------------------------------------------------------------------ search

    private static readonly Regex[] DateQueries =
    {
        new(@"^(?<y>\d{4})[-./ ](?<m>\d{1,2})[-./ ](?<d>\d{1,2})\.?$"),
        new(@"^(?:(?<y>\d{4})\s*년\s*)?(?<m>\d{1,2})\s*월\s*(?<d>\d{1,2})\s*일?(?:\s*\(?[일월화수목금토]\)?)?$"),
        new(@"^(?<m>\d{1,2})[/.-](?<d>\d{1,2})\.?$"),
    };

    /// <summary>Parses "2026-10-22", "2026.10.22", "10월 22일 (목)", "2026년 10월 22일", "10/22", "10.22".</summary>
    public static bool TryParseDateQuery(string? text, out int? year, out int month, out int day)
    {
        year = null;
        month = day = 0;
        var s = (text ?? "").Trim();
        foreach (var re in DateQueries)
        {
            var m = re.Match(s);
            if (!m.Success) continue;
            year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
            month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (month is < 1 or > 12 || day is < 1 or > 31) return false;
            return true;
        }
        return false;
    }

    /// <summary>Finds the divider for a date query; without a year the most recent matching day wins.</summary>
    public static BlockUIContainer? FindDay(FlowDocument doc, string query)
    {
        if (!TryParseDateQuery(query, out var y, out var m, out var d)) return null;
        return Days(doc).LastOrDefault(x => x.Day.Month == m && x.Day.Day == d && (y is null || x.Day.Year == y)).Divider;
    }

    // -------------------------------------------------------------- plain text

    /// <summary>
    /// Plain text of a page with dividers written as "## 2026-10-22 10월 22일 (목)" lines, so the dates are searchable
    /// and Claude sees which day each line belongs to.
    /// </summary>
    public static string GetPlainText(FlowDocument doc, string language)
    {
        var sb = new StringBuilder();
        foreach (var b in doc.Blocks)
        {
            if (DayOf(b) is { } day)
                sb.Append("## ").Append(day.ToString(DateFormat, CultureInfo.InvariantCulture)).Append(' ').AppendLine(DayLabel(day, language));
            else if (YearOf(b) is { } year)
                sb.Append("# ").AppendLine(YearLabel(year, language));
            else
                sb.AppendLine(new TextRange(b.ContentStart, b.ContentEnd).Text.TrimEnd('\r', '\n'));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Only the days of a page's plain text that fall in [from, to] (inclusive), dividers kept.</summary>
    public static string FilterDays(string plain, DateTime from, DateTime to)
    {
        var sb = new StringBuilder();
        var include = false;
        foreach (var line in plain.Replace("\r\n", "\n").Split('\n'))
        {
            var m = PlainDateLine.Match(line);
            if (m.Success) include = TryParseIso(m.Groups[1].Value, out var d) && d >= from.Date && d <= to.Date;
            else if (line.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (include) sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    // --------------------------------------------------------- storage/export

    // WPF's XamlPackage drops UI elements (a BlockUIContainer comes back as an empty paragraph), so on disk a divider is a
    // marker paragraph that is turned back into a divider on load. Users never see the marker text.
    private static readonly Regex Marker = new(@"^\[\[soko-(day:(?<d>\d{4}-\d{2}-\d{2})|year:(?<y>\d{4}))\]\]$");

    private static string MarkerText(Block divider) =>
        DayOf(divider) is { } day ? $"[[soko-day:{day.ToString(DateFormat, CultureInfo.InvariantCulture)}]]" : $"[[soko-year:{YearOf(divider)}]]";

    /// <summary>Appended on save to user text that happens to look like a marker, so it is not read back as a divider.</summary>
    private const char MarkerEscape = (char)0x200B; // zero-width space

    /// <summary>Serializes a page for storage: dividers become marker paragraphs, everything else keeps its formatting.</summary>
    public static byte[] Save(FlowDocument page)
    {
        var markers = new HashSet<Paragraph>();
        var copy = CopyReplacingDividers(page, b =>
        {
            var p = new Paragraph(new Run(MarkerText(b)));
            markers.Add(p);
            return p;
        });
        foreach (var p in copy.Blocks.OfType<Paragraph>().ToList())
            if (!markers.Contains(p) && Marker.IsMatch(new TextRange(p.ContentStart, p.ContentEnd).Text.Trim()))
                p.Inlines.Add(new Run(MarkerEscape.ToString()));
        return DocumentService.Save(copy);
    }

    /// <summary>Loads a stored page and turns marker paragraphs back into dividers.</summary>
    public static void Load(FlowDocument doc, byte[]? data, string language)
    {
        DocumentService.Load(doc, data);
        foreach (var p in doc.Blocks.OfType<Paragraph>().ToList())
        {
            var text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
            if (text.Length > 0 && text[^1] == MarkerEscape && Marker.IsMatch(text[..^1]))
            {
                // user text that looks like a marker: drop the escape so it reads exactly as typed
                var end = p.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
                new TextRange(end.GetPositionAtOffset(-1, LogicalDirection.Backward) ?? end, end).Text = "";
                continue;
            }
            var m = Marker.Match(text);
            if (!m.Success) continue;
            Block divider = m.Groups["d"].Success && TryParseIso(m.Groups["d"].Value, out var day)
                ? CreateDayDivider(day, language)
                : CreateYearDivider(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture), language);
            doc.Blocks.InsertBefore(p, divider);
            doc.Blocks.Remove(p);
        }
    }

    /// <summary>A copy with dividers turned into ordinary bold paragraphs, for RTF/print where UI elements are dropped.</summary>
    public static FlowDocument CloneForExport(FlowDocument page, string language) =>
        CopyReplacingDividers(page, b =>
        {
            var isYear = YearOf(b) is not null;
            return new Paragraph(new Run(isYear ? YearLabel(YearOf(b)!.Value, language) : DayLabel(DayOf(b)!.Value, language)))
            {
                FontWeight = FontWeights.Bold,
                FontSize = isYear ? 18 : 15,
                Margin = new Thickness(0, 16, 0, 4),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, isYear ? 0 : 1),
            };
        });

    /// <summary>
    /// Copies a page without touching it: the text between dividers is copied with its formatting through XamlPackage
    /// (segment by segment, since dividers would not survive), dividers are replaced by <paramref name="replace"/>.
    /// </summary>
    private static FlowDocument CopyReplacingDividers(FlowDocument page, Func<Block, Paragraph> replace)
    {
        var copy = new FlowDocument { FontFamily = page.FontFamily, FontSize = page.FontSize };
        TextPointer? segmentStart = page.ContentStart;
        foreach (var b in page.Blocks.ToList())
        {
            if (!IsDivider(b)) continue;
            AppendSegment(copy, segmentStart!, b.ElementStart);
            copy.Blocks.Add(replace(b));
            segmentStart = b.ElementEnd;
        }
        AppendSegment(copy, segmentStart!, page.ContentEnd);
        return copy;
    }

    private static void AppendSegment(FlowDocument target, TextPointer start, TextPointer end)
    {
        if (start.CompareTo(end) >= 0) return;
        var range = new TextRange(start, end);
        if (range.IsEmpty && range.Text.Length == 0 && start.GetOffsetToPosition(end) <= 2) return;
        using var ms = new System.IO.MemoryStream();
        range.Save(ms, DataFormats.XamlPackage);
        var piece = new FlowDocument();
        new TextRange(piece.ContentStart, piece.ContentEnd).Load(new System.IO.MemoryStream(ms.ToArray()), DataFormats.XamlPackage);
        foreach (var block in piece.Blocks.ToList())
        {
            piece.Blocks.Remove(block);
            target.Blocks.Add(block);
        }
    }

    private static bool TryParseIso(string s, out DateTime d) =>
        DateTime.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
}
