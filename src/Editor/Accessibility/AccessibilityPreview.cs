using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>A small software color-filter preview; never applies a full-window effect to the editor or game.</summary>
public sealed class AccessibilityPreview : FrameworkElement
{
    /// <summary>Identifies the simulated mode for this thumbnail.</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(nameof(Mode), typeof(string), typeof(AccessibilityPreview), new FrameworkPropertyMetadata("normal", FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Gets or sets the preview mode, independently of the selected window mode.</summary>
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 200, ActualHeight / 76));
        var dark = new SolidColorBrush(ColorVisionFilter.Transform(Color.FromRgb(27, 29, 39), Mode));
        dc.DrawRoundedRectangle(dark, null, new Rect(0, 0, 200, 76), 5, 5);
        string[] groups = ["narrative", "logic", "visual"]; string[] labels = ["Реплика", "Условие", "Фон"];
        for (int i = 0; i < groups.Length; i++)
        {
            var presentation = BlockAccessibility.Presentation(groups[i], Mode); double x = 6 + i * 65;
            var ink = new SolidColorBrush(ColorVisionFilter.Transform(presentation.Foreground, Mode));
            var color = new SolidColorBrush(ColorVisionFilter.Transform(presentation.Color, Mode));
            dc.DrawRoundedRectangle(color, new Pen(Brushes.White, presentation.BorderWidth == 3 ? 2.5 : .5), new Rect(x, 14, 57, 47), 4, 4);
            if (presentation.Badges)
            { dc.PushTransform(new MatrixTransform(.48, 0, 0, .48, x + 22, 20)); dc.DrawGeometry(ink, null, CategoryIcon.GeometryFor(groups[i])); dc.Pop(); }
            dc.DrawText(new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, ink, 1), new Point(x + 5, presentation.Badges ? 42 : 31));
            if (i < 2) dc.DrawLine(new Pen(Brushes.White, 1), new Point(x + 57, 37), new Point(x + 65, 37));
        }
        dc.Pop();
    }
}
