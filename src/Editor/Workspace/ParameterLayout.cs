using System.Windows;
using System.Windows.Controls;

namespace KNE;

/// <summary>Compact parameter pages with natural fields and no page-level scrolling or scaling.</summary>
internal static class ParameterLayout
{
    internal static StackPanel Cards(params UIElement[] cards)
    {
        var panel = new StackPanel { Margin = new(12, 0, 12, 0) };
        foreach (var card in cards)
        {
            if (card is FrameworkElement element) element.Margin = new(0, 0, 0, 8);
            panel.Children.Add(card);
        }
        return panel;
    }
    internal static void SpreadFields(Panel panel)
    {
        var fields = new List<UIElement>();
        var children = panel.Children.Cast<UIElement>().ToArray(); panel.Children.Clear();
        int first = 0;
        if (children.FirstOrDefault() is TextBlock { FontSize: >= 15 } heading) { panel.Children.Add(heading); first = 1; }
        for (int i = first; i < children.Length; i++)
        {
            if (children[i] is TextBlock { FontSize: 12, Opacity: 1 } && i + 1 < children.Length)
            {
                var field = new StackPanel(); field.Children.Add(children[i]); field.Children.Add(children[++i]); fields.Add(field);
            }
            else fields.Add(children[i]);
        }
        var left = new StackPanel(); var right = new StackPanel();
        for (int i = 0; i < fields.Count; i++) (i < (fields.Count + 1) / 2 ? left : right).Children.Add(fields[i]);
        panel.Children.Add(Columns(left, right));
    }
    internal static Grid Columns(UIElement left, UIElement right)
    {
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(left); grid.Children.Add(right); Grid.SetColumn(right, 2); return grid;
    }
    internal static void Compact(Panel panel)
    {
        var children = panel.Children.Cast<UIElement>().ToArray(); panel.Children.Clear();
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i] is TextBlock label && label.FontSize == 12 && label.Opacity == 1 && label.FontWeight == FontWeights.Normal && i + 1 < children.Length && children[i + 1] is FrameworkElement input && input is not TextBlock)
            {
                if (label.Text.Length > 36 || input is StackPanel values && values.Children.OfType<WrapPanel>().Any())
                {
                    label.TextWrapping = TextWrapping.Wrap; label.Margin = new(0, 2, 0, 2); CompactControls(input); panel.Children.Add(label); panel.Children.Add(input); i++; continue;
                }
                var row = new Grid { Margin = new(0, 2, 0, 2) }; row.ColumnDefinitions.Add(new() { Width = new GridLength(0.43, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(0.57, GridUnitType.Star) });
                label.TextWrapping = TextWrapping.Wrap; label.Margin = new(0, 0, 8, 0); label.VerticalAlignment = VerticalAlignment.Center;
                input.Margin = new(0); input.MinWidth = 0; input.VerticalAlignment = VerticalAlignment.Center;
                CompactControls(input); Grid.SetColumn(input, 1); row.Children.Add(label); row.Children.Add(input); panel.Children.Add(row); i++;
            }
            else { if (children[i] is FrameworkElement child) { var m = child.Margin; child.Margin = new(m.Left, Math.Min(m.Top, 3), m.Right, Math.Min(m.Bottom, 3)); } CompactControls(children[i]); panel.Children.Add(children[i]); }
        }
    }
    private static void CompactControls(DependencyObject target)
    {
        if (target is FrameworkElement element) { var m = element.Margin; element.Margin = new(m.Left, Math.Min(m.Top, 2), m.Right, Math.Min(m.Bottom, 2)); }
        if (target is CheckBox { Content: string caption } check) check.Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap };
        if (target is Control control && target is not CheckBox) { control.Padding = new(4, 2, 4, 2); control.MinWidth = 0; control.MinHeight = 0; }
        foreach (object child in LogicalTreeHelper.GetChildren(target)) if (child is DependencyObject item) CompactControls(item);
    }
}
