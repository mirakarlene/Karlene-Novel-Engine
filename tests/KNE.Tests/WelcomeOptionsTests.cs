using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class WelcomeOptionsTests
{
    [Fact]
    public Task WelcomeObjects_FiveDifferentShapesKeepTheSameMotionRepulsionAndParticleCount_AndUnknownRestoresBubbles() => WorkspaceTabsTests.Sta(() =>
    {
        string[] ids = ["bubbles", "stars", "petals", "crystals", "fireflies"];
        Assert.Equal(ids, WelcomeBubbles.Objects.Select(option => option.Id));
        Assert.All(WelcomeBubbles.Objects, option => Assert.False(string.IsNullOrWhiteSpace(option.Label)));
        Assert.Equal(ids.Length, WelcomeBubbles.Objects.Select(option => option.Label).Distinct().Count());
        var control = new WelcomeBubbles(); var size = new Size(500, 300);
        control.Measure(size); control.Arrange(new Rect(size)); control.Step(0);
        var before = control.Field.Bubbles.Select(bubble => (bubble.X, bubble.Y, bubble.Radius)).ToArray();
        var renders = new List<byte[]>();
        foreach (string id in ids)
        {
            Assert.Equal(id, WelcomeBubbles.Normalize(id)); control.ObjectKind = id;
            byte[] pixels = Pixels(control, size); Assert.True(pixels.Where((_, index) => index % 4 == 3).Count(alpha => alpha > 0) > 100);
            Assert.DoesNotContain(renders, previous => previous.SequenceEqual(pixels)); renders.Add(pixels);
            Assert.Equal(before, control.Field.Bubbles.Select(bubble => (bubble.X, bubble.Y, bubble.Radius)));
            Assert.False(control.IsHitTestVisible);
        }
        foreach (string? unknown in new[] { null, "", "UNKNOWN" })
        {
            Assert.Equal("bubbles", WelcomeBubbles.Normalize(unknown)); control.ObjectKind = unknown!;
            Assert.Equal(renders[0], Pixels(control, size));
        }
        var plain = new WelcomeBubbles(); plain.Measure(size); plain.Arrange(new Rect(size)); plain.Step(0);
        var pointer = new Point(before[0].X - 10, before[0].Y); control.SetPointer(pointer);
        control.Step(.05); plain.Step(.05);
        Assert.Equal(before.Length, control.Field.Bubbles.Count);
        Assert.True(control.Field.Bubbles[0].X > plain.Field.Bubbles[0].X);
        Assert.NotEqual(before[0].Y, control.Field.Bubbles[0].Y);
        return Task.CompletedTask;
    });

    [Fact]
    public Task NewThemes_AreDistinctCompleteDynamicPalettesWithReadableText() => WorkspaceTabsTests.Sta(() =>
    {
        string[] additions = ["lavender", "midnight", "cherry", "sand", "aurora"];
        var owner = new Window(); var field = new TextBox(); owner.Content = field;
        var backgrounds = new HashSet<Color>();
        try
        {
            foreach (string id in additions)
            {
                Assert.Contains(EditorTheme.Themes, option => option.Id == id && !string.IsNullOrWhiteSpace(option.Label));
                EditorTheme.Apply(owner, id); field.Measure(new Size(600, 300)); field.Arrange(new Rect(0, 0, 600, 300)); field.UpdateLayout();
                var foreground = BrushColor(field.Foreground); var surface = BrushColor(field.Background);
                Assert.Equal(BrushColor(owner.FindResource("Editor.Surface")), surface);
                Assert.Equal(BrushColor(owner.FindResource("Editor.Text")), foreground);
                Assert.True(Contrast(foreground, surface) >= 4.5, id + " text must remain readable.");
                var gradient = Assert.IsType<LinearGradientBrush>(owner.FindResource("Editor.WelcomeBackground"));
                Assert.True(backgrounds.Add(gradient.GradientStops[0].Color), id + " must add a distinct welcome palette.");
            }
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    internal static byte[] Pixels(FrameworkElement view, Size size)
    {
        view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var bytes = new byte[(int)size.Width * (int)size.Height * 4]; image.CopyPixels(bytes, (int)size.Width * 4, 0); return bytes;
    }
    internal static Color BrushColor(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    internal static double Contrast(Color first, Color second)
    {
        static double L(Color color)
        {
            static double C(byte value) { double c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * C(color.R) + .7152 * C(color.G) + .0722 * C(color.B);
        }
        return (Math.Max(L(first), L(second)) + .05) / (Math.Min(L(first), L(second)) + .05);
    }
}
