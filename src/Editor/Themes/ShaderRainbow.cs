using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

/// <summary>Retained rainbow animation that stops with its owning editor element.</summary>
internal static class ShaderRainbow
{
    private static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached("Brush", typeof(LinearGradientBrush), typeof(ShaderRainbow));
    internal static LinearGradientBrush Create(FrameworkElement owner)
    {
        if (owner.GetValue(BrushProperty) is LinearGradientBrush existing) return existing;
        var transform = new RotateTransform(0, .5, .5);
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), RelativeTransform = transform };
        owner.SetValue(BrushProperty, brush);
        string[] colors = ["#F06AA6", "#F6AD55", "#F4E285", "#58CFA0", "#55BEF0", "#7C5CFF", "#D275E5"];
        for (int i = 0; i < colors.Length; i++) brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(colors[i]), i / (double)(colors.Length - 1)));
        void Refresh()
        {
            transform.BeginAnimation(RotateTransform.AngleProperty, null);
            if (owner.IsLoaded && owner.IsVisible && owner.IsMouseOver && owner.IsEnabled && CategoryHover.GetAvailable(owner))
                transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(8)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        owner.Loaded += (_, _) => Refresh(); owner.Unloaded += (_, _) => transform.BeginAnimation(RotateTransform.AngleProperty, null);
        owner.IsVisibleChanged += (_, _) => Refresh(); owner.IsEnabledChanged += (_, _) => Refresh();
        owner.MouseEnter += (_, _) => Refresh(); owner.MouseLeave += (_, _) => Refresh();
        Refresh(); return brush;
    }
}
