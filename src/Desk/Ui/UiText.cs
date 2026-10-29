using System.Windows;
using System.Windows.Controls;

namespace Desk.Ui;

/// <summary>Small building blocks shared by the pages.</summary>
internal static class UiText
{
    /// <summary>A card header: a title and a secondary description line.</summary>
    public static StackPanel Header(string title, string? description)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
        {
            var secondary = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            secondary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(secondary);
        }
        return panel;
    }
}
