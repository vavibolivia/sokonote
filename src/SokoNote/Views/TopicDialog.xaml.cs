using System.Windows;
using System.Windows.Controls;
using SokoNote.Controls;
using SokoNote.Services;

namespace SokoNote.Views;

public partial class TopicDialog : Window
{
    public string TopicName => NameBox.Text.Trim();
    public string Color { get; private set; }

    private readonly Func<string, bool>? _isDuplicate;

    /// <param name="isDuplicate">Returns true when another topic already has the given name.</param>
    public TopicDialog(Window owner, string? name, string? color, Func<string, bool>? isDuplicate = null)
    {
        _isDuplicate = isDuplicate;
        InitializeComponent();
        Owner = owner;
        Title = name is null ? Loc.T("S.TopicDialogNew") : Loc.T("S.TopicDialogEdit");
        NameBox.Text = name ?? "";
        Color = color ?? ColorPalette.TopicColors[Random.Shared.Next(ColorPalette.TopicColors.Length)];
        var colors = ColorPalette.TopicColors.ToList();
        if (!colors.Contains(Color, StringComparer.OrdinalIgnoreCase)) colors.Insert(0, Color);
        Swatches.ItemsSource = colors;
        Loaded += (_, _) =>
        {
            foreach (var item in Swatches.Items)
            {
                if (Swatches.ItemContainerGenerator.ContainerFromItem(item) is ContentPresenter cp
                    && VisualTreeHelperEx.FindChild<RadioButton>(cp) is { } rb
                    && string.Equals(rb.Tag as string, Color, StringComparison.OrdinalIgnoreCase))
                {
                    rb.IsChecked = true;
                    break;
                }
            }
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Swatch_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string hex }) Color = hex;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = TopicName.Length == 0 ? Loc.T("S.TopicNameRequired")
            : _isDuplicate?.Invoke(TopicName) == true ? Loc.F("S.TopicNameDuplicate", TopicName)
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}

public static class VisualTreeHelperEx
{
    public static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var inner = FindChild<T>(child);
            if (inner is not null) return inner;
        }
        return null;
    }

    public static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T t) return t;
            child = child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
