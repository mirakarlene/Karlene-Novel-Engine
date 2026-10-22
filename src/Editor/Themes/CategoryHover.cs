using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

/// <summary>Reveals a category's tint from right to left without allocating a new bitmap per frame.</summary>
public static class CategoryHover
{
    /// <summary>The category-colored fill used by the hover template.</summary>
    public static readonly DependencyProperty ColorProperty = DependencyProperty.RegisterAttached("Color", typeof(Brush), typeof(CategoryHover), new PropertyMetadata(Brushes.Transparent, ColorChanged));
    /// <summary>The readable foreground when the category fill is visible.</summary>
    public static readonly DependencyProperty InkProperty = DependencyProperty.RegisterAttached("Ink", typeof(Brush), typeof(CategoryHover), new PropertyMetadata(Brushes.White));
    /// <summary>The animated fraction of the category fill, from zero to one.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(CategoryHover), new PropertyMetadata(0d));
    /// <summary>Whether this command can currently be activated; favorites can remain enabled separately.</summary>
    public static readonly DependencyProperty AvailableProperty = DependencyProperty.RegisterAttached("Available", typeof(bool), typeof(CategoryHover), new PropertyMetadata(true, AvailabilityChanged));
    /// <summary>Reads command availability.</summary><param name="view">Library card.</param><returns>Whether insertion is allowed.</returns>
    public static bool GetAvailable(DependencyObject view) => (bool)view.GetValue(AvailableProperty);
    /// <summary>Sets command availability without disabling the nested favorite button.</summary><param name="view">Library card.</param><param name="available">Whether insertion is allowed.</param>
    public static void SetAvailable(DependencyObject view, bool available) => view.SetValue(AvailableProperty, available);
    private static void AvailabilityChanged(DependencyObject view, DependencyPropertyChangedEventArgs args)
    {
        if (view is FrameworkElement item && !(bool)args.NewValue) { item.BeginAnimation(ProgressProperty, null); SetProgress(item, 0); }
    }
    /// <summary>Associates a navigation item with live accessibility category colors.</summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached("Group", typeof(string), typeof(CategoryHover), new PropertyMetadata("", GroupChanged));
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(CategoryHover), new PropertyMetadata(false));
    /// <summary>Reads the category fill.</summary><param name="view">Styled item.</param><returns>Category brush.</returns>
    public static Brush GetColor(DependencyObject view) => (Brush)view.GetValue(ColorProperty);
    /// <summary>Sets the category fill.</summary><param name="view">Styled item.</param><param name="color">Category brush.</param>
    public static void SetColor(DependencyObject view, Brush color) => view.SetValue(ColorProperty, color);
    /// <summary>Reads the hover text ink.</summary><param name="view">Styled item.</param><returns>Readable ink.</returns>
    public static Brush GetInk(DependencyObject view) => (Brush)view.GetValue(InkProperty);
    /// <summary>Sets the hover text ink.</summary><param name="view">Styled item.</param><param name="ink">Readable ink.</param>
    public static void SetInk(DependencyObject view, Brush ink) => view.SetValue(InkProperty, ink);
    /// <summary>Reads the current reveal fraction.</summary><param name="view">Styled item.</param><returns>A fraction from zero to one.</returns>
    public static double GetProgress(DependencyObject view) => (double)view.GetValue(ProgressProperty);
    /// <summary>Sets the reveal fraction.</summary><param name="view">Styled item.</param><param name="progress">Reveal fraction.</param>
    public static void SetProgress(DependencyObject view, double progress) => view.SetValue(ProgressProperty, progress);
    /// <summary>Reads a navigation category ID.</summary><param name="view">Navigation item.</param><returns>Group ID.</returns>
    public static string GetGroup(DependencyObject view) => (string)view.GetValue(GroupProperty);
    /// <summary>Associates a navigation item with its category.</summary><param name="view">Navigation item.</param><param name="group">Group ID.</param>
    public static void SetGroup(DependencyObject view, string group) => view.SetValue(GroupProperty, group);
    private static void GroupChanged(DependencyObject view, DependencyPropertyChangedEventArgs args)
    {
        if (view is not FrameworkElement item) return;
        string group = (string)args.NewValue;
        BlockAccessibility.Decorate(item, group, presentation =>
        {
            var color = group switch { "favorites" => Colors.DarkGoldenrod, "recent" => Colors.SlateGray, _ => presentation.Color };
            SetColor(item, group == "shaders" && BlockAccessibility.GetMode(item) == "normal" ? ShaderRainbow.Create(item) : new SolidColorBrush(color));
            double luminance = .2126 * BlockAccessibility.Linear(color.R) + .7152 * BlockAccessibility.Linear(color.G) + .0722 * BlockAccessibility.Linear(color.B);
            SetInk(item, luminance > .179 ? Brushes.Black : Brushes.White);
        });
    }
    private static void ColorChanged(DependencyObject view, DependencyPropertyChangedEventArgs args)
    {
        if (view is not FrameworkElement item || (bool)item.GetValue(HookedProperty)) return;
        item.SetValue(HookedProperty, true);
        item.MouseEnter += (_, _) => { if (item.IsEnabled && GetAvailable(item)) Animate(item, 1); };
        item.MouseLeave += (_, _) => Animate(item, 0);
        item.IsEnabledChanged += (_, _) => { if (!item.IsEnabled) Animate(item, 0); };
    }
    private static void Animate(FrameworkElement item, double target)
    {
        if (!item.IsEnabled || !GetAvailable(item)) { item.BeginAnimation(ProgressProperty, null); SetProgress(item, 0); return; }
        item.BeginAnimation(ProgressProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(.24)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
