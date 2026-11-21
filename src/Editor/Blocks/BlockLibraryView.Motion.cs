using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace KNE;

public partial class BlockLibraryView
{
    internal void SetLuaMotion(bool enabled)
    {
        LuaMotionCanvas.Children.Clear();
        LuaMotionCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) return;
        var geometry = Geometry.Parse(ProjectFileFormats.LuaMark);
        geometry.Freeze();
        for (int i = 0; i < 6; i++)
        {
            bool visualLua = i % 2 == 1;
            var mark = new Path
            {
                Data = geometry, Stretch = Stretch.Uniform, Width = 32 + i * 5, Height = 40 + i * 5,
                Fill = new SolidColorBrush(visualLua ? Color.FromRgb(46, 166, 102) : Color.FromRgb(64, 113, 208)),
                Opacity = .07, RenderTransform = new RotateTransform(visualLua ? 180 : 0), RenderTransformOrigin = new(.5, .5)
            };
            var translation = new TranslateTransform();
            var host = new Border { Child = mark, RenderTransform = translation };
            Canvas.SetTop(host, 44 + i * 92);
            LuaMotionCanvas.Children.Add(host);
            translation.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-90, Width + 90, TimeSpan.FromSeconds(39 + i * 4))
            {
                RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.Zero
            });
            translation.ApplyAnimationClock(TranslateTransform.YProperty, new DoubleAnimation(-6, 6, TimeSpan.FromSeconds(12))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }.CreateClock());
        }
    }
}
