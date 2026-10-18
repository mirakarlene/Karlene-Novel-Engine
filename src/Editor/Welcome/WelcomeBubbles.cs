using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>Soft vector bubbles drifting behind the welcome UI, repelled by its pointer.</summary>
public sealed class WelcomeBubbles : WelcomeAnimation
{
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(WelcomeBubbles), new FrameworkPropertyMetadata(Brushes.Plum, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(WelcomeBubbles), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    private readonly BubbleField field = new();
    private Point? pointer;
    internal BubbleField Field => this.field;
    public void SetPointer(Point? point) => pointer = point is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y) ? p : null;
    protected override void Advance(double seconds) => field.Step(seconds, RenderSize, pointer);
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        field.Resize(RenderSize);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        Color color = (Ink as SolidColorBrush)?.Color ?? Colors.Plum;
        Color accent = (Accent as SolidColorBrush)?.Color ?? Colors.MediumPurple;
        var fill = new RadialGradientBrush { Center = new Point(.4, .35), GradientOrigin = new Point(.3, .22), RadiusX = .65, RadiusY = .65 };
        fill.GradientStops.Add(new(Color.FromArgb(2, color.R, color.G, color.B), 0));
        fill.GradientStops.Add(new(Color.FromArgb(6, accent.R, accent.G, accent.B), .6));
        fill.GradientStops.Add(new(Color.FromArgb(30, accent.R, accent.G, accent.B), .9));
        fill.GradientStops.Add(new(Color.FromArgb(12, color.R, color.G, color.B), 1)); fill.Freeze();
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(45, (byte)((accent.R + color.R) / 2), (byte)((accent.G + color.G) / 2), (byte)((accent.B + color.B) / 2))), 1); edge.Freeze();
        var shine = new Pen(new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B)), 1.4); shine.Freeze();
        foreach (var bubble in field.Bubbles)
        {
            dc.DrawEllipse(fill, edge, new Point(bubble.X, bubble.Y), bubble.Radius, bubble.Radius);
            dc.DrawEllipse(null, shine, new Point(bubble.X - bubble.Radius * .26, bubble.Y - bubble.Radius * .32), bubble.Radius * .32, bubble.Radius * .16);
        }
        dc.Pop();
    }
}

internal sealed class Bubble
{
    internal double X, Y, Radius, Speed, Drift, Vx, Vy;
}

/// <summary>Deterministic bounded motion independent of the UI clock.</summary>
internal sealed class BubbleField
{
    internal IReadOnlyList<Bubble> Bubbles => bubbles;
    private readonly List<Bubble> bubbles = [];
    private Size size;
    private double phase;
    internal void Resize(Size next)
    {
        if (next.Width <= 0 || next.Height <= 0 || !double.IsFinite(next.Width) || !double.IsFinite(next.Height)) return;
        if (bubbles.Count == 0)
        {
            var random = new Random(0x4B4E45);
            for (int i = 0; i < 30; i++) bubbles.Add(new() { X = random.NextDouble() * next.Width, Y = random.NextDouble() * next.Height, Radius = 12 + random.NextDouble() * 34, Speed = 5 + random.NextDouble() * 12, Drift = random.NextDouble() * Math.Tau });
        }
        else if (size.Width > 0 && size != next)
            foreach (var bubble in bubbles) { bubble.X *= next.Width / size.Width; bubble.Y *= next.Height / size.Height; }
        size = next;
    }
    internal void Step(double seconds, Size next, Point? pointer)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Resize(next); if (size.Width <= 0 || size.Height <= 0) return;
        double dt = Math.Min(.05, seconds); phase = (phase + dt) % Math.Tau;
        foreach (var bubble in bubbles)
        {
            double damping = Math.Exp(-2.5 * dt); bubble.Vx *= damping; bubble.Vy *= damping;
            if (pointer is { } point && double.IsFinite(point.X) && double.IsFinite(point.Y))
            {
                double dx = bubble.X - point.X, dy = bubble.Y - point.Y, distance = Math.Sqrt(dx * dx + dy * dy), reach = 100 + bubble.Radius;
                if (distance < reach)
                {
                    if (distance < .001) { dx = Math.Cos(bubble.Drift); dy = Math.Sin(bubble.Drift); distance = 1; }
                    double force = 250 * (1 - distance / reach) * dt;
                    bubble.Vx = Math.Clamp(bubble.Vx + dx / distance * force, -90, 90); bubble.Vy = Math.Clamp(bubble.Vy + dy / distance * force, -90, 90);
                }
            }
            bubble.X += (Math.Sin(phase + bubble.Drift) * 5 + bubble.Vx) * dt;
            bubble.Y += (-bubble.Speed + bubble.Vy) * dt;
            if (bubble.Y < -bubble.Radius) bubble.Y = size.Height + bubble.Radius;
            if (bubble.Y > size.Height + bubble.Radius) bubble.Y = -bubble.Radius;
            if (bubble.X < -bubble.Radius) bubble.X = size.Width + bubble.Radius;
            if (bubble.X > size.Width + bubble.Radius) bubble.X = -bubble.Radius;
        }
    }
}
