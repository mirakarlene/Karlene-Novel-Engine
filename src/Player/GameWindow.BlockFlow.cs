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

    // A clock tick never saves a new slot or steals input focus.
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
            await current.TickAsync(seconds, operation.Token);
            if (!closed && ReferenceEquals(current, runtime)) RenderOutput();
        }
        catch (OperationCanceledException) { if (!closed) StatusText.Text = "Фоновое выполнение остановлено"; }
        catch (Exception error)
        {
            if (!closed && ReferenceEquals(current, runtime)) ShowStartupError(error, "Параллельные ветки и таймеры");
        }
        finally
        {
            clockTickOperation = null; pumpingSceneTime = false;
            if (!closed) StopButton.IsEnabled = execution != null;
        }
    }
}
