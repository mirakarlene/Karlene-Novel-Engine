using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Explicit isolated scene testing with replay-based one-frame undo and no game save writes.</summary>
public sealed class SceneTestSession : IAsyncDisposable
{
    private readonly ProjectInfo project;
    private readonly EditorSelection selection;
    private sealed record TestAction(string Kind, string BeforeKind, int Option = 0, double Seconds = 0);
    private readonly List<TestAction> history = [];
    private readonly int seed = Random.Shared.Next(1, int.MaxValue);
    private string? sourceSignature;
    private ProcessGameSession? session;
    public StoryFrame Story => session?.Story ?? StoryFrame.Empty;
    public SceneVisuals Visuals => session?.Visuals ?? SceneVisuals.Empty;
    public RuntimeWindowSettings Window => session?.WindowSettings ?? project.Engine.RuntimeDefaults(project.Title);
    public bool CanGoBack => history.Any(action => action.BeforeKind is "dialogue" or "choice");
    public bool Completed => Story.Completed;
    public SceneTestSession(ProjectInfo project, EditorSelection selection) { this.project = project; this.selection = selection; }

    public async Task RestartAsync(CancellationToken token = default)
    {
        history.Clear(); await StartFreshAsync(token).ConfigureAwait(false);
    }
    public async Task AdvanceAsync(CancellationToken token = default)
    {
        if (session == null || Story.Kind != "dialogue") throw new InvalidOperationException("Нет фрейма для продолжения.");
        string before = Story.Kind;
        await session.AdvanceAsync(token).ConfigureAwait(false); history.Add(new("advance", before));
    }
    public async Task ChooseAsync(int option, CancellationToken token = default)
    {
        if (session == null || Story.Kind != "choice") throw new InvalidOperationException("Нет выбора для продолжения.");
        string before = Story.Kind;
        await session.ChooseAsync(option, token).ConfigureAwait(false); history.Add(new("choose", before, option));
    }
    /// <summary>Advances logical timers and background branches without consuming the visible frame.</summary>
    public async Task TickAsync(double seconds, CancellationToken token = default)
    {
        if (session == null || !Story.NeedsTick) return;
        string before = Story.Kind;
        await session.TickAsync(seconds, token).ConfigureAwait(false); history.Add(new("tick", before, Seconds: seconds));
    }
    public async Task BackAsync(CancellationToken token = default)
    {
        if (!CanGoBack) throw new InvalidOperationException("Предыдущего фрейма нет.");
        if (sourceSignature != await ReadSourceSignatureAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Сценарии изменились во время тестирования. Начните сцену заново для безопасного возврата.");
        int previousFrame = history.FindLastIndex(action => action.BeforeKind is "dialogue" or "choice");
        history.RemoveRange(previousFrame, history.Count - previousFrame);
        TestAction[] actions = history.ToArray(); await StartFreshAsync(token).ConfigureAwait(false);
        foreach (var action in actions)
        {
            token.ThrowIfCancellationRequested();
            if (action.Kind == "tick") await session!.TickAsync(action.Seconds, token).ConfigureAwait(false);
            else if (action.Kind == "advance") await session!.AdvanceAsync(token).ConfigureAwait(false);
            else await session!.ChooseAsync(action.Option, token).ConfigureAwait(false);
        }
    }
    private async Task StartFreshAsync(CancellationToken token)
    {
        if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        sourceSignature = await ReadSourceSignatureAsync(token).ConfigureAwait(false);
        var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(JsonSerializer.Serialize(project.Configuration, ProjectConfiguration.JsonOptions), ProjectConfiguration.JsonOptions)!;
        configuration.EntryPoints ??= new();
        configuration.EntryPoints.MainScene = Path.GetRelativePath(project.Path, PathSafety.EnsureInside(project.Path, selection.FilePath)).Replace('\\', '/');
        var isolated = new ProjectInfo { Path = project.Path, Configuration = configuration, Engine = project.Engine };
        session = new ProcessGameSession(isolated, _ => { }, preview: true,
            editorSource: new EditorPreviewSource(selection.FilePath, selection.Source, selection.Function, StepCommands: false, SceneTest: true, Seed: seed));
        await session.StartAsync(token).ConfigureAwait(false);
    }
    private Task<string> ReadSourceSignatureAsync(CancellationToken token) => Task.Run(() =>
    {
        var scripts = GameScripts.Load(project, new(selection.FilePath, selection.Source));
        string sources = string.Join("\n", scripts.Files.Select(file => file.Path + "\n" + file.Priority + "\n" + file.Source));
        foreach (string path in ProjectLoader.EnumerateFiles(project.Path, token).Files.Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            sources += "\n" + Path.GetRelativePath(project.Path, path) + "\n" + File.ReadAllText(path);
        foreach (string relative in new[] { "project.json", "engine/state.json" })
        {
            string path = PathSafety.ResolveRelative(project.Path, relative);
            sources += "\n" + relative + "\n" + (File.Exists(path) ? File.ReadAllText(path) : "<missing>");
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sources)));
    }, token);
    public async ValueTask DisposeAsync() { if (session != null) await session.DisposeAsync().ConfigureAwait(false); session = null; }
}
