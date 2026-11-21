using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace KNE;

public partial class BlockLibraryView
{
    private DispatcherTimer? luaMotionTimer;

    internal void SetLuaMotion(bool enabled)
    {
        luaMotionTimer?.Stop();
        luaMotionTimer = null;
        LuaMotionCanvas.Children.Clear();
        LuaMotionCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) return;

        var geometry = Geometry.Parse(ProjectFileFormats.LuaMark);
        geometry.Freeze();
        var lua = MotionMark(geometry, Color.FromRgb(64, 113, 208));
        var visualLua = MotionMark(geometry, Color.FromRgb(46, 166, 102));
        var marks = new (TranslateTransform Position, double Phase, double Height, double Speed)[4];
        for (int index = 0; index < marks.Length; index++)
        {
            bool inverted = index % 2 == 1;
            var position = new TranslateTransform();
            var image = new Image
            {
                Source = inverted ? visualLua : lua, Width = 32 + Random.Shared.Next(20), Height = 50,
                Opacity = .055, RenderTransformOrigin = new(.5, .5),
                RenderTransform = new TransformGroup { Children = { new RotateTransform(inverted ? 180 : 0), position } }
            };
            LuaMotionCanvas.Children.Add(image);
            marks[index] = (position, Random.Shared.NextDouble(), (index + .3 + Random.Shared.NextDouble() * .4) / marks.Length,
                (index < 2 ? 1 : -1) / (65 + Random.Shared.NextDouble() * 35));
        }

        var elapsed = Stopwatch.StartNew();
        luaMotionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        luaMotionTimer.Tick += (_, _) =>
        {
            double seconds = elapsed.Elapsed.TotalSeconds;
            foreach (var (position, phase, height, speed) in marks)
            {
                double progress = phase + seconds * speed;
                position.X = -55 + (progress - Math.Floor(progress)) * (LuaMotionCanvas.ActualWidth + 110);
                position.Y = LuaMotionCanvas.ActualHeight * height + 18 * Math.Sin(seconds * .17 + phase * 12);
            }
        };
        luaMotionTimer.Start();
    }

    private static DrawingImage MotionMark(Geometry geometry, Color color)
    {
        var image = new DrawingImage(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
        image.Freeze();
        return image;
    }

    private void LuaMotionCanvas_Unloaded(object sender, RoutedEventArgs e) => SetLuaMotion(false);
}
