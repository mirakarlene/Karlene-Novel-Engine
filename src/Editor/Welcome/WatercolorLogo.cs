using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>Resolution-independent watercolor lettering with cached pigment washes and gentle per-letter motion.</summary>
public sealed class WatercolorLogo : WelcomeAnimation
{
    public const string BrandText = "Karlene Novel Engine";
    public string Text => BrandText;
    public static readonly DependencyProperty StartColorProperty = DependencyProperty.Register(nameof(StartColor), typeof(Color), typeof(WatercolorLogo), new FrameworkPropertyMetadata(Color.FromRgb(124, 92, 255), Rebuild));
    public static readonly DependencyProperty EndColorProperty = DependencyProperty.Register(nameof(EndColor), typeof(Color), typeof(WatercolorLogo), new FrameworkPropertyMetadata(Color.FromRgb(230, 165, 217), Rebuild));
    public Color StartColor { get => (Color)GetValue(StartColorProperty); set => SetValue(StartColorProperty, value); }
    public Color EndColor { get => (Color)GetValue(EndColorProperty); set => SetValue(EndColorProperty, value); }
    private readonly List<DrawingGroup> letters = [];
    private readonly List<WelcomeArtCache> cachedLetters = [];
    private double cachedScale;
    internal int CacheBuilds { get; private set; }
    private GraphicsLayer? graphics;
    private bool preparing;
    private int generation;
    public WatercolorLogo() { Unloaded += (_, _) => { graphics?.Dispose(); graphics = null; generation++; preparing = false; }; }
    private double width, phase;
    internal double MotionPhase => phase;
    internal Vector OffsetFor(int index) => new(Math.Sin(phase * .7 + index * .8) * 1.4, Math.Sin(phase + index * .65) * 3);
    private static void Rebuild(DependencyObject view, DependencyPropertyChangedEventArgs args)
    { var logo = (WatercolorLogo)view; logo.generation++; logo.preparing = false; logo.letters.Clear(); logo.cachedLetters.Clear(); logo.InvalidateVisual(); }
    protected override void Advance(double seconds) => phase = (phase + seconds * .45) % (Math.Tau * 10);
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        bool live = IsLoaded && IsVisible && Window.GetWindow(this)?.IsVisible == true;
        if (live && letters.Count == 0) { PrepareArtwork(); return; }
        if (letters.Count == 0) BuildLetters();
        double scale = Math.Min(ActualWidth / (width + 12), ActualHeight / 112);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX * Math.Max(1, scale);
        if (cachedLetters.Count != letters.Count || Math.Abs(cachedScale - dpi) > .01)
        {
            if (live) { PrepareArtwork(); if (cachedLetters.Count == 0) return; }
            else { cachedLetters.Clear(); foreach (var letter in letters) cachedLetters.Add(WelcomeArtCache.Create(letter, dpi)); cachedScale = dpi; CacheBuilds++; }
        }
        if (IsLoaded && IsVisible && Window.GetWindow(this)?.IsVisible == true)
        {
            graphics ??= new(this, primary: false);
            double density = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (graphics.TryDraw(dc, () =>
            {
                var sprites = new List<GraphicsSprite>(cachedLetters.Count);
                for (int i = 0; i < cachedLetters.Count; i++)
                {
                    var art = cachedLetters[i]; if (art.Bounds.IsEmpty) continue;
                    var bounds = art.Bounds; bounds.Offset(OffsetFor(i));
                    bounds = new Rect(((ActualWidth - width * scale) / 2 + bounds.X * scale) * density,
                        ((ActualHeight - 100 * scale) / 2 + bounds.Y * scale) * density, bounds.Width * scale * density, bounds.Height * scale * density);
                    sprites.Add(new(GraphicsTextures.Get(art.Image), bounds));
                }
                return new GraphicsFrame(Math.Max(1, (int)Math.Ceiling(ActualWidth * density)), Math.Max(1, (int)Math.Ceiling(ActualHeight * density)), sprites);
            }, new Rect(RenderSize))) return;
        }
        dc.PushTransform(new TranslateTransform((ActualWidth - width * scale) / 2, (ActualHeight - 100 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        for (int i = 0; i < letters.Count; i++)
        {
            var art = cachedLetters[i]; if (art.Bounds.IsEmpty) continue;
            Vector offset = OffsetFor(i); var bounds = art.Bounds; bounds.Offset(offset); dc.DrawImage(art.Image, bounds);
        }
        dc.Pop(); dc.Pop();
    }
    private void PrepareArtwork()
    {
        if (preparing) return;
        preparing = true;
        int revision = generation; Color start = StartColor, end = EndColor;
        double actualWidth = ActualWidth, actualHeight = ActualHeight, density = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            try
            {
                var builder = new WatercolorLogo { StartColor = start, EndColor = end };
                builder.BuildLetters();
                double scale = Math.Min(actualWidth / (builder.width + 12), actualHeight / 112);
                double rasterScale = density * Math.Max(1, scale);
                var artwork = builder.letters.Select(letter => WelcomeArtCache.Create(letter, rasterScale)).ToArray();
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != revision) return;
                    letters.Clear(); letters.AddRange(builder.letters); cachedLetters.Clear(); cachedLetters.AddRange(artwork);
                    width = builder.width; cachedScale = rasterScale; CacheBuilds++; preparing = false; InvalidateVisual();
                }));
            }
            catch (Exception error)
            {
                EngineDiagnostics.Trace("Подготовка логотипа", error.Message);
                if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => { if (generation == revision) preparing = false; }));
            }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true, Name = "KNE watercolor preparation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private void BuildLetters()
    {
        var random = new Random(0x4B4E45); width = 0;
        for (int index = 0; index < BrandText.Length; index++)
        {
            var text = new FormattedText(BrandText[index].ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Georgia"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 84, Brushes.White, 1);
            Geometry glyph = text.BuildGeometry(new Point(width, 0)); width += text.WidthIncludingTrailingWhitespace;
            var group = new DrawingGroup();
            if (!glyph.Bounds.IsEmpty)
            {
                Rect bounds = glyph.Bounds; double mix = index / (double)(BrandText.Length - 1); Color color = Blend(StartColor, EndColor, mix);
                var pores = new GeometryGroup();
                for (int grain = 0; grain < 75; grain++) pores.Children.Add(new EllipseGeometry(new Point(bounds.X + random.NextDouble() * bounds.Width, bounds.Y + random.NextDouble() * bounds.Height), .12 + random.NextDouble() * .35, .15 + random.NextDouble() * .5));
                var pigment = new CombinedGeometry(GeometryCombineMode.Exclude, glyph, pores); pigment.Freeze();
                using var drawing = group.Open();
                var wash = new LinearGradientBrush(Blend(color, Colors.White, .16), color, new Point(0, 0), new Point(.8, 1));
                drawing.DrawGeometry(wash, new Pen(new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B)), .8), pigment);
                drawing.PushClip(pigment);
                for (int stain = 0; stain < 22; stain++)
                {
                    Color ink = Blend(color, stain % 3 == 0 ? Colors.White : EndColor, .1 + random.NextDouble() * .22); ink.A = (byte)random.Next(12, 54);
                    drawing.DrawEllipse(new SolidColorBrush(ink), null, new Point(bounds.X + random.NextDouble() * bounds.Width, bounds.Y + random.NextDouble() * bounds.Height), 2 + random.NextDouble() * 10, 2 + random.NextDouble() * 9);
                }
                drawing.Pop();
            }
            group.Freeze(); letters.Add(group);
        }
    }
    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R * (1 - amount) + b.R * amount), (byte)(a.G * (1 - amount) + b.G * amount), (byte)(a.B * (1 - amount) + b.B * amount));
}
