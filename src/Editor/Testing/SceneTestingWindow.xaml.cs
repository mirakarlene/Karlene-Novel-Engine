using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace KNE;

public partial class SceneTestingWindow : Window
{
    private readonly ProjectInfo project;
    private readonly EditorSelection selection;
    private readonly bool sceneMode;
    private readonly CancellationTokenSource lifetime = new();
    private SceneTestSession? scene;
    private EditorPreview? frame;
    private bool busy, started, closed;
    private bool snapshotReady;
    private readonly DispatcherTimer sceneClock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
    public SceneTestingWindow(ProjectInfo project, EditorSelection selection, bool sceneTest)
    {
        InitializeComponent(); this.project = project; this.selection = selection; sceneMode = sceneTest;
        Title = (sceneMode ? "Тестировать сцену · " : "Предпросмотр фрейма · ") + project.Title;
        Width = project.Engine.Window.Width; Height = project.Engine.Window.Height + 70;
        SceneSurface.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, StoryFrame.Empty, false, false);
        NextFrameButton.Visibility = sceneMode ? Visibility.Visible : Visibility.Collapsed;
        SceneSurface.MouseLeftButtonUp += async (_, e) =>
        {
            if (sceneMode && scene?.Story.CanAdvance == true && e.OriginalSource is not Button)
            { e.Handled = true; await ExecuteAsync(() => scene.AdvanceAsync(lifetime.Token)); }
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Space && e.OriginalSource is not TextBox && sceneMode && scene?.Story.CanAdvance == true)
            { e.Handled = true; await ExecuteAsync(() => scene.AdvanceAsync(lifetime.Token)); }
        };
        sceneClock.Tick += async (_, _) =>
        {
            if (busy || closed) return;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double seconds = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(lastSceneTick, now).TotalSeconds, .001, 60);
            lastSceneTick = now;
            if (sceneMode && scene?.Story.NeedsTick == true)
                await ExecuteAsync(() => scene.TickAsync(seconds, lifetime.Token));
        };
    }
    public Task InitializeAsync() => ExecuteAsync(async () =>
    {
        if (started) return; started = true;
        if (sceneMode) { scene = new SceneTestSession(project, selection); await scene.RestartAsync(lifetime.Token); lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp(); sceneClock.Start(); }
        else { frame = new EditorPreview(project, _ => { }); await frame.SeekAsync(selection with { FromMain = false }, lifetime.Token); await SeekTextFrameAsync(); }
    });
    private async void Testing_Loaded(object sender, RoutedEventArgs e) => await InitializeAsync();
    private async void Testing_Closed(object? sender, EventArgs e)
    {
        closed = true; sceneClock.Stop(); lifetime.Cancel(); SceneSurface.Stop();
        if (scene != null) await scene.DisposeAsync(); if (frame != null) await frame.DisposeAsync();
        lifetime.Dispose();
    }
    private async Task SeekTextFrameAsync()
    {
        if (frame?.Reached != true) return;
        for (int i = 0; frame.Story.Kind is "preview" or "wait" && i < 2000; i++) await frame.StepAsync(lifetime.Token);
        if (frame.Story.Kind is "preview" or "wait") throw new InvalidOperationException("Фрейм не найден после 2000 команд. Проверьте цикл в сцене.");
        snapshotReady = true;
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || closed) return; busy = true; NextFrameButton.IsEnabled = false; SceneSurface.IsHitTestVisible = false;
        BackFrameButton.IsEnabled = RestartSceneButton.IsEnabled = false;
        try { await action(); if (!closed) Render(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!closed) { SceneSurface.ResetPresentation(); TestingErrorText.Text = GameErrorReport.Create(project, error, "Тестирование сценария").ToDisplayText(); TestingErrorOverlay.Visibility = Visibility.Visible; }
        }
        finally { busy = false; if (!closed) { SceneSurface.IsHitTestVisible = true; RestartSceneButton.IsEnabled = true; } }
    }
    private void Render()
    {
        if (sceneMode && scene != null)
        {
            if (!scene.Completed) SceneSurface.DisplayScene(project, scene.Window, scene.Visuals, scene.Story, true, true);
            SceneEndOverlay.Visibility = scene.Completed ? Visibility.Visible : Visibility.Collapsed;
            BackFrameButton.IsEnabled = scene.CanGoBack;
            NextFrameButton.IsEnabled = scene.Story.CanAdvance;
            TestingStatus.Text = "Изолированный тест сцены · " + (selection.Function ?? "сценарий") + DescribeIdentity(scene.Story);
        }
        else if (frame != null)
        {
            SceneSurface.DisplayScene(project, frame.Window, frame.Visuals, frame.Story, false, (frame.NeedsChoice || frame.NeedsInput) && !snapshotReady);
            TestingStatus.Text = (frame.NeedsChoice || frame.NeedsInput) && !snapshotReady ? "Выберите ветку или введите ответ, чтобы построить снимок фрейма." :
                frame.Story.Completed ? "После выделенного сегмента нет текстового фрейма." : "Снимок фрейма · " + frame.Status + DescribeIdentity(frame.Story);
        }
    }
    private static string DescribeIdentity(StoryFrame story) => story.Identity is { } identity
        ? $" · фрейм {identity.SceneFrameId} / в игре {identity.GlobalFrameId} · блок {identity.BlockId}" : "";
    private async void Choice_Requested(object? sender, int option) => await ExecuteAsync(async () =>
    {
        if (sceneMode) await scene!.ChooseAsync(option, lifetime.Token);
        else { await frame!.ChooseAsync(option, lifetime.Token); await SeekTextFrameAsync(); }
    });
    private async void Input_Requested(object? sender, TextInputSubmission submission)
    {
        await ExecuteAsync(async () =>
        {
            if (sceneMode) await scene!.SubmitInputAsync(submission.Text, submission.Cancel, lifetime.Token);
            else { await frame!.SubmitInputAsync(submission.Text, submission.Cancel, lifetime.Token); await SeekTextFrameAsync(); }
        });
        if (sceneMode && scene?.Story.Kind == "input" || !sceneMode && frame?.NeedsInput == true && !snapshotReady) SceneSurface.FocusPlayerInput();
    }
    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (scene?.Story.CanAdvance == true) await ExecuteAsync(() => scene.AdvanceAsync(lifetime.Token));
    }
    private async void Back_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        SceneSurface.ResetPresentation(); await scene!.BackAsync(lifetime.Token); lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
    });
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(async () =>
    {
        SceneSurface.ResetPresentation(); await scene!.RestartAsync(lifetime.Token); lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
    });
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
