using System.Windows;
using System.Windows.Controls;

namespace Desk.Ui;

/// <summary>
/// The left side of a settings card: a title and an optional secondary line.
/// Elements placed inside it in XAML (a status line, say) come after the two.
/// </summary>
public sealed class CardHeader : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(CardHeader),
        new PropertyMetadata("", (d, e) => ((CardHeader)d)._title.Text = (string?)e.NewValue ?? ""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(CardHeader),
        new PropertyMetadata(null, (d, e) => ((CardHeader)d).ShowDescription((string?)e.NewValue)));

    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _description = new()
    {
        Margin = new Thickness(0, 2, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    public CardHeader()
    {
        VerticalAlignment = VerticalAlignment.Center;
        _description.SetResourceReference(StyleProperty, "Desk.Caption");
        Children.Add(_title);
        Children.Add(_description);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    private void ShowDescription(string? text)
    {
        _description.Text = text ?? "";
        _description.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
