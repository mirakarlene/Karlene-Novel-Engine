using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Explicit isolated scene testing with replay-based one-frame undo and no game save writes.</summary>
public sealed class SceneTestSession : IAsyncDisposable
{
    private readonly ProjectInfo project;
    private readonly EditorSelection selection;
    private sealed record TestAction(string Kind, string BeforeKind, int Option = 0, double Seconds = 0, int FrameStart = 0, string Text = "", bool Cancel = false);
    private readonly List<TestAction> history = [];
    private readonly int seed = Random.Shared.Next(1, int.MaxValue);
    private string? sourceSignature;
    private ProcessGameSession? session;
    private int frameStart;
    public StoryFrame Story => session?.Story ?? StoryFrame.Empty;
    public SceneVisuals Visuals => session?.Visuals ?? SceneVisuals.Empty;
    public RuntimeWindowSettings Window => session?.WindowSettings ?? project.Engine.RuntimeDefaults(project.Title);
    public bool CanGoBack => history.Any(action => IsVisibleFrame(action.BeforeKind));
    public bool Completed => Story.Completed;
    public SceneTestSession(ProjectInfo project, EditorSelection selection) { this.project = project; this.selection = selection; }

    public async Task RestartAsync(CancellationToken token = default)
    {
        history.Clear(); frameStart = 0; await StartFreshAsync(token).ConfigureAwait(false);
    }
    public async Task AdvanceAsync(CancellationToken token = default)
    {
        if (session == null || !Story.CanAdvance) throw new InvalidOperationException("Нет фрейма для продолжения.");
        string before = Story.Kind;
        await session.AdvanceAsync(token).ConfigureAwait(false); history.Add(new("advance", before, FrameStart: frameStart)); frameStart = history.Count;
    }
    public async Task ChooseAsync(int option, CancellationToken token = default)
    {
        if (session == null || Story.Kind != "choice") throw new InvalidOperationException("Нет выбора для продолжения.");
        string before = Story.Kind;
        await session.ChooseAsync(option, token).ConfigureAwait(false); history.Add(new("choose", before, option, FrameStart: frameStart)); frameStart = history.Count;
    }
    public async Task SubmitInputAsync(string text, bool cancel = false, CancellationToken token = default)
    {
        if (session == null || Story.Kind != "input") throw new InvalidOperationException("Нет поля ввода для продолжения.");
        long previous = Story.PresentationId;
        await session.SubmitInputAsync(text, cancel, token).ConfigureAwait(false);
        bool changed = Story.Kind != "input" || Story.PresentationId != previous;
        history.Add(new("input", changed ? "input" : "", FrameStart: frameStart, Text: text, Cancel: cancel));
        if (changed) frameStart = history.Count;
    }
    /// <summary>Tests a scene-scoped input handler and includes its signal in one-frame replay.</summary>
    public async Task TriggerAsync(TriggerSignal signal, CancellationToken token = default)
    {
        if (session == null) throw new InvalidOperationException("Сцена не запущена.");
        if (!Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
        var before = Story;
        await session.TriggerAsync(signal, token).ConfigureAwait(false);
        bool changed = before.PresentationId != Story.PresentationId || before.Kind != Story.Kind;
        history.Add(new("trigger", changed ? before.Kind : "", FrameStart: frameStart, Text: JsonSerializer.Serialize(signal, EngineSettings.JsonOptions)));
        if (changed) frameStart = history.Count;
    }
    /// <summary>Advances logical timers and background branches without consuming the visible frame.</summary>
    public async Task TickAsync(double seconds, CancellationToken token = default)
    {
        if (session == null || !Story.NeedsTick) return;
        var before = Story;
        await session.TickAsync(seconds, token).ConfigureAwait(false);
        bool changed = before.PresentationId != Story.PresentationId || before.Identity != Story.Identity || before.Kind != Story.Kind || before.Text != Story.Text || before.Speaker != Story.Speaker || !before.Options.SequenceEqual(Story.Options);
        history.Add(new("tick", changed ? before.Kind : "", Seconds: seconds, FrameStart: frameStart));
        if (changed) frameStart = history.Count;
    }
    public async Task BackAsync(CancellationToken token = default)
    {
        if (!CanGoBack) throw new InvalidOperationException("Предыдущего фрейма нет.");
        if (sourceSignature != await ReadSourceSignatureAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Сценарии изменились во время тестирования. Начните сцену заново для безопасного возврата.");
        int previousFrame = history.FindLast(action => IsVisibleFrame(action.BeforeKind))!.FrameStart;
        history.RemoveRange(previousFrame, history.Count - previousFrame);
        TestAction[] actions = history.ToArray(); await StartFreshAsync(token).ConfigureAwait(false);
        foreach (var action in actions)
        {
            token.ThrowIfCancellationRequested();
            if (action.Kind == "tick") await session!.TickAsync(action.Seconds, token).ConfigureAwait(false);
            else if (action.Kind == "trigger") await session!.TriggerAsync(JsonSerializer.Deserialize<TriggerSignal>(action.Text, EngineSettings.JsonOptions)!, token).ConfigureAwait(false);
            else if (action.Kind == "advance") await session!.AdvanceAsync(token).ConfigureAwait(false);
            else if (action.Kind == "input") await session!.SubmitInputAsync(action.Text, action.Cancel, token).ConfigureAwait(false);
            else await session!.ChooseAsync(action.Option, token).ConfigureAwait(false);
        }
        frameStart = history.FindLastIndex(action => action.BeforeKind.Length != 0) + 1;
    }
    private static bool IsVisibleFrame(string kind) => kind is "dialogue" or "choice" or "text_pause" or "input";
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
