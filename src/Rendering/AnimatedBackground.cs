using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

/// <summary>Scalable vector patterns with a seamless phase loop and reduced-motion support.</summary>
public sealed class AnimatedBackground : FrameworkElement
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double), typeof(AnimatedBackground),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    private GameTheme theme = new();
    private bool motion = true;
    public AnimatedBackground()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
    }
    public void Configure(GameTheme value, bool animate)
    {
        theme = value; motion = animate && value.AnimationEnabled;
        InvalidateVisual(); Start();
    }
    private void Start()
    {
        BeginAnimation(PhaseProperty, null); Phase = 0;
        if (!motion || !theme.Pattern || theme.PatternStyle == "none") return;
        BeginAnimation(PhaseProperty, new DoubleAnimation(0, 2 * Math.PI, TimeSpan.FromSeconds(12 / theme.AnimationSpeed))
        { RepeatBehavior = RepeatBehavior.Forever });
    }
    public static double WaveY(double x, double width, double baseline, double amplitude, double phase) =>
        baseline + amplitude * Math.Sin(x / Math.Max(width, 1) * 4 * Math.PI + phase);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!theme.Pattern || theme.PatternStyle is "none" or "ornament" || ActualWidth < 1 || ActualHeight < 1) return;
        var ink = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.PatternColor));
        ink.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.PushOpacity(0.28);
        var pen = new Pen(ink, 1.5);
        if (theme.PatternStyle == "waves")
        {
            for (int row = 0; row < 9; row++)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    double baseline = (row - 0.5) * ActualHeight / 7;
                    path.BeginFigure(new Point(0, WaveY(0, ActualWidth, baseline, theme.WaveAmplitude, Phase + row * 0.5)), false, false);
                    for (int step = 1; step <= 100; step++)
                    {
                        double x = step * ActualWidth / 100;
                        path.LineTo(new Point(x, WaveY(x, ActualWidth, baseline, theme.WaveAmplitude, Phase + row * 0.5)), true, false);
                    }
                }
                geometry.Freeze(); dc.DrawGeometry(null, pen, geometry);
            }
        }
        else
        {
            double shift = Phase / (2 * Math.PI) * 60;
            for (double x = -60 + shift; x < ActualWidth + 60; x += 60)
                for (double y = -60 + shift; y < ActualHeight + 60; y += 60)
                    if (theme.PatternStyle == "dots") dc.DrawEllipse(ink, null, new Point(x, y), 2.5, 2.5);
                    else { dc.DrawLine(pen, new Point(x, y), new Point(x + 60, y)); dc.DrawLine(pen, new Point(x, y), new Point(x, y + 60)); }
        }
        dc.Pop(); dc.Pop();
    }
}

public static class ThemePalettes
{
    public static readonly string[] Names = ["violet", "ocean", "rose", "forest", "amber", "custom"];
    public static void Apply(GameTheme theme, string palette)
    {
        var colors = palette switch
        {
            "violet" => new[] { "#FF201134", "#FF704697", "#FFD3ACFF", "#EB3C2654" },
            "ocean" => new[] { "#FF0D2435", "#FF368AA3", "#FFA5E9FF", "#EB21475C" },
            "rose" => new[] { "#FF321824", "#FF9F5677", "#FFFFB8D4", "#EB562C40" },
            "forest" => new[] { "#FF122A24", "#FF4B9178", "#FFBBE5C2", "#EB284D40" },
            "amber" => new[] { "#FF30251A", "#FFA98748", "#FFFFDCA1", "#EB52402A" },
            "custom" => null,
            _ => throw new ArgumentException("Неизвестная палитра.")
        };
        theme.Palette = palette;
        if (colors == null) return;
        theme.BackgroundColor = colors[0]; theme.PatternColor = colors[1]; theme.AccentColor = colors[2]; theme.ButtonColor = colors[3];
        theme.Dialogue.SpeakerColor = colors[2]; theme.Dialogue.BorderColor = colors[1]; theme.Dialogue.BackgroundColor = colors[0];
    }
}
