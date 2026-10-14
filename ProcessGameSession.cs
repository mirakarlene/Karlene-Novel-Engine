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
    private long requestId;
    public TimeSpan ExecutionLimit { get; }
    public int? WorkerProcessId { get { lock (sync) return worker?.Id; } }
    public RuntimeWindowSettings WindowSettings { get { lock (sync) return snapshot.Window; } }
    public StoryFrame Story { get { lock (sync) return snapshot.Story; } }
    public SceneVisuals Visuals { get { lock (sync) return snapshot.Visuals; } }

    public ProcessGameSession(ProjectInfo project, Action<string> output, TimeSpan? executionLimit = null, bool preview = false)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.preview = preview;
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        ExecutionLimit = executionLimit ?? TimeSpan.FromSeconds(project.Engine.Game.ExecutionTimeoutSeconds);
        if (ExecutionLimit <= TimeSpan.Zero || ExecutionLimit > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(executionLimit));
        snapshot = new(project.Engine.RuntimeDefaults(project.Title), StoryFrame.Empty, SceneVisuals.Empty);
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => ExecuteAsync("start", cancellationToken);
    public Task AdvanceAsync(CancellationToken cancellationToken = default) => ExecuteAsync("advance", cancellationToken);
    public Task ChooseAsync(int option, CancellationToken cancellationToken = default) => ExecuteAsync("choose", cancellationToken, option: option);
    public Task ProcessCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return ExecuteAsync("command", cancellationToken, text: command);
    }
    public async Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default) =>
        (await ExecuteAsync("checkpoint", cancellationToken).ConfigureAwait(false)).Checkpoint
        ?? throw new InvalidDataException("Состояние игры нельзя сохранить.");
    public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ExecuteAsync("restore", cancellationToken, checkpoint: checkpoint);
    }

    private async Task<WorkerMessage> ExecuteAsync(string command, CancellationToken token, int option = 0,
        string? text = null, GameCheckpoint? checkpoint = null)
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
            token.ThrowIfCancellationRequested();
            var exchange = ExchangeAsync(command, option, text, checkpoint, linked.Token);
            WorkerMessage message;
            try { message = await exchange.WaitAsync(ExecutionLimit, linked.Token).ConfigureAwait(false); }
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
                recovery = message.Checkpoint;
                if (command is "start" or "restore") started = true;
            }
            return message;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            if (GameErrorReport.TryGet(ex) == null)
                GameErrorReport.Attach(ex, GameErrorReport.Create(project, ex, command switch
                {
                    "start" => "Запуск игры", "advance" => "Продолжение сценария", "choose" => "Выбор ответа",
                    "command" => "Команда отладки", "restore" => "Загрузка сохранения", _ => "Связь с Lua-worker"
                }));
            KillWorker(); throw;
        }
        catch { KillWorker(); throw; }
        finally { gate.Release(); }
    }

    private async Task<WorkerMessage> ExchangeAsync(string command, int option, string? text,
        GameCheckpoint? checkpoint, CancellationToken token)
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
                string assembly = typeof(App).Assembly.Location;
                string executable = Path.ChangeExtension(assembly, ".exe");
                var info = new ProcessStartInfo(File.Exists(executable) ? executable : "dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(assembly)!
                };
                if (!File.Exists(executable)) info.ArgumentList.Add(assembly);
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
            await SendAsync(process, new(++requestId, "restore", Checkpoint: safePoint), token).ConfigureAwait(false);
        return await SendAsync(process, new(++requestId, command, option, text, checkpoint), token).ConfigureAwait(false);
    }

    private async Task<WorkerMessage> SendAsync(Process process, WorkerRequest request, CancellationToken token)
    {
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
            if (message.Type == "fatal") throw new IOException(message.Error);
            if (message.Id != request.Id) throw new InvalidDataException("Нарушен порядок ответов Lua.");
            if (message.Type == "error") throw CreateException(message);
            if (message.Type != "result" || message.Snapshot == null) throw new InvalidDataException("Некорректный ответ Lua.");
            return message;
        }
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
        if (message.Report != null) GameErrorReport.Attach(error, message.Report);
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
