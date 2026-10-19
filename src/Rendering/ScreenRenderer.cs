using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Shared vector element rendering for the appearance editor and the game window.</summary>
public static class ScreenRenderer
{
    public static FrameworkElement Create(ScreenElement element, GameTheme theme, string root, Func<string, string> resolve, Func<string, BitmapSource>? loadImage = null)
    {
        string label = resolve(element.Text);
        Brush TextBrush() => new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextColor));
        FrameworkElement view = element.Type switch
        {
            "text" => new TextBlock { Text = label, Foreground = TextBrush(), TextWrapping = TextWrapping.Wrap, FontSize = element.FontSize, VerticalAlignment = VerticalAlignment.Center },
            "panel" => new Border { Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonColor)), CornerRadius = new CornerRadius(16) },
            "toggle" => new CheckBox { Content = label, Foreground = TextBrush(), FontSize = element.FontSize, VerticalContentAlignment = VerticalAlignment.Center },
            "slider" => SliderView(label, TextBrush(), element.FontSize),
            "image" => ImageView(root, element.Image, loadImage),
            "save_list" => new ListBox { ItemsSource = new[] { "Ручной слот · текущая реплика", "Автосохранение · дата и время" }, FontSize = 18 },
            "save_name" => new TextBox { Text = "Имя ручного слота", Padding = new Thickness(8), FontSize = 18 },
            _ => new Button { Content = label, FontSize = element.FontSize, Padding = new Thickness(12, 6, 12, 6), HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.ButtonColor)), Foreground = TextBrush(),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentColor)) }
        };
        view.Tag = element;
        view.Width = element.Width; view.Height = element.Height;
        view.FontFamilyIfControl(theme.FontFamily);
        Canvas.SetLeft(view, element.X); Canvas.SetTop(view, element.Y);
        return view;
    }

    private static FrameworkElement SliderView(string text, Brush brush, double fontSize)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = text, Foreground = brush, FontSize = fontSize });
        stack.Children.Add(new Slider { Minimum = 0, Maximum = 1, Value = 0.8, Margin = new Thickness(0, 8, 0, 0) });
        return stack;
    }
    private static Image ImageView(string root, string? relative, Func<string, BitmapSource>? loadImage)
    {
        var view = new Image { Stretch = Stretch.UniformToFill };
        if (!string.IsNullOrWhiteSpace(relative))
        {
            string path = ResourcePaths.Resolve(root, relative);
            if (loadImage != null) { view.Source = loadImage(path); return view; }
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); view.Source = image;
        }
        return view;
    }
    private static void FontFamilyIfControl(this FrameworkElement view, string family)
    {
        if (view is Control control) control.FontFamily = new FontFamily(family);
        if (view is TextBlock text) text.FontFamily = new FontFamily(family);
    }

    public static void Animate(FrameworkElement view, ScreenElement element, bool enabled)
    {
        var transform = new TranslateTransform(); view.RenderTransform = transform;
        if (!enabled || (element.MotionX == 0 && element.MotionY == 0)) return;
        transform.X = element.MotionX; transform.Y = element.MotionY;
        DoubleAnimation Motion(double distance) => new(distance, 0, TimeSpan.FromSeconds(element.Duration))
        {
            BeginTime = TimeSpan.FromSeconds(element.Delay + element.MotionOrder * 0.12),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            AutoReverse = element.Loop, RepeatBehavior = element.Loop ? RepeatBehavior.Forever : new RepeatBehavior(1)
        };
        transform.BeginAnimation(TranslateTransform.XProperty, Motion(element.MotionX));
        transform.BeginAnimation(TranslateTransform.YProperty, Motion(element.MotionY));
    }
}
