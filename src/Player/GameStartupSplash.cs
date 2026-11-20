using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

internal static class GameStartupSplash
{
    internal static async Task ShowThenAsync(ProjectInfo project, Action showGame, CancellationToken token = default)
    {
        var options = project.Configuration.EntryPoints;
        Window? splash = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(options?.SplashImage))
            {
                try
                {
                    string path = PathSafety.ResolveRelative(project.Path, options.SplashImage);
                    if (!ProjectLifecycle.SupportsImage(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                        throw new InvalidDataException("Сплэш-скрин должен быть изображением до 32 МБ.");
                    double duration = ProjectLifecycle.Duration(options.SplashSeconds, ProjectLifecycle.DefaultSplashSeconds, 60);
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 1600;
                    image.UriSource = new Uri(path); image.EndInit();
                    Rect area = SystemParameters.WorkArea;
                    double scale = Math.Min(1, Math.Min(area.Width * .8 / image.Width, area.Height * .8 / image.Height));
                    splash = new Window
                    {
                        Content = new Image { Source = image, Stretch = Stretch.Uniform },
                        Width = Math.Max(1, image.Width * scale), Height = Math.Max(1, image.Height * scale),
                        WindowStartupLocation = WindowStartupLocation.CenterScreen, WindowStyle = WindowStyle.None,
                        ShowActivated = false, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black
                    };
                    splash.Show();
                    await Task.Delay(TimeSpan.FromSeconds(duration), token);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException)
                { EngineDiagnostics.Trace("Сплэш-скрин", error.Message); }
            }
            token.ThrowIfCancellationRequested();
            showGame();
        }
        finally { splash?.Close(); }
    }
}
