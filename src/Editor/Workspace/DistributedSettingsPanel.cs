using System.Windows;
using System.Windows.Controls;

namespace KNE;

/// <summary>Distributes spare card height between complete fields, preserving their natural sizes.</summary>
internal sealed class DistributedSettingsPanel : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren) { child.Measure(new(available.Width, double.PositiveInfinity)); width = Math.Max(width, child.DesiredSize.Width); height += child.DesiredSize.Height; }
        return new(width, height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        double gap = children.Length < 2 ? 0 : Math.Max(0, final.Height - children.Sum(c => c.DesiredSize.Height)) / (children.Length - 1), top = 0;
        foreach (var child in children) { child.Arrange(new Rect(0, top, final.Width, child.DesiredSize.Height)); top += child.DesiredSize.Height + gap; }
        return final;
    }
}

/// <summary>Fills the viewport with cards in proportion to their natural content; feedback stays compact.</summary>
internal sealed class UniformSettingsColumn : Panel
{
    protected override Size MeasureOverride(Size available)
    {
        foreach (UIElement child in InternalChildren) child.Measure(new(available.Width, double.PositiveInfinity));
        return new(InternalChildren.Cast<UIElement>().Select(c => c.DesiredSize.Width).DefaultIfEmpty(0).Max(), InternalChildren.Cast<UIElement>().Sum(c => c.DesiredSize.Height));
    }
    protected override Size ArrangeOverride(Size final)
    {
        double natural = InternalChildren.Cast<UIElement>().Sum(c => c.DesiredSize.Height), cards = InternalChildren.Cast<UIElement>().Where(c => c is Border).Sum(c => c.DesiredSize.Height), top = 0;
        foreach (UIElement child in InternalChildren)
        {
            double height = child.DesiredSize.Height + (child is Border && cards > 0 ? Math.Max(0, final.Height - natural) * child.DesiredSize.Height / cards : 0);
            child.Arrange(new Rect(0, top, final.Width, height)); top += height;
        }
        return final;
    }
}
