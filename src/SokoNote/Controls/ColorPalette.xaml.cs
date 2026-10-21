using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SokoNote.Services;

namespace SokoNote.Controls;

/// <summary>Swatch grid + HEX entry. Raises <see cref="ColorPicked"/> with a hex string, or null for "automatic".</summary>
public partial class ColorPalette : UserControl
{
    public static readonly string[] TextColors =
    {
        "#000000", "#1F2430", "#4B5563", "#6B7280", "#9CA3AF", "#D1D5DB", "#FFFFFF",
        "#D64545", "#E8590C", "#E6A100", "#2F9E44", "#0CA678", "#1C7ED6", "#4F6BED", "#7048E8", "#D6336C",
        "#B91C1C", "#9A3412", "#92400E", "#166534", "#065F46", "#1E3A8A", "#3730A3", "#4C1D95", "#9D174D",
    };

    public static readonly string[] HighlightColors =
    {
        "#FFF176", "#FFE082", "#FFCCBC", "#F8BBD0", "#E1BEE7", "#C5CAE9", "#BBDEFB", "#B2EBF2",
        "#C8E6C9", "#DCEDC8", "#F0F4C3", "#D7CCC8", "#CFD8DC", "#EEEEEE",
    };

    public static readonly string[] PaperColors =
    {
        "#FFFFFF", "#FFFDF5", "#FFF8E1", "#FDF6EC", "#F7F3E9", "#FAF3DD", "#FFF0F0", "#FDEEF4",
        "#F3F0FF", "#EEF2FF", "#E8F4FD", "#E6F7F1", "#EDF7E4", "#F5F5F5", "#ECEFF4", "#E3E6EC",
        "#2B2B2B", "#1E222B", "#1F2A1F", "#2A2230",
    };

    public static readonly string[] TopicColors =
    {
        "#4F6BED", "#1C7ED6", "#0CA678", "#2F9E44", "#E6A100", "#E8590C", "#D64545", "#D6336C",
        "#7048E8", "#6B7280", "#0B7285", "#5C940D", "#C92A2A", "#862E9C", "#364FC7", "#1F2430",
    };

    public event EventHandler<string?>? ColorPicked;

    public ColorPalette()
    {
        InitializeComponent();
        Swatches.ItemsSource = TextColors;
    }

    public void Configure(IEnumerable<string> colors, bool allowAutomatic, string? current)
    {
        Swatches.ItemsSource = colors.ToArray();
        AutoButton.Visibility = allowAutomatic ? Visibility.Visible : Visibility.Collapsed;
        HexBox.Text = current ?? "";
    }

    public void FocusHex()
    {
        HexBox.Focus();
        HexBox.SelectAll();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) ColorPicked?.Invoke(this, hex);
    }

    private void Auto_Click(object sender, RoutedEventArgs e) => ColorPicked?.Invoke(this, null);

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplyHex();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHex();
            e.Handled = true;
        }
    }

    private void ApplyHex()
    {
        if (DocumentService.TryParseColor(HexBox.Text, out var c)) ColorPicked?.Invoke(this, DocumentService.ToHex(c));
        else FocusHex();
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var ok = DocumentService.TryParseColor(HexBox.Text, out var c);
        PreviewSwatch.Background = ok ? new SolidColorBrush(c) : Brushes.Transparent;
        ApplyButton.IsEnabled = ok;
    }
}
