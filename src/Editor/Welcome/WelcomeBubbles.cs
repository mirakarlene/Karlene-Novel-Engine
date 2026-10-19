using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>A persisted decorative-object choice for the welcome screen.</summary>
/// <param name="Id">Stable settings discriminator.</param><param name="Label">User-facing object name.</param>
public sealed record WelcomeObjectOption(string Id, string Label);

/// <summary>Soft vector bubbles drifting behind the welcome UI, repelled by its pointer.</summary>
public sealed class WelcomeBubbles : WelcomeAnimation
{
    /// <summary>Available shapes; all use the existing particle motion.</summary>
    public static IReadOnlyList<WelcomeObjectOption> Objects { get; } = [new("bubbles", "Мыльные пузыри"), new("stars", "Звёзды"), new("petals", "Лепестки"), new("crystals", "Кристаллы"), new("fireflies", "Светлячки")];
    /// <summary>Resolves a shape ID, falling back to the original bubbles.</summary>
    /// <param name="value">Stored or selected shape.</param><returns>A supported shape ID.</returns>
    public static string Normalize(string? value) => Objects.Any(option => option.Id == value) ? value! : "bubbles";
    /// <summary>Identifies the decorative shape to render.</summary>
    public static readonly DependencyProperty ObjectKindProperty = DependencyProperty.Register(nameof(ObjectKind), typeof(string), typeof(WelcomeBubbles), new FrameworkPropertyMetadata("bubbles", FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Gets or sets the decorative shape without resetting particle positions.</summary>
    public string ObjectKind { get => (string)GetValue(ObjectKindProperty); set => SetValue(ObjectKindProperty, value); }
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(WelcomeBubbles), new FrameworkPropertyMetadata(Brushes.Plum, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(WelcomeBubbles), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    private readonly BubbleField field = new();
    private Point? pointer;
    private readonly List<WelcomeArtCache> sprites = [];
    private (Color Ink, Color Accent, string Kind, double Dpi) cachedKey;
    internal int CacheBuilds { get; private set; }
    private GraphicsLayer? graphics;
    private bool preparing;
    private int generation;
    public WelcomeBubbles() { Unloaded += (_, _) => { graphics?.Dispose(); graphics = null; generation++; preparing = false; }; }
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
        var key = (color, accent, Normalize(ObjectKind), VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (sprites.Count != field.Bubbles.Count || cachedKey != key)
        {
            if (IsLoaded && IsVisible && Window.GetWindow(this)?.IsVisible == true)
            { PrepareSprites(key); if (sprites.Count == 0) { dc.Pop(); return; } }
            else { sprites.Clear(); foreach (var bubble in field.Bubbles) sprites.Add(WelcomeArtCache.Create(Artwork(bubble.Radius, key.Item3, color, accent), key.Item4)); cachedKey = key; CacheBuilds++; }
        }
        if (IsLoaded && IsVisible && Window.GetWindow(this)?.IsVisible == true)
        {
            graphics ??= new(this);
            double density = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (graphics.TryDraw(dc, () =>
            {
                var items = new List<GraphicsSprite>(field.Bubbles.Count);
                for (int i = 0; i < field.Bubbles.Count; i++)
                {
                    var bounds = sprites[i].Bounds; bounds.Offset(field.Bubbles[i].X, field.Bubbles[i].Y);
                    items.Add(new(GraphicsTextures.Get(sprites[i].Image), new Rect(bounds.X * density, bounds.Y * density, bounds.Width * density, bounds.Height * density)));
                }
                return new GraphicsFrame(Math.Max(1, (int)Math.Ceiling(ActualWidth * density)), Math.Max(1, (int)Math.Ceiling(ActualHeight * density)), items);
            }, new Rect(RenderSize))) { dc.Pop(); return; }
        }
        for (int i = 0; i < field.Bubbles.Count; i++)
        {
            var bounds = sprites[i].Bounds; bounds.Offset(field.Bubbles[i].X, field.Bubbles[i].Y); dc.DrawImage(sprites[i].Image, bounds);
        }
        dc.Pop();
    }
    private void PrepareSprites((Color Ink, Color Accent, string Kind, double Dpi) key)
    {
        if (preparing) return; preparing = true;
        int revision = generation; var radii = field.Bubbles.Select(bubble => bubble.Radius).ToArray(); var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            try
            {
                var artwork = radii.Select(radius => WelcomeArtCache.Create(Artwork(radius, key.Kind, key.Ink, key.Accent), key.Dpi)).ToArray();
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() =>
                {
                    if (revision != generation) return;
                    var current = ((Ink as SolidColorBrush)?.Color ?? Colors.Plum, (Accent as SolidColorBrush)?.Color ?? Colors.MediumPurple, Normalize(ObjectKind), VisualTreeHelper.GetDpi(this).DpiScaleX);
                    if (current == key) { sprites.Clear(); sprites.AddRange(artwork); cachedKey = key; CacheBuilds++; }
                    preparing = false; InvalidateVisual();
                }));
            }
            catch (Exception error)
            {
                EngineDiagnostics.Trace("Подготовка частиц", error.Message);
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { if (revision == generation) preparing = false; }));
            }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "KNE welcome artwork" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private static DrawingGroup Artwork(double radius, string kind, Color color, Color accent)
    {
        var art = new DrawingGroup(); using (var dc = art.Open())
        {
        var fill = new RadialGradientBrush { Center = new Point(.4, .35), GradientOrigin = new Point(.3, .22), RadiusX = .65, RadiusY = .65 };
        fill.GradientStops.Add(new(Color.FromArgb(2, color.R, color.G, color.B), 0));
        fill.GradientStops.Add(new(Color.FromArgb(6, accent.R, accent.G, accent.B), .6));
        fill.GradientStops.Add(new(Color.FromArgb(30, accent.R, accent.G, accent.B), .9));
        fill.GradientStops.Add(new(Color.FromArgb(12, color.R, color.G, color.B), 1)); fill.Freeze();
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(45, (byte)((accent.R + color.R) / 2), (byte)((accent.G + color.G) / 2), (byte)((accent.B + color.B) / 2))), 1); edge.Freeze();
        var shine = new Pen(new SolidColorBrush(Color.FromArgb(48, color.R, color.G, color.B)), 1.4); shine.Freeze();
        if (kind == "bubbles")
        {
            dc.DrawEllipse(fill, edge, new Point(), radius, radius);
            dc.DrawEllipse(null, shine, new Point(-radius * .26, -radius * .32), radius * .32, radius * .16);
        }
        else if (kind == "fireflies")
        {
            dc.DrawEllipse(fill, null, new Point(), radius, radius);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)), null, new Point(), 2.5, 2.5);
        }
        else
        {
            var geometry = new StreamGeometry(); using (var path = geometry.Open())
            {
                if (kind == "petals")
                { path.BeginFigure(new Point(0, -radius), true, true); path.BezierTo(new Point(radius, -radius * .4), new Point(radius * .7, radius * .8), new Point(0, radius), true, false); path.BezierTo(new Point(-radius * .6, radius * .2), new Point(-radius * .3, -radius * .8), new Point(0, -radius), true, false); }
                else
                {
                    int count = kind == "stars" ? 10 : 4;
                    for (int i = 0; i < count; i++) { double angle = i * Math.Tau / count - Math.PI / 2, r = kind == "stars" && i % 2 == 1 ? radius * .44 : radius; var point = new Point(Math.Cos(angle) * r, Math.Sin(angle) * r); if (i == 0) path.BeginFigure(point, true, true); else path.LineTo(point, true, false); }
                }
            }
            geometry.Freeze(); dc.DrawGeometry(fill, edge, geometry);
            dc.DrawLine(shine, new Point(0, -radius * .6), new Point(0, radius * .6));
        }
        }
        art.Freeze(); return art;
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
