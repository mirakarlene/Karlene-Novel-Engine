using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private TaskCompletionSource? resumeTestPause;

    internal static double ReadPauseDuration(SystemCall call)
    {
        var duration = GameSystemServices.Arg(call, 0);
        if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out double seconds) ||
            !double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds || seconds < 0 && seconds != -1)
            throw new ArgumentException("Длительность паузы должна быть числом от 0 или равна −1.");
        return seconds;
    }

    private async Task PauseProjectAsync(SystemCall call, CancellationToken token)
    {
        double seconds = ReadPauseDuration(call);
        if (seconds == 0) return;
        bool allowEarlyResume = call.Arguments.GetArrayLength() > 1 && GameSystemServices.Arg(call, 1) switch
        {
            { ValueKind: JsonValueKind.True } => StandalonePlayer.RuntimeManifest is not { TestBuild: false },
            { ValueKind: JsonValueKind.False } => false,
            _ => throw new ArgumentException("Разморозка в тестовом билде: true или false.")
        };

        var root = (Grid)Content;
        var frame = SceneTransitionPresenter.Capture(root);
        var overlay = new Border
        {
            Background = frame == null ? Brushes.Transparent : new ImageBrush(frame),
            Focusable = true
        };
        Panel.SetZIndex(overlay, int.MaxValue);
        root.Children.Add(overlay);
        overlay.Focus();
        systemFrozen = true;
        UpdatePlayClock();
        PauseAudio();
        if (menuMusicPlaying) music.Pause();
        using var delayLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (allowEarlyResume) resumeTestPause = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var shortcut = resumeTestPause == null ? null : TryInstallTestPauseShortcut(resumeTestPause);
            var delay = WaitForPauseAsync(seconds, delayLifetime.Token);
            if (resumeTestPause is { } resumed && await Task.WhenAny(delay, resumed.Task) != delay)
            {
                token.ThrowIfCancellationRequested();
                return;
            }
            await delay;
        }
        finally
        {
            delayLifetime.Cancel();
            resumeTestPause = null;
            root.Children.Remove(overlay);
            systemFrozen = false;
            UpdatePlayClock();
            if (!closed)
            {
                if (menuMusicPlaying) music.Play();
                if (MenuOverlay.Visibility != Visibility.Visible) ResumeAudio();
            }
        }
    }

    private TestPauseShortcut? TryInstallTestPauseShortcut(TaskCompletionSource resumed)
    {
        try { return new TestPauseShortcut(new WindowInteropHelper(this).Handle, () => resumed.TrySetResult()); }
        catch (Win32Exception error)
        {
            QueueOutput("Не удалось перехватить Ctrl+Shift+Esc для тестовой разморозки: " + error.Message);
            return null;
        }
    }

    private static async Task WaitForPauseAsync(double seconds, CancellationToken token)
    {
        if (seconds == -1) await Task.Delay(Timeout.InfiniteTimeSpan, token);
        while (seconds > 0)
        {
            double interval = Math.Min(seconds, 3600);
            await Task.Delay(TimeSpan.FromSeconds(interval), token);
            seconds -= interval;
        }
    }

    private void HandleFrozenInput(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            resumeTestPause?.TrySetResult();
        scanInput?.Invoke("key");
        e.Handled = true;
    }
}
