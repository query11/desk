using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Desk.Ui;

internal sealed class AdjustmentList : ScrollViewer
{
    public AdjustmentList(IThemeLibrary library, string id)
    {
        MaxHeight = 440;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var rows = new StackPanel();
        Content = rows;
        var changes = library.GetThemeAdjustments(id);
        if (changes.Count == 0) rows.Children.Add(new TextBlock { Text = "当前数值与方案默认值一致。" });
        foreach (var change in changes)
        {
            var parameter = Parameters.Taskbar.Concat(Parameters.StartMenu).FirstOrDefault(p => p.Name == change.Name);
            string Format(string raw) => string.Join(" / ", raw.Split(" / ").Select(part => parameter != null && double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? parameter.Format(parameter.Inverted ? 1 - value : value) : part));
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock { Text = (parameter?.Title ?? change.Name) + (change.Light is { } light ? (light ? " · 浅色" : " · 深色") : "")
                + "\n" + Format(change.Before) + "  →  " + Format(change.After), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0), MaxWidth = 420 };
            row.Children.Add(text);
            var reset = new Wpf.Ui.Controls.Button { Content = "还原此项", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(reset, 1); row.Children.Add(reset);
            reset.Click += (_, _) =>
            {
                try { library.ResetThemeAdjustment(id, change.Key); reset.IsEnabled = false; reset.Content = "已还原"; }
                catch (Exception e) { UiHost.Toast("没有还原成功", e.Message, Wpf.Ui.Controls.SymbolRegular.Warning24); }
            };
            rows.Children.Add(row);
        }
    }
}
