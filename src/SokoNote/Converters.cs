using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SokoNote.Models;
using SokoNote.Services;

namespace SokoNote;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is not null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        DocumentService.BrushFromHex(value as string, Colors.Gray);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class StatusPalette
{
    public static Color Strong(EntryStatus s) => s switch
    {
        EntryStatus.InProgress => Color.FromRgb(0x2F, 0x6F, 0xE4),
        EntryStatus.Done => Color.FromRgb(0x1E, 0x9E, 0x62),
        EntryStatus.Hold => Color.FromRgb(0xB7, 0x7A, 0x12),
        EntryStatus.Urgent => Color.FromRgb(0xD6, 0x45, 0x45),
        _ => Color.FromRgb(0x6B, 0x72, 0x80),
    };

    public static Color Soft(EntryStatus s) => s switch
    {
        EntryStatus.InProgress => Color.FromRgb(0xE6, 0xEF, 0xFD),
        EntryStatus.Done => Color.FromRgb(0xE3, 0xF6, 0xEC),
        EntryStatus.Hold => Color.FromRgb(0xFC, 0xF3, 0xDF),
        EntryStatus.Urgent => Color.FromRgb(0xFD, 0xEC, 0xEC),
        _ => Color.FromRgb(0xF0, 0xF1, 0xF4),
    };
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(StatusPalette.Strong(value is EntryStatus s ? s : EntryStatus.Note));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new SolidColorBrush(StatusPalette.Soft(value is EntryStatus s ? s : EntryStatus.Note));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Loc.T("S.Status." + (value is EntryStatus s ? s : EntryStatus.Note));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ShortDateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is DateTime d ? Loc.ShortDate(d) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Dates that have journal entries, shown as dots in the calendar.</summary>
public static class CalendarMarks
{
    private static HashSet<DateTime> _dates = new();
    private static int _version;

    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    public static int Version => _version;

    public static bool Contains(DateTime d) => _dates.Contains(d.Date);

    public static void Set(HashSet<DateTime> dates)
    {
        _dates = dates;
        _version++;
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Version)));
    }
}

public sealed class CalendarMarkConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is DateTime d && CalendarMarks.Contains(d);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
