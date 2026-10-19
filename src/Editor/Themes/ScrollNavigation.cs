using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

/// <summary>Keeps wide content reachable by wheel when scrollbar chrome is hidden.</summary>
public static class ScrollNavigation
{
    /// <summary>Enables Shift+wheel horizontal navigation on a scroll viewer.</summary>
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollNavigation), new PropertyMetadata(false, Changed));
    /// <summary>Reads wheel navigation opt-in.</summary><param name="view">Scroll viewer.</param><returns>Whether enabled.</returns>
    public static bool GetEnabled(DependencyObject view) => (bool)view.GetValue(EnabledProperty);
    /// <summary>Enables wheel navigation.</summary><param name="view">Scroll viewer.</param><param name="enabled">Whether enabled.</param>
    public static void SetEnabled(DependencyObject view, bool enabled) => view.SetValue(EnabledProperty, enabled);
    private static void Changed(DependencyObject view, DependencyPropertyChangedEventArgs args)
    {
        if (view is not ScrollViewer viewer) return;
        if ((bool)args.NewValue) viewer.PreviewMouseWheel += Wheel;
        else viewer.PreviewMouseWheel -= Wheel;
    }
    private static void Wheel(object sender, MouseWheelEventArgs args)
    {
        var viewer = (ScrollViewer)sender;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Let the closest scrollable child consume its own wheel input first.
        for (DependencyObject? current = args.OriginalSource as DependencyObject; current != null && current != viewer;)
        {
            if (current is ScrollViewer child && (child.ScrollableWidth > 0 || (!shift && child.ScrollableHeight > 0))) return;
            current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        if (TryScrollHorizontal(viewer, args.Delta, shift)) args.Handled = true;
    }
    internal static bool TryScrollHorizontal(ScrollViewer viewer, int delta, bool shift)
    {
        if (delta == 0 || viewer.ScrollableWidth <= 0 || (!shift && viewer.ScrollableHeight > 0)) return false;
        double target = Math.Clamp(viewer.HorizontalOffset - delta, 0, viewer.ScrollableWidth);
        if (target == viewer.HorizontalOffset) return false;
        viewer.ScrollToHorizontalOffset(target);
        return true;
    }
}
