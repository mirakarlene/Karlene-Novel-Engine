using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KNE;

public partial class GameWindow
{
    private async Task PlayStartupSplashAsync(CancellationToken token)
    {
        var options = project.Configuration.EntryPoints;
        if (preview || string.IsNullOrWhiteSpace(options?.SplashImage)) return;
        try
        {
            string path = PathSafety.ResolveRelative(project.Path, options.SplashImage);
            if (!ProjectLifecycle.SupportsImage(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Сплэш-скрин должен быть изображением до 32 МБ.");
            double duration = ProjectLifecycle.Duration(options.SplashSeconds, ProjectLifecycle.DefaultSplashSeconds, 60);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1600;
            image.UriSource = new Uri(path);
            image.EndInit();
            StartupSplashImage.Source = image;
            StartupSplash.Visibility = Visibility.Visible;
            await Task.Delay(TimeSpan.FromSeconds(duration), token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            EngineDiagnostics.Trace("Сплэш-скрин", error.Message);
        }
        finally
        {
            StartupSplash.Visibility = Visibility.Collapsed;
            StartupSplashImage.Source = null;
        }
    }
}
