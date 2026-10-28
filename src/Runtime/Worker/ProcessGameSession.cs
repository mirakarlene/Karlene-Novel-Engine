using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KNE;

/// <summary>Runs Lua outside the UI process and kills the entire worker tree on cancellation or timeout.</summary>
public sealed class ProcessGameSession : IGameSession
{
    private readonly ProjectInfo project;
    private readonly Action<string> output;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private Process? worker;
    private BoundedLineReader? responses;
    private Task? disposal;
    private SessionSnapshot snapshot;
    private GameCheckpoint? recovery;
    private bool started;
    private readonly bool preview;
    private readonly EditorPreviewSource? editorSource;
    private bool canReturnAfterError;
    private long requestId;
    private volatile bool debuggerPaused;
    private readonly bool pluginsEnabled;
    private readonly string[] applicationPlugins;
    private readonly string? debuggerPipe;
    public TimeSpan ExecutionLimit { get; }
    public int? WorkerProcessId { get { lock (sync) return worker?.Id; } }
    public RuntimeWindowSettings WindowSettings { get { lock (sync) return snapshot.Window; } }
    public StoryFrame Story { get { lock (sync) return snapshot.Story; } }
    public SceneVisuals Visuals { get { lock (sync) return snapshot.Visuals; } }
    private double logicMilliseconds;
    public double LogicMilliseconds { get { lock (sync) return logicMilliseconds; } }
    public PreviewLocation? Location { get { lock (sync) return snapshot.Location; } }
    public bool PreviewPaused => Story.Kind == "preview";
    public JsonElement Variables { get { lock (sync) return snapshot.Variables; } }
    public bool CanReturnAfterError { get { lock (sync) return canReturnAfterError && recovery != null; } }

    public ProcessGameSession(ProjectInfo project, Action<string> output, TimeSpan? executionLimit = null, bool preview = false,
        EditorPreviewSource? editorSource = null, bool? pluginsEnabled = null, string? debuggerPipe = null)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        applicationPlugins = project.ApplicationPlugins.ToArray();
        this.preview = preview;
        this.pluginsEnabled = pluginsEnabled ?? EngineLaunch.PluginsEnabled;
        this.editorSource = editorSource;
        this.debuggerPipe = debuggerPipe ?? Environment.GetEnvironmentVariable(RemoteDebugConnection.EnvironmentName);
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        ExecutionLimit = executionLimit ?? TimeSpan.FromSeconds(project.Engine.Game.ExecutionTimeoutSeconds);
        if (ExecutionLimit <= TimeSpan.Zero || ExecutionLimit > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(executionLimit));
        snapshot = new(project.Engine.RuntimeDefaults(project.Title), StoryFrame.Empty, SceneVisuals.Empty,
            Variables: JsonSerializer.SerializeToElement(new { Globals = new { }, Frames = new { } }));
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => ExecuteAsync("start", cancellationToken);
    public IReadOnlyList<string> PluginScreens { get { lock (sync) return snapshot.PluginScreens ?? []; } }
    public async Task<bool> InterceptInputAsync(PluginInput input, CancellationToken cancellationToken = default) =>
        (await ExecuteAsync("plugin_input", cancellationToken, text: JsonSerializer.Serialize(input)).ConfigureAwait(false)).InputHandled;
    public Task NotifyPluginInputAsync(PluginInput input, CancellationToken cancellationToken = default) => ExecuteAsync("plugin_event_input", cancellationToken, text: JsonSerializer.Serialize(input));
    public Task AdvanceAsync(CancellationToken cancellationToken = default) => ExecuteAsync("advance", cancellationToken);
    public Task ChooseAsync(int option, CancellationToken cancellationToken = default)
    {
        var frame = Story;
        if (frame.Kind != "choice") throw new InvalidOperationException("Сценарий не ожидает выбора.");
        if (option < 1 || option > frame.Options.Count) throw new ArgumentOutOfRangeException(nameof(option));
        if (frame.ChoiceOptions != null && !frame.ChoiceOptions[option - 1].IsEnabled)
            throw new InvalidOperationException("Этот вариант выбора заблокирован.");
        return ExecuteAsync("choose", cancellationToken, option: option);
    }
    public Task SubmitInputAsync(string text, bool cancel = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 8192) throw new ArgumentException("Ответ слишком длинный.", nameof(text));
        return ExecuteAsync("input", cancellationToken, text: text, cancel: cancel);
    }
    public Task TickAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        return Story.NeedsTick ? ExecuteAsync("tick", cancellationToken, seconds: seconds) : Task.CompletedTask;
    }
    public Task ProcessCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return ExecuteAsync("command", cancellationToken, text: command);
    }
    public Task TriggerAsync(TriggerSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal); signal.Validate();
        return ExecuteAsync("trigger", cancellationToken, text: JsonSerializer.Serialize(signal, EngineSettings.JsonOptions));
    }
    public async Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default) =>
        (await ExecuteAsync("checkpoint", cancellationToken).ConfigureAwait(false)).Checkpoint
        ?? throw new InvalidDataException("Состояние игры нельзя сохранить.");
    public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ExecuteAsync("restore", cancellationToken, checkpoint: checkpoint);
    }

    public Task ReturnAsync(CancellationToken cancellationToken = default)
    {
        GameCheckpoint checkpoint;
        lock (sync)
        {
            if (!canReturnAfterError || recovery == null) throw new InvalidOperationException("Нет безопасного сегмента для возврата после перехода.");
            checkpoint = recovery;
        }
        return RestoreAsync(checkpoint, cancellationToken);
    }

    private async Task<WorkerMessage> ExecuteAsync(string command, CancellationToken token, int option = 0,
        string? text = null, GameCheckpoint? checkpoint = null, double seconds = 0, bool cancel = false)
    {
        CancellationToken shutdown;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposal != null, this);
            shutdown = lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (command is not ("tick" or "checkpoint")) EngineDiagnostics.Trace("Игровой рабочий процесс", command + " · " + project.Title);
            token.ThrowIfCancellationRequested();
            var exchange = ExchangeAsync(command, option, text, checkpoint, linked.Token, seconds, cancel);
            WorkerMessage message;
            try { message = await WaitForExecutionAsync(exchange, linked.Token).ConfigureAwait(false); }
            catch
            {
                linked.Cancel();
                KillWorker();
                // Observe the abandoned exchange: killing closes the pipes and releases its reads.
                _ = exchange.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
            lock (sync)
            {
                if (message.Snapshot != null) snapshot = message.Snapshot;
                if (command != "checkpoint" && message.Snapshot != null) logicMilliseconds = message.Snapshot.LogicMilliseconds;
                recovery = message.Checkpoint;
                canReturnAfterError = false;
                if (command is "start" or "restore") started = true;
            }
            return message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            if (GameErrorReport.TryGet(ex) == null)
            {
                bool transitionFailure;
                lock (sync)
                {
                    transitionFailure = snapshot.TransitionInProgress;
                    canReturnAfterError = transitionFailure && recovery != null;
                }
                GameErrorReport.Attach(ex, GameErrorReport.Create(project, ex, command switch
                {
                    "start" => "Запуск игры", "advance" => "Продолжение сценария", "choose" => "Выбор ответа",
                    "command" => "Команда отладки", "restore" => "Загрузка сохранения", "input" => "Ввод ответа игрока", _ => "Связь с Lua-worker"
                }) with { TransitionFailure = transitionFailure });
            }
            KillWorker(); throw;
        }
        catch { KillWorker(); throw; }
        finally { gate.Release(); }
    }

    private async Task<WorkerMessage> ExchangeAsync(string command, int option, string? text,
        GameCheckpoint? checkpoint, CancellationToken token, double seconds = 0, bool cancel = false)
    {
        Process process;
        bool restart;
        GameCheckpoint? safePoint;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(disposal != null, this);
            restart = worker == null || worker.HasExited;
            if (restart)
            {
                worker?.Dispose();
                var info = EngineLaunch.CreateStartInfo();
                info.RedirectStandardInput = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                info.StandardInputEncoding = new UTF8Encoding(false); info.StandardOutputEncoding = Encoding.UTF8; info.StandardErrorEncoding = Encoding.UTF8;
                info.Environment[EngineLaunch.PluginsEnvironment] = pluginsEnabled ? "0" : "1";
                if (string.IsNullOrEmpty(debuggerPipe)) info.Environment.Remove(RemoteDebugConnection.EnvironmentName); else info.Environment[RemoteDebugConnection.EnvironmentName] = debuggerPipe;
                info.ArgumentList.Add("--lua-worker");
                info.ArgumentList.Add(project.Path);
                info.ArgumentList.Add(Path.GetRelativePath(project.Path, project.MainScenePath));
                if (preview) info.ArgumentList.Add("--preview");
                worker = Process.Start(info) ?? throw new IOException("Не удалось запустить Lua.");
                responses = new(worker.StandardOutput);
                _ = DrainErrorsAsync(worker.StandardError);
            }
            process = worker!;
            safePoint = recovery;
            if (restart && started && command is not ("start" or "restore") && safePoint == null)
                throw new InvalidOperationException("После остановки требуется перезапустить игру: состояние нельзя восстановить.");
        }
        if (restart && started && command is not ("start" or "restore"))
            await SendAsync(process, new(++requestId, "restore", Checkpoint: safePoint, EditorSource: editorSource), token).ConfigureAwait(false);
        return await SendAsync(process, new(++requestId, command, option, text, checkpoint, editorSource, seconds, cancel), token).ConfigureAwait(false);
    }

    private async Task<WorkerMessage> SendAsync(Process process, WorkerRequest request, CancellationToken token)
    {
        request = request with { ApplicationPlugins = applicationPlugins };
        var reader = responses!;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        while (true)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line == null) throw new IOException("Рабочий процесс Lua завершился неожиданно.");
            var message = JsonSerializer.Deserialize<WorkerMessage>(line)
                ?? throw new InvalidDataException("Некорректный ответ Lua.");
            if (message.Type == "log") { output(message.Text ?? ""); continue; }
            if (message.Type is "debug_paused" or "debug_running") { debuggerPaused = message.Type == "debug_paused"; continue; }
            if (message.Type == "variables")
            {
                if (message.Snapshot != null) lock (sync) snapshot = message.Snapshot;
                continue;
            }
            if (message.Type == "fatal") throw new IOException(message.Error);
            if (message.Id != request.Id) throw new InvalidDataException("Нарушен порядок ответов Lua.");
            if (message.Type == "error")
            {
                lock (sync)
                {
                    if (message.Snapshot != null) snapshot = message.Snapshot;
                    canReturnAfterError = message.Report?.TransitionFailure == true && recovery != null;
                }
                throw CreateException(message);
            }
            if (message.Type != "result" || message.Snapshot == null) throw new InvalidDataException("Некорректный ответ Lua.");
            return message;
        }
    }

    private async Task<WorkerMessage> WaitForExecutionAsync(Task<WorkerMessage> exchange, CancellationToken token)
    {
        if (string.IsNullOrEmpty(debuggerPipe)) return await exchange.WaitAsync(ExecutionLimit, token).ConfigureAwait(false);
        var remaining = ExecutionLimit; long last = Stopwatch.GetTimestamp();
        while (!exchange.IsCompleted)
        {
            await Task.WhenAny(exchange, Task.Delay(50, token)).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp(); if (!debuggerPaused) remaining -= Stopwatch.GetElapsedTime(last, now); last = now;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Превышено время выполнения игрового worker.");
        }
        return await exchange.ConfigureAwait(false);
    }

    private static Exception CreateException(WorkerMessage message)
    {
        Exception error = message.ErrorType switch
        {
        nameof(InvalidDataException) => new InvalidDataException(message.Error),
        nameof(InvalidOperationException) => new InvalidOperationException(message.Error),
        nameof(ArgumentException) => new ArgumentException(message.Error),
        nameof(ArgumentOutOfRangeException) => new ArgumentOutOfRangeException(null, message.Error),
        nameof(FileNotFoundException) => new FileNotFoundException(message.Error),
        nameof(TimeoutException) => new TimeoutException(message.Error),
            _ => new InvalidOperationException("Ошибка Lua: " + message.Error)
        };
        if (message.Report != null)
        {
            GameErrorReport.Attach(error, message.Report);
            if (message.Report.PluginFailure)
                PluginFailure.Report(error, message.Report.File ?? "Игровой worker", message.Report.Operation);
        }
        return error;
    }

    private static async Task DrainErrorsAsync(StreamReader reader)
    {
        // Never allow redirected stderr to fill its pipe, without retaining an unbounded log.
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void KillWorker()
    {
        lock (sync)
        {
            var process = worker;
            worker = null;
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposal == null)
            {
                lifetime.Cancel();
                KillWorker();
                disposal = DisposeCoreAsync();
            }
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { lifetime.Dispose(); }
        finally { gate.Release(); }
    }
}
