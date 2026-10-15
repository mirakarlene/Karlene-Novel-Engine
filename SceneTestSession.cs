using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Explicit isolated scene testing with replay-based one-frame undo and no game save writes.</summary>
public sealed class SceneTestSession : IAsyncDisposable
{
    private readonly ProjectInfo project;
    private readonly EditorSelection selection;
    private readonly List<int> history = [];
    private readonly int seed = Random.Shared.Next(1, int.MaxValue);
    private string? sourceSignature;
    private ProcessGameSession? session;
    public StoryFrame Story => session?.Story ?? StoryFrame.Empty;
    public SceneVisuals Visuals => session?.Visuals ?? SceneVisuals.Empty;
    public RuntimeWindowSettings Window => session?.WindowSettings ?? project.Engine.RuntimeDefaults(project.Title);
    public bool CanGoBack => history.Count > 0;
    public bool Completed => Story.Completed;
    public SceneTestSession(ProjectInfo project, EditorSelection selection) { this.project = project; this.selection = selection; }

    public async Task RestartAsync(CancellationToken token = default)
    {
        history.Clear(); await StartFreshAsync(token).ConfigureAwait(false);
    }
    public async Task AdvanceAsync(CancellationToken token = default)
    {
        if (session == null || Story.Kind != "dialogue") throw new InvalidOperationException("Нет фрейма для продолжения.");
        await session.AdvanceAsync(token).ConfigureAwait(false); history.Add(0);
    }
    public async Task ChooseAsync(int option, CancellationToken token = default)
    {
        if (session == null || Story.Kind != "choice") throw new InvalidOperationException("Нет выбора для продолжения.");
        await session.ChooseAsync(option, token).ConfigureAwait(false); history.Add(option);
    }
    public async Task BackAsync(CancellationToken token = default)
    {
        if (!CanGoBack) throw new InvalidOperationException("Предыдущего фрейма нет.");
        if (sourceSignature != await ReadSourceSignatureAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Сценарии изменились во время тестирования. Начните сцену заново для безопасного возврата.");
        history.RemoveAt(history.Count - 1);
        int[] actions = history.ToArray(); await StartFreshAsync(token).ConfigureAwait(false);
        foreach (int option in actions)
        {
            token.ThrowIfCancellationRequested();
            if (option == 0) await session!.AdvanceAsync(token).ConfigureAwait(false);
            else await session!.ChooseAsync(option, token).ConfigureAwait(false);
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
        foreach (string relative in new[] { "project.json", "engine/state.json" })
        {
            string path = PathSafety.ResolveRelative(project.Path, relative);
            sources += "\n" + relative + "\n" + (File.Exists(path) ? File.ReadAllText(path) : "<missing>");
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sources)));
    }, token);
    public async ValueTask DisposeAsync() { if (session != null) await session.DisposeAsync().ConfigureAwait(false); session = null; }
}
