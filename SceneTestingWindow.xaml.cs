using System.Windows;

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
    public SceneTestingWindow(ProjectInfo project, EditorSelection selection, bool sceneTest)
    {
        InitializeComponent(); this.project = project; this.selection = selection; sceneMode = sceneTest;
        Title = (sceneMode ? "Тестировать сцену · " : "Предпросмотр фрейма · ") + project.Title;
        Width = project.Engine.Window.Width; Height = project.Engine.Window.Height + 70;
        SceneSurface.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, StoryFrame.Empty, false, false);
        NextFrameButton.Visibility = sceneMode ? Visibility.Visible : Visibility.Collapsed;
    }
    public Task InitializeAsync() => ExecuteAsync(async () =>
    {
        if (started) return; started = true;
        if (sceneMode) { scene = new SceneTestSession(project, selection); await scene.RestartAsync(lifetime.Token); }
        else { frame = new EditorPreview(project, _ => { }); await frame.SeekAsync(selection with { FromMain = false }, lifetime.Token); await SeekTextFrameAsync(); }
    });
    private async void Testing_Loaded(object sender, RoutedEventArgs e) => await InitializeAsync();
    private async void Testing_Closed(object? sender, EventArgs e)
    {
        closed = true; lifetime.Cancel(); SceneSurface.Stop();
        if (scene != null) await scene.DisposeAsync(); if (frame != null) await frame.DisposeAsync();
        lifetime.Dispose();
    }
    private async Task SeekTextFrameAsync()
    {
        if (frame?.Reached != true) return;
        for (int i = 0; frame.Story.Kind == "preview" && i < 2000; i++) await frame.StepAsync(lifetime.Token);
        if (frame.Story.Kind == "preview") throw new InvalidOperationException("Фрейм не найден после 2000 команд. Проверьте цикл в сцене.");
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
            if (!closed) { TestingErrorText.Text = GameErrorReport.Create(project, error, "Тестирование сценария").ToDisplayText(); TestingErrorOverlay.Visibility = Visibility.Visible; }
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
            NextFrameButton.IsEnabled = scene.Story.Kind == "dialogue";
            TestingStatus.Text = "Изолированный тест сцены · " + (selection.Function ?? "сценарий") + DescribeIdentity(scene.Story);
        }
        else if (frame != null)
        {
            SceneSurface.DisplayScene(project, frame.Window, frame.Visuals, frame.Story, false, frame.NeedsChoice && !snapshotReady);
            TestingStatus.Text = frame.NeedsChoice && !snapshotReady ? "Выберите ветку, чтобы построить снимок фрейма." :
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
    private async void Next_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => scene!.AdvanceAsync(lifetime.Token));
    private async void Back_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => scene!.BackAsync(lifetime.Token));
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ExecuteAsync(() => scene!.RestartAsync(lifetime.Token));
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
