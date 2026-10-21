using SokoNote.Services;
using System.Windows;

namespace SokoNote.Views;

public partial class LinkDialog : Window
{
    public string LinkText => TextBox1.Text;
    public Uri? Url { get; private set; }
    public bool RemoveRequested { get; private set; }

    public LinkDialog(Window owner, string text, string? url, bool canRemove)
    {
        InitializeComponent();
        Owner = owner;
        TextBox1.Text = text;
        UrlBox.Text = url ?? "";
        RemoveButton.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(UrlBox.Text) && System.Windows.Clipboard.ContainsText())
            {
                var clip = System.Windows.Clipboard.GetText().Trim();
                if (TryNormalize(clip, out _)) UrlBox.Text = clip;
            }
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    public static bool TryNormalize(string? input, out Uri uri) => LinkNormalizer.TryNormalize(input, out uri);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalize(UrlBox.Text, out var uri))
        {
            ErrorText.Visibility = Visibility.Visible;
            UrlBox.Focus();
            return;
        }
        Url = uri;
        if (string.IsNullOrWhiteSpace(TextBox1.Text)) TextBox1.Text = uri.ToString();
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        RemoveRequested = true;
        DialogResult = true;
    }
}
