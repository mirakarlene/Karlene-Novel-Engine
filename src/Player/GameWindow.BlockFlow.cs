namespace KNE;

public partial class GameWindow
{
    private readonly CancellationTokenSource blockFlowLifetime = new();
    private bool pumpingSceneTime;
    private bool clockSuspended;
    private CancellationTokenSource? clockTickOperation;
    private long lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();

    private async void PlayerClock_Tick(object? sender, EventArgs e)
    {
        RenderOutput();
        if (pumpingSceneTime) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(lastSceneTick, now).TotalSeconds, .001, 60);
        lastSceneTick = now;
        await PumpSceneTimeAsync(seconds);
    }

    // A clock tick only writes an explicitly requested scene-entry checkpoint; it never steals focus.
    private async Task PumpSceneTimeAsync(double seconds = .1)
    {
        if (pumpingSceneTime || clockSuspended || closed || startupFailed || !ready || execution != null ||
            MenuOverlay.Visibility == System.Windows.Visibility.Visible || !runtime.Story.NeedsTick) return;
        pumpingSceneTime = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(blockFlowLifetime.Token);
        clockTickOperation = operation; StopButton.IsEnabled = true;
        var current = runtime;
        try
        {
            var previous = current.Visuals.Scene;
            await current.TickAsync(seconds, operation.Token);
            var image = NeedsSceneTransition(previous) ? SceneTransitionPresenter.Capture(SceneArea) : null;
            using var cover = image == null ? null : SceneTransitionPresenter.Hold(SceneArea, image);
            if (!closed && ReferenceEquals(current, runtime))
            {
                try { await SaveSceneEntryAsync(operation.Token); }
                catch (Exception error) when (error is not OperationCanceledException && GameErrorReport.TryGet(error) == null) { Notify("Точка входа не сохранена: " + error.Message); }
                cover?.Dispose();
                PresentSceneOutput();
                if (image != null && runtime.Visuals.Scene is { } scene && SceneChanged(previous))
                    await SceneTransitionPresenter.PlayAsync(this, project, SceneArea, image, previous?.Settings.ExitTransition ?? new(), scene.Exited ? new() : scene.Settings.EntryTransition, operation.Token);
            }
        }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "Фоновое выполнение остановлено"; }
        catch (Exception error)
        {
            if (!closed && ReferenceEquals(current, runtime)) ShowStartupError(error, "Параллельные ветки и таймеры");
        }
        finally
        {
            lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
            clockTickOperation = null; pumpingSceneTime = false;
            if (!closed) StopButton.IsEnabled = execution != null;
        }
    }
}
