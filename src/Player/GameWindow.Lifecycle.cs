using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private bool preMenuActive, preMenuFinishing, exitRequested, exitApproved;
    private long lastProjectInput = Stopwatch.GetTimestamp();
    private Point lastProjectPointer = new(double.NaN, double.NaN);
    private bool idleEventSent;
    internal Func<bool>? ConfirmProjectExit { get; set; }

    private void InitializeProjectEvents()
    {
        Closing += Window_ProjectClosing;
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler((_, _) => RecordProjectActivity()), handledEventsToo: true);
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => RecordProjectActivity()), handledEventsToo: true);
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => RecordProjectActivity()), handledEventsToo: true);
        PreviewMouseMove += (_, args) =>
        {
            Point position = args.GetPosition(this);
            if (position == lastProjectPointer) return;
            lastProjectPointer = position;
            RecordProjectActivity();
        };
    }

    private void RecordProjectActivity()
    {
        lastProjectInput = Stopwatch.GetTimestamp();
        idleEventSent = false;
    }

    private bool HasProjectEvent(string kind) => !preview &&
        !string.IsNullOrWhiteSpace(ProjectLifecycle.Script(project.Configuration.EntryPoints, kind));

    private async Task InvokeProjectEventAsync(string kind, string? value, CancellationToken token)
    {
        if (HasProjectEvent(kind) && runtime is ProcessGameSession session)
            await session.RunProjectEventAsync(new(kind, value), token);
    }

    private void RunMainMenuEvent()
    {
        if (!loaded || ready || returningToMenu || !HasProjectEvent("menu")) return;
        var session = runtime;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await systemInitialization.WaitAsync(blockFlowLifetime.Token); }
            catch (Exception error) { if (!closed) QueueOutput("Подготовка главного меню: " + error.Message); return; }
            if (closed || startupFailed || !ReferenceEquals(session, runtime) || ready || MenuOverlay.Visibility != Visibility.Visible) return;
            await ExecuteAsync(() => InvokeProjectEventAsync("menu", null, execution!.Token), operation: "Скрипт главного меню");
        }));
    }

    private async Task RunIdleEventAsync()
    {
        if (idleEventSent || !loaded || closed || startupFailed || preMenuActive || systemFrozen || !IsActive || execution != null ||
            exitRequested || !HasProjectEvent("idle") || !ready && MenuOverlay.Visibility != Visibility.Visible) return;
        double delay;
        try { delay = ProjectLifecycle.Duration(project.Configuration.EntryPoints?.IdleSeconds, ProjectLifecycle.DefaultIdleSeconds, 86400); }
        catch (Exception error) { idleEventSent = true; QueueOutput(error.Message); return; }
        if (Stopwatch.GetElapsedTime(lastProjectInput).TotalSeconds < delay) return;
        idleEventSent = true;
        await ExecuteAsync(() => InvokeProjectEventAsync("idle", null, execution!.Token), operation: "Скрипт при AFK");
    }

    private async Task StartPreMenuAsync()
    {
        preMenuActive = true;
        await ExecuteAsync(async () =>
        {
            var candidate = CreateGameSession();
            try { await candidate.StartPreMenuAsync(execution!.Token); }
            catch { await candidate.DisposeAsync(); throw; }
            await ReplaceSessionAsync(candidate);
        }, starting: true, operation: "Сцена до главного меню");
    }

    private bool HandlePreMenuEnd(StoryFrame frame)
    {
        if (!preMenuActive) return false;
        if (frame.Kind == "completed" && !preMenuFinishing)
        {
            preMenuFinishing = true;
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (closed || startupFailed) return;
                try
                {
                    ready = false;
                    await runtime.DisposeAsync();
                    if (closed) return;
                    runtime = CreateGameSession();
                    preMenuActive = false;
                    CloseAudio();
                    RecordProjectActivity();
                    ShowMenu();
                }
                catch (Exception error) { if (!closed) ShowStartupError(error, "Завершение сцены до главного меню"); }
            }));
        }
        return true;
    }

    private async Task StoreSaveAsync(string slot, CancellationToken token)
    {
        await saves.SaveAsync(slot, await CheckpointAsync(token), token);
        await InvokeProjectEventAsync("save", slot, token);
    }

    private async void Window_ProjectClosing(object? sender, CancelEventArgs e)
    {
        if (exitApproved || !loaded || startupFailed || !HasProjectEvent("exit")) return;
        e.Cancel = true;
        if (exitRequested) return;
        exitRequested = true;
        // WPF must finish dispatching the original Closing event before another Close call.
        await System.Windows.Threading.Dispatcher.Yield();
        try
        {
            await InvokeProjectEventAsync("exit", null, blockFlowLifetime.Token);
        }
        catch (OperationCanceledException) when (closed) { return; }
        catch (Exception error)
        {
            if (!closed) QueueOutput("Скрипт выхода: " + error.Message);
        }
        if (closed) return;
        bool confirmed;
        try { confirmed = ConfirmProjectExit?.Invoke() ?? await ConfirmInGameAsync("Подтвердите выход", "Выйти из игры?"); }
        finally { exitRequested = false; }
        if (!confirmed) return;
        if (closed) return;
        exitApproved = true;
        Close();
    }

    private async Task PresentProjectErrorAsync(GameErrorReport report)
    {
        try
        {
            await using var fallback = new ProcessGameSession(project, QueueOutput, executionLimit: TimeSpan.FromSeconds(4), pluginsEnabled: false);
            string? text = await fallback.RunProjectEventAsync(new("error", report.Message), blockFlowLifetime.Token);
            if (!closed && ReferenceEquals(currentErrorReport, report) && text != null) StartupErrorMessage.Text = text;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { QueueOutput("Скрипт при ошибке: " + error.Message); }
        catch (OperationCanceledException) { }
        if (!closed && ReferenceEquals(currentErrorReport, report)) ShowProjectErrorOverlay();
    }

    private void ShowProjectErrorOverlay()
    {
        StartupErrorOverlay.Visibility = Visibility.Visible;
        StartupErrorExitButton.Focus();
    }
}
