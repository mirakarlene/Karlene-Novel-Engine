using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace KNE;

internal sealed record WorkerRequest(long Id, string Command, int Option = 0, string? Text = null, GameCheckpoint? Checkpoint = null,
    EditorPreviewSource? EditorSource = null, double Seconds = 0);
internal sealed record SessionSnapshot(RuntimeWindowSettings Window, StoryFrame Story, SceneVisuals Visuals,
    PreviewLocation? Location = null, JsonElement Variables = default, bool TransitionInProgress = false);
internal sealed record WorkerMessage(string Type, long Id = 0, string? Text = null, SessionSnapshot? Snapshot = null,
    GameCheckpoint? Checkpoint = null, string? Error = null, string? ErrorType = null, GameErrorReport? Report = null);

internal static class LuaWorker
{
    public static async Task<int> RunAsync(string projectPath, string? scenePath = null, bool preview = false)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        object outputLock = new();
        var requests = new BoundedLineReader(input);
        void Send(WorkerMessage message)
        {
            if (message.Type == "log" && message.Text is { Length: > 4096 } text)
                message = message with { Text = text[..4096] + "…" };
            lock (outputLock) output.WriteLine(JsonSerializer.Serialize(message));
        }
        try
        {
            var project = await ProjectLoader.LoadProjectAsync(projectPath).ConfigureAwait(false);
            if (scenePath != null)
            {
                // The UI may preview a scene without changing the project's persisted entry point.
                _ = PathSafety.ResolveRelative(project.Path, scenePath);
                project.Configuration.EntryPoints ??= new EntryPoints();
                project.Configuration.EntryPoints.MainScene = scenePath;
            }
            LuaRuntime? runtime = null;
            var queue = Channel.CreateBounded<WorkerRequest>(1);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await requests.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        var request = JsonSerializer.Deserialize<WorkerRequest>(line)
                            ?? throw new InvalidDataException("Пустой запрос рабочего процесса.");
                        await queue.Writer.WriteAsync(request).ConfigureAwait(false);
                    }
                    // The UI disappeared or closed its pipe. Exit even if Lua disabled its hook.
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    try { Send(new("fatal", Error: ex.Message)); }
                    finally { Environment.Exit(1); }
                }
            });
            await foreach (var request in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    runtime ??= new LuaRuntime(project, line => Send(new("log", Text: line)),
                        TimeSpan.FromHours(1), interactive: true, preview: preview, editorSource: request.EditorSource,
                        variablesUpdated: value => Send(new("variables", Snapshot: new(runtime!.WindowSettings, runtime.Story, runtime.Visuals,
                            runtime.Location, value, runtime.TransitionInProgress))));
                    switch (request.Command)
                    {
                        case "start": await runtime.StartAsync().ConfigureAwait(false); break;
                        case "advance": await runtime.AdvanceAsync().ConfigureAwait(false); break;
                        case "choose": await runtime.ChooseAsync(request.Option).ConfigureAwait(false); break;
                        case "tick": await runtime.TickAsync(request.Seconds).ConfigureAwait(false); break;
                        case "command": await runtime.ProcessCommandAsync(request.Text!).ConfigureAwait(false); break;
                        case "restore": await runtime.RestoreAsync(request.Checkpoint!).ConfigureAwait(false); break;
                        case "checkpoint": break;
                        default: throw new InvalidDataException("Неизвестная команда рабочего процесса.");
                    }
                    GameCheckpoint? checkpoint = null;
                    try { if (request.EditorSource == null) checkpoint = await runtime.GetCheckpointAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (request.Command != "checkpoint")
                    {
                        Send(new("log", Text: "Сохранение недоступно: " + ex.Message));
                    }
                    Send(new("result", request.Id, Snapshot: new(runtime.WindowSettings, runtime.Story, runtime.Visuals, runtime.Location, runtime.Variables, runtime.TransitionInProgress), Checkpoint: checkpoint));
                }
                catch (Exception ex) { Send(new("error", request.Id,
                    Snapshot: runtime == null ? null : new(runtime.WindowSettings, runtime.Story, runtime.Visuals, runtime.Location, runtime.Variables, runtime.TransitionInProgress),
                    Error: ex.Message, ErrorType: ex.GetType().Name, Report: GameErrorReport.TryGet(ex))); }
            }
            if (runtime != null) await runtime.DisposeAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Send(new("fatal", Error: ex.Message));
            return 1;
        }
    }
}
