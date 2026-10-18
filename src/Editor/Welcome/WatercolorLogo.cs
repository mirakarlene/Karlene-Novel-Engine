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
    private double width, phase;
    internal double MotionPhase => phase;
    internal Vector OffsetFor(int index) => new(Math.Sin(phase * .7 + index * .8) * 1.4, Math.Sin(phase + index * .65) * 3);
    private static void Rebuild(DependencyObject view, DependencyPropertyChangedEventArgs args)
    { var logo = (WatercolorLogo)view; logo.letters.Clear(); logo.InvalidateVisual(); }
    protected override void Advance(double seconds) => phase = (phase + seconds * .45) % (Math.Tau * 10);
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (letters.Count == 0) BuildLetters();
        double scale = Math.Min(ActualWidth / (width + 12), ActualHeight / 112);
        dc.PushTransform(new TranslateTransform((ActualWidth - width * scale) / 2, (ActualHeight - 100 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        for (int i = 0; i < letters.Count; i++)
        {
            Vector offset = OffsetFor(i); dc.PushTransform(new TranslateTransform(offset.X, offset.Y)); dc.DrawDrawing(letters[i]); dc.Pop();
        }
        dc.Pop(); dc.Pop();
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
