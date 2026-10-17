using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KNE;

/// <summary>A visual copy of accessible dialogue, animated by Unicode text element rather than UTF-16 code unit.</summary>
public sealed class EffectTextPresenter : Canvas
{
    private readonly DispatcherTimer clock;
    private readonly Stopwatch elapsed = new();
    private readonly List<TextBlock> letters = [];
    private readonly List<Point> positions = [];
    private readonly List<Ellipse> drops = [];
    private readonly List<GlyphAnimation> animations = [];
    private readonly List<(string Text, Point Position, double Width)> layoutGlyphs = [];
    private ScrollViewer? scroll;
    private int firstVisible = -1, lastVisible = -1;
    private double lineHeight;
    public const int MaximumAnimatedGlyphs = 4096;
    public bool IsEffectSuppressedForCapacity { get; private set; }
    private string[] elements = [];
    private TextBlock? source;
    private TextEffectSettings? settings;
    private object? frameKey;
    private bool animate;
    private double measuredWidth = double.NaN;
    private LayoutSignature? layoutKey;
    private TimeSpan phase;
    public int GlyphCount => letters.Count;
    public bool IsAnimating => clock.IsEnabled;
    public string PlainText { get; private set; } = "";

    private sealed record LayoutSignature(string Text, string Font, double Size, FontWeight Weight,
        FontStyle Style, FontStretch Stretch, string Color, TextAlignment Alignment);

    private sealed class GlyphAnimation
    {
        public required string Original { get; init; }
        public TranslateTransform Translation { get; } = new();
        public RotateTransform Rotation { get; } = new();
        public ScaleTransform Scale { get; } = new();
        public TransformGroup Transform { get; } = new();
        public BlurEffect Blur { get; } = new();
        public DropShadowEffect Glitch { get; } = new() { BlurRadius = 0, ShadowDepth = 3, Color = Colors.Cyan, Opacity = .65 };
        public SolidColorBrush Color { get; } = new();
        public GlyphAnimation()
        {
            Transform.Children.Add(Scale); Transform.Children.Add(Rotation); Transform.Children.Add(Translation);
        }
    }

    public EffectTextPresenter()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = false;
        clock = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        clock.Tick += (_, _) =>
        {
            RenderAt(elapsed.Elapsed);
            if (settings is { Loop: false } && elapsed.Elapsed.TotalSeconds * settings.Speed >= 1) clock.Stop();
        };
        Loaded += (_, _) => { AttachViewport(); RefreshViewport(RenderSize.Height); UpdateClock(); };
        Unloaded += (_, _) => { clock.Stop(); elapsed.Stop(); if (scroll is not null) scroll.ScrollChanged -= ViewportChanged; scroll = null; };
        IsVisibleChanged += (_, _) => UpdateClock();
    }

    public void Present(TextBlock text, TextEffectSettings? effect, bool play, object? frameKey = null, bool preserveTextPhase = false)
    {
        bool changed = !ReferenceEquals(source, text) || settings != effect || !Equals(this.frameKey, frameKey) || !preserveTextPhase && PlainText != text.Text;
        source = text; settings = effect; animate = play; PlainText = text.Text; this.frameKey = frameKey;
        Visibility = effect is not null && text.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Opacity = Visibility == Visibility.Visible && !IsEffectSuppressedForCapacity ? 0 : 1;
        var key = new LayoutSignature(text.Text, text.FontFamily.Source, text.FontSize, text.FontWeight,
            text.FontStyle, text.FontStretch, text.Foreground.ToString(), text.TextAlignment);
        if (layoutKey != key)
        {
            layoutKey = key; measuredWidth = double.NaN; InvalidateMeasure();
        }
        if (changed) { phase = TimeSpan.Zero; elapsed.Restart(); }
        if (!play) { elapsed.Reset(); phase = TimeSpan.Zero; }
        RenderAt(phase); UpdateClock();
    }

    public void Stop()
    {
        animate = false; clock.Stop(); elapsed.Stop();
    }

    /// <summary>Starts the next session or replay with a fresh animation even when its first frame is identical.</summary>
    public void Reset()
    {
        Stop(); elapsed.Reset(); phase = TimeSpan.Zero; frameKey = null; settings = null;
        if (source is not null) source.Opacity = 1;
        Visibility = Visibility.Collapsed;
    }

    private void UpdateClock()
    {
        bool run = animate && !IsEffectSuppressedForCapacity && settings is not null && IsLoaded && IsVisible &&
            (settings.Loop || elapsed.Elapsed.TotalSeconds * settings.Speed < 1);
        if (run) { elapsed.Start(); clock.Start(); }
        else { clock.Stop(); elapsed.Stop(); }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double width = double.IsFinite(constraint.Width) ? Math.Max(1, constraint.Width) : Math.Max(1, source?.DesiredSize.Width ?? 1);
        if (source is not null && measuredWidth != width) BuildLayout(width);
        foreach (UIElement child in Children) child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0); // The plain TextBlock determines wrapping and scroll extent.
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        RefreshViewport(arrangeSize.Height);
        return base.ArrangeOverride(arrangeSize);
    }

    private void BuildLayout(double width)
    {
        if (source is null) return;
        measuredWidth = width; layoutGlyphs.Clear(); firstVisible = lastVisible = -1;
        elements = TextElements(PlainText);
        double x = 0, y = 0;
        var typeface = new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        lineHeight = new FormattedText("Mg", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, source.FontSize, source.Foreground, dpi).Height;
        var advances = new Dictionary<string, double>(StringComparer.Ordinal);
        double Advance(string element)
        {
            if (!advances.TryGetValue(element, out double value))
            {
                value = new FormattedText(element, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, source.FontSize, source.Foreground, dpi).WidthIncludingTrailingWhitespace;
                advances[element] = value;
            }
            return value;
        }
        var lines = new List<(int Start, int Count, double Width)>(); int start = 0;
        for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
        {
            string element = elements[elementIndex];
            if (element is "\n" or "\r" or "\r\n")
            {
                lines.Add((start, layoutGlyphs.Count - start, x)); start = layoutGlyphs.Count; x = 0; y += lineHeight; continue;
            }
            double advance = Advance(element);
            // Match normal word wrapping, while still splitting a word longer than the available line.
            if (x > 0 && !string.IsNullOrWhiteSpace(element) && elementIndex > 0 && string.IsNullOrWhiteSpace(elements[elementIndex - 1]))
            {
                double wordWidth = 0;
                for (int wordIndex = elementIndex; wordIndex < elements.Length && !string.IsNullOrWhiteSpace(elements[wordIndex]); wordIndex++)
                {
                    wordWidth += Advance(elements[wordIndex]);
                }
                if (wordWidth <= width && x + wordWidth > width)
                {
                    lines.Add((start, layoutGlyphs.Count - start, x)); start = layoutGlyphs.Count; x = 0; y += lineHeight;
                }
            }
            if (x > 0 && x + advance > width)
            {
                lines.Add((start, layoutGlyphs.Count - start, x)); start = layoutGlyphs.Count; x = 0; y += lineHeight;
            }
            layoutGlyphs.Add((element, new Point(x, y), advance));
            x += advance;
        }
        lines.Add((start, layoutGlyphs.Count - start, x));
        foreach (var line in lines)
        {
            double offset = source.TextAlignment switch { TextAlignment.Center => (width - line.Width) / 2, TextAlignment.Right => width - line.Width, _ => 0 };
            double extra = source.TextAlignment == TextAlignment.Justify && line.Count > 1 && line != lines[^1] ? (width - line.Width) / (line.Count - 1) : 0;
            for (int i = 0; i < line.Count; i++)
            {
                int index = line.Start + i; var glyph = layoutGlyphs[index];
                layoutGlyphs[index] = (glyph.Text, new Point(glyph.Position.X + offset + i * extra, glyph.Position.Y), glyph.Width);
            }
        }
        RefreshViewport(RenderSize.Height);
    }

    private void AttachViewport()
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ScrollViewer viewport) continue;
            scroll = viewport; viewport.ScrollChanged += ViewportChanged; break;
        }
    }

    private void ViewportChanged(object sender, ScrollChangedEventArgs e) => RefreshViewport(RenderSize.Height);

    private void RefreshViewport(double height)
    {
        if (source is null || layoutGlyphs.Count == 0) { Children.Clear(); letters.Clear(); positions.Clear(); drops.Clear(); animations.Clear(); return; }
        double top = 0, bottom = height > 0 ? height : 720;
        if (scroll is not null && scroll.ActualHeight > 0)
        {
            double origin = TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
            top = -origin; bottom = scroll.ActualHeight - origin;
        }
        int lower = 0, upper = layoutGlyphs.Count;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (layoutGlyphs[middle].Position.Y < top - lineHeight) lower = middle + 1; else upper = middle;
        }
        int first = lower, last = first;
        while (last < layoutGlyphs.Count && layoutGlyphs[last].Position.Y <= bottom + lineHeight) last++;
        if (firstVisible == first && lastVisible == last) return;
        firstVisible = first; lastVisible = last;
        Children.Clear(); letters.Clear(); positions.Clear(); drops.Clear(); animations.Clear();
        IsEffectSuppressedForCapacity = last - first > MaximumAnimatedGlyphs;
        source.Opacity = settings is not null && !IsEffectSuppressedForCapacity && PlainText.Length > 0 ? 0 : 1;
        if (IsEffectSuppressedForCapacity) { UpdateClock(); return; }
        for (int i = first; i < last; i++)
        {
            var glyph = layoutGlyphs[i];
            var animation = new GlyphAnimation { Original = glyph.Text };
            animations.Add(animation);
            var letter = new TextBlock
            {
                Text = glyph.Text, Tag = glyph.Text, FontFamily = source.FontFamily, FontSize = source.FontSize, FontWeight = source.FontWeight,
                FontStyle = source.FontStyle, FontStretch = source.FontStretch, Foreground = source.Foreground,
                RenderTransformOrigin = new Point(.5, .5), RenderTransform = animation.Transform, IsHitTestVisible = false
            };
            letter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            letters.Add(letter); positions.Add(glyph.Position); Children.Add(letter);
            SetLeft(letter, glyph.Position.X); SetTop(letter, glyph.Position.Y);
            var drop = new Ellipse { Width = 3, Height = 7, Fill = source.Foreground, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            drops.Add(drop); Children.Add(drop);
        }
        RenderAt(phase); UpdateClock();
    }

    public static string[] TextElements(string value)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result.ToArray();
    }

    /// <summary>Sets an exact animation phase; frozen previews use zero and never subscribe to a clock.</summary>
    public void RenderAt(TimeSpan time)
    {
        phase = time;
        if (settings is null || source is null) return;
        double cycles = Math.Max(0, time.TotalSeconds) * settings.Speed;
        bool ended = !settings.Loop && cycles >= 1;
        double cycle = ended ? 1 : cycles % 1;
        double t = cycle * Math.Tau;
        for (int i = 0; i < letters.Count; i++)
        {
            var glyph = letters[i]; int sequence = firstVisible + i; double angle = t + sequence * .7;
            var animation = animations[i];
            animation.Translation.X = animation.Translation.Y = 0; animation.Rotation.Angle = 0;
            animation.Scale.ScaleX = animation.Scale.ScaleY = 1;
            glyph.Opacity = 1; glyph.Effect = null; glyph.Foreground = source.Foreground;
            string original = animation.Original;
            glyph.Text = original;
            drops[i].Visibility = Visibility.Collapsed;
            if (ended && settings.Kind != "dissolve") continue;
            switch (settings.Kind)
            {
                case "shake": animation.Translation.X = Math.Sin(t * 19 + sequence * 13) * 2.5; animation.Translation.Y = Math.Cos(t * 23 + sequence * 7) * 2.5; break;
                case "wave": animation.Translation.Y = Math.Sin(angle) * 7; break;
                case "sway": animation.Rotation.Angle = Math.Sin(angle) * 12; break;
                case "bounce": animation.Translation.Y = -Math.Abs(Math.Sin(angle)) * 12; break;
                case "float": animation.Translation.X = Math.Sin(t * .5 + sequence) * 2; animation.Translation.Y = Math.Sin(t * (1 + sequence % 3 * .2) + sequence) * 5; break;
                case "explode":
                    double spread = Math.Pow(Math.Sin(t / 2), 4);
                    animation.Translation.X = (positions[i].X - measuredWidth / 2) * spread * .7; animation.Translation.Y = (sequence % 2 == 0 ? -1 : 1) * spread * 35; break;
                case "pulse": double pulse = 1 + Math.Sin(t) * .16; animation.Scale.ScaleX = animation.Scale.ScaleY = pulse; break;
                case "spin": animation.Rotation.Angle = t * 180 / Math.PI; break;
                case "squash": double squash = Math.Sin(angle) * .25; animation.Scale.ScaleX = 1 + squash; animation.Scale.ScaleY = 1 - squash; break;
                case "glitch":
                    double glitch = Math.Sin(Math.Floor(cycle * 30) * 71 + sequence * 17);
                    animation.Translation.X = Math.Round(glitch * 5); animation.Translation.Y = Math.Round(Math.Cos(glitch * 4) * 2);
                    animation.Glitch.Direction = sequence % 2 == 0 ? 0 : 180; glyph.Effect = animation.Glitch; break;
                case "blur": animation.Blur.Radius = 1 + (Math.Sin(t) + 1) * 2.5; glyph.Effect = animation.Blur; break;
                case "dissolve": glyph.Opacity = ended ? 0 : Math.Max(0, 1 - cycle); break;
                case "scramble":
                    if (cycle < .7 && (animate || time > TimeSpan.Zero) && !string.IsNullOrWhiteSpace(original)) glyph.Text = "@#$%&?!+*"[(int)(Math.Floor(cycle * 40) + sequence * 7) % 9].ToString(); break;
                case "rainbow": animation.Color.Color = Hue(cycle + sequence * .075); glyph.Foreground = animation.Color; break;
                case "color_cycle":
                    Color first = (Color)ColorConverter.ConvertFromString(settings.Color1), second = (Color)ColorConverter.ConvertFromString(settings.Color2);
                    double blend = (Math.Sin(t) + 1) / 2;
                    animation.Color.Color = Color.FromArgb(Mix(first.A, second.A, blend), Mix(first.R, second.R, blend), Mix(first.G, second.G, blend), Mix(first.B, second.B, blend)); glyph.Foreground = animation.Color; break;
                case "drip":
                    if (!string.IsNullOrWhiteSpace(original))
                    {
                        double progress = (cycle + sequence * .17) % 1;
                        var drop = drops[i]; drop.Visibility = Visibility.Visible; drop.Opacity = 1 - progress;
                        SetLeft(drop, positions[i].X + glyph.DesiredSize.Width / 2); SetTop(drop, positions[i].Y + source.FontSize * .8 + progress * 25);
                        drop.Fill = source.Foreground;
                    }
                    break;
            }
        }
    }

    private static byte Mix(byte first, byte second, double amount) => (byte)Math.Round(first + (second - first) * amount);
    private static Color Hue(double hue)
    {
        double h = (hue % 1) * 6; double x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch { 0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
