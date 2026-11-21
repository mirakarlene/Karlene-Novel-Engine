using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private TaskCompletionSource? videoPlayback;
    private (Key Key, ModifierKeys Modifiers)? skipVideoWith;
    internal static (string Path, Rect? Region, (Key Key, ModifierKeys Modifiers)? Skip) ReadVideoRequest(string projectPath, SystemCall call)
    {
        string path = PathSafety.ResolveRelative(projectPath, GameSystemServices.String(call, 0));
        if (!File.Exists(path)) throw new FileNotFoundException("Видеофайл не найден.", path);
        int count = call.Arguments.GetArrayLength();
        if (count is not (1 or 2 or 5 or 6))
            throw new ArgumentException("Укажите путь к видео, необязательную область (X, Y, ширина, высота) и настройку пропуска.");
        (Key, ModifierKeys)? skip = count is 2 or 6 ? GameSystemServices.Arg(call, count - 1) switch
        {
            { ValueKind: JsonValueKind.False } => null,
            { ValueKind: JsonValueKind.True } => (Key.Escape, ModifierKeys.None),
            { ValueKind: JsonValueKind.String } value => VideoSkipShortcut.Parse(value.GetString()!),
            _ => throw new ArgumentException("Пропуск видео: false, true или клавиша либо сочетание в строке.")
        } : (Key.Escape, ModifierKeys.None);
        if (count <= 2) return (path, null, skip);

        double x = ReadVideoNumber(call, 1), y = ReadVideoNumber(call, 2);
        double width = ReadVideoNumber(call, 3), height = ReadVideoNumber(call, 4);
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1280 || y + height > 720)
            throw new ArgumentException("Область видео должна целиком помещаться на экране 1280×720.");
        return (path, new Rect(x, y, width, height), skip);
    }

    private static double ReadVideoNumber(SystemCall call, int index)
    {
        var value = GameSystemServices.Arg(call, index);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number))
            throw new ArgumentException("Координаты и размеры области видео должны быть конечными числами.");
        return number;
    }

    private async Task PlayVideoAsync(SystemCall call, CancellationToken token)
    {
        var (path, area, skip) = ReadVideoRequest(project.Path, call);
        var media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = preferences.SoundVolume
        };
        FrameworkElement surface = new Border { Background = Brushes.Black, Child = media, ClipToBounds = true };
        if (area is { } rect)
        {
            var canvas = new Canvas { Width = 1280, Height = 720, ClipToBounds = true };
            surface.Width = rect.Width;
            surface.Height = rect.Height;
            Canvas.SetLeft(surface, rect.X);
            Canvas.SetTop(surface, rect.Y);
            canvas.Children.Add(surface);
            surface = new Viewbox { Child = canvas };
        }
        var overlay = new Border { Child = surface, Background = Brushes.Transparent, Focusable = true };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        media.MediaEnded += (_, _) => completed.TrySetResult();
        media.MediaOpened += (_, _) =>
        {
            if (!media.HasVideo) completed.TrySetException(new InvalidDataException("В файле нет видеодорожки: " + Path.GetFileName(path)));
        };
        media.MediaFailed += (_, e) => completed.TrySetException(new IOException(
            "Windows не смогла воспроизвести видео «" + Path.GetFileName(path) + "». Проверьте файл и наличие кодека.", e.ErrorException));
        var root = (Grid)Content;
        Panel.SetZIndex(overlay, 9);
        root.Children.Add(overlay);
        videoPlayback = completed;
        skipVideoWith = skip;
        try
        {
            overlay.Focus();
            media.Source = new Uri(path);
            media.Play();
            await completed.Task.WaitAsync(token);
        }
        finally
        {
            videoPlayback = null;
            skipVideoWith = null;
            media.Close();
            root.Children.Remove(overlay);
        }
    }

    private bool HandleVideoInput(KeyEventArgs e)
    {
        if (videoPlayback is not { } playing) return false;
        if (skipVideoWith == (e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) playing.TrySetResult();
        e.Handled = true;
        return true;
    }
}
