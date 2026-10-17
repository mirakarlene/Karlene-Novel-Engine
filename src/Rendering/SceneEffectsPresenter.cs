using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KNE;

/// <summary>Starts animations only when their command version changes, preserving clocks across frame updates.</summary>
internal sealed class SceneEffectsPresenter
{
    private SceneVisuals? previous;
    private readonly FrameworkElement cameraLayer;
    private readonly FrameworkElement background;
    private readonly FrameworkElement character;
    private readonly Border flash;
    private readonly TextBlock title;
    private readonly ScaleTransform cameraScale = new(1, 1);
    private readonly TranslateTransform cameraPan = new();
    private readonly TranslateTransform shake = new();
    private readonly Dictionary<FrameworkElement, (TranslateTransform Move, ScaleTransform Scale, RotateTransform Rotate)> transforms = [];

    public SceneEffectsPresenter(FrameworkElement cameraLayer, FrameworkElement background, FrameworkElement character, Border flash, TextBlock title)
    {
        this.cameraLayer = cameraLayer; this.background = background; this.character = character; this.flash = flash; this.title = title;
        var cameraTransform = new TransformGroup(); cameraTransform.Children.Add(cameraScale); cameraTransform.Children.Add(cameraPan); cameraTransform.Children.Add(shake);
        cameraLayer.RenderTransform = cameraTransform; cameraLayer.RenderTransformOrigin = new(.5, .5);
        foreach (var target in new[] { background, character })
        {
            var move = new TranslateTransform(); var scale = new ScaleTransform(1, 1); var rotate = new RotateTransform();
            var group = new TransformGroup(); group.Children.Add(scale); group.Children.Add(rotate); group.Children.Add(move);
            target.RenderTransform = group; target.RenderTransformOrigin = new(.5, .5); transforms[target] = (move, scale, rotate);
        }
    }

    public void Apply(SceneVisuals state, bool animate)
    {
        title.Text = state.Title; title.Visibility = state.Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (previous?.BackgroundMotion != state.BackgroundMotion) Move(background, state.BackgroundMotion, animate);
        if (previous?.CharacterMotion != state.CharacterMotion) Move(character, state.CharacterMotion, animate);
        if (previous?.BackgroundAnimation != state.BackgroundAnimation) Animate(background, state.BackgroundAnimation, animate);
        if (previous?.CharacterAnimation != state.CharacterAnimation) Animate(character, state.CharacterAnimation, animate);
        if (previous?.Camera != state.Camera)
        {
            Tween(cameraScale, ScaleTransform.ScaleXProperty, state.Camera.Zoom, state.Camera.Duration, animate);
            Tween(cameraScale, ScaleTransform.ScaleYProperty, state.Camera.Zoom, state.Camera.Duration, animate);
            Tween(cameraPan, TranslateTransform.XProperty, state.Camera.X, state.Camera.Duration, animate);
            Tween(cameraPan, TranslateTransform.YProperty, state.Camera.Y, state.Camera.Duration, animate);
        }
        if (previous?.Shake != state.Shake)
        {
            shake.BeginAnimation(TranslateTransform.XProperty, null); shake.X = 0;
            if (animate && state.Shake.Duration > 0 && state.Shake.Amount > 0)
            {
                var timeline = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(state.Shake.Duration) };
                for (int i = 0; i <= 12; i++) timeline.KeyFrames.Add(new LinearDoubleKeyFrame(i == 12 ? 0 : state.Shake.Amount * (i % 2 == 0 ? -1 : 1), KeyTime.FromPercent(i / 12d)));
                shake.BeginAnimation(TranslateTransform.XProperty, timeline);
            }
        }
        if (previous?.Flash != state.Flash)
        {
            flash.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(state.Flash.Color));
            flash.BeginAnimation(UIElement.OpacityProperty, null); flash.Opacity = 0;
            if (animate && state.Flash.Duration > 0 && state.Flash.Version > 0)
                flash.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(state.Flash.Duration)));
        }
        previous = state;
    }

    private void Move(FrameworkElement target, VisualMotion motion, bool animate)
    {
        var move = transforms[target].Move;
        Tween(move, TranslateTransform.XProperty, motion.X, motion.Duration, animate);
        Tween(move, TranslateTransform.YProperty, motion.Y, motion.Duration, animate);
    }
    private void Animate(FrameworkElement target, VisualAnimation effect, bool animate)
    {
        var (_, scale, rotate) = transforms[target];
        target.BeginAnimation(UIElement.OpacityProperty, null); scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        target.Opacity = effect.Kind == "fade_out" ? 0 : 1; scale.ScaleX = scale.ScaleY = 1; rotate.Angle = effect.Kind == "rotate" ? effect.Amount : 0;
        if (!animate || effect.Duration <= 0) return;
        var duration = TimeSpan.FromSeconds(effect.Duration);
        switch (effect.Kind)
        {
            case "fade_in": target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration)); break;
            case "fade_out": target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration)); break;
            case "rotate": rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, effect.Amount, duration)); break;
            case "pulse":
                var pulse = new DoubleAnimation(1, Math.Clamp(effect.Amount, .05, 10), TimeSpan.FromSeconds(effect.Duration / 2)) { AutoReverse = true };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse); scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse); break;
        }
    }
    private static void Tween(Animatable target, DependencyProperty property, double destination, double seconds, bool animate)
    {
        double from = (double)target.GetValue(property); target.BeginAnimation(property, null); target.SetValue(property, destination);
        if (animate && seconds > 0) target.BeginAnimation(property, new DoubleAnimation(from, destination, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }
    public void Stop()
    {
        previous = null;
        foreach (var target in new[] { background, character }) { Move(target, new(), false); Animate(target, new(), false); }
        foreach (var item in new[] { cameraPan, shake }) { item.BeginAnimation(TranslateTransform.XProperty, null); item.BeginAnimation(TranslateTransform.YProperty, null); item.X = item.Y = 0; }
        cameraScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); cameraScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); cameraScale.ScaleX = cameraScale.ScaleY = 1;
        flash.BeginAnimation(UIElement.OpacityProperty, null); flash.Opacity = 0;
    }
}
