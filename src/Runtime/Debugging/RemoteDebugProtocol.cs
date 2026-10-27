using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace KNE;

internal sealed record DebugCommand(int Version, string Command, DebugBreakpoint[]? Breakpoints = null);

/// <summary>Editor endpoint. The random pipe accepts only the current Windows user; protocol v1 is newline-delimited JSON.</summary>
internal sealed class RemoteDebugConnection : IAsyncDisposable
{
    internal const string EnvironmentName = "KNE_REMOTE_DEBUG_PIPE";
    internal string PipeName { get; } = "kne-debug-" + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writing = new(1);
    private readonly Task readerTask;
    private StreamWriter? writer;
    private DebugBreakpoint[] breakpoints = [];
    private int disposed;
    internal event Action<DebugSnapshot>? Changed;
    internal bool Connected { get; private set; }
    internal RemoteDebugConnection()
    {
        pipe = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        readerTask = ReadAsync();
    }
    private async Task ReadAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true); var lines = new BoundedLineReader(reader);
                Connected = true; await SendAsync("breakpoints").ConfigureAwait(false);
                while (await lines.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    var value = JsonSerializer.Deserialize<DebugSnapshot>(line) ?? throw new InvalidDataException("Пустой снимок отладки.");
                    Changed?.Invoke(value);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException)
            { if (!lifetime.IsCancellationRequested) Changed?.Invoke(new(false, null, default, "Соединение закрыто: " + error.Message)); }
            finally
            {
                Connected = false; writer = null;
                try { if (!lifetime.IsCancellationRequested && pipe.IsConnected) pipe.Disconnect(); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException) { }
                Changed?.Invoke(new(false, null, default, "Игра отключена · ожидание worker"));
            }
        }
    }
    internal async Task SetBreakpointsAsync(DebugBreakpoint[] values)
    {
        if (values.Length > 1000) throw new InvalidDataException("Не более 1000 точек останова.");
        foreach (var value in values) value.Validate();
        breakpoints = values.ToArray(); if (Connected) await SendAsync("breakpoints").ConfigureAwait(false);
    }
    internal async Task SendAsync(string command)
    {
        if (command is not ("pause" or "continue" or "step" or "detach" or "breakpoints")) throw new InvalidDataException("Неизвестная команда отладки.");
        await writing.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { if (writer != null) await writer.WriteLineAsync(JsonSerializer.Serialize(new DebugCommand(1, command, command == "breakpoints" ? breakpoints : null)).AsMemory(), lifetime.Token).ConfigureAwait(false); }
        finally { writing.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); pipe.Dispose(); await readerTask.ConfigureAwait(false); lifetime.Dispose();
    }
}

internal sealed class WorkerDebugConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly StreamWriter writer;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object writing = new();
    private readonly Action<bool> paused;
    internal RuntimeDebugger Debugger { get; }
    private WorkerDebugConnection(string name, Action<bool> paused)
    {
        this.paused = paused;
        pipe = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect(500); }
        catch { pipe.Dispose(); throw; }
        writer = new(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        Debugger = new(Publish);
        // Receive initial breakpoints before executing the first authored line.
        var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true); var lines = new BoundedLineReader(reader);
        Apply(lines.ReadLineAsync(lifetime.Token).GetAwaiter().GetResult() ?? throw new IOException("Редактор отключён."));
        _ = Task.Run(async () =>
        {
            try { using (reader) { while (await lines.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line) Apply(line); } }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or JsonException) { }
            finally { Debugger.Control("detach"); }
        });
    }
    internal static WorkerDebugConnection? Connect(Action<bool> paused)
    {
        string? name = Environment.GetEnvironmentVariable(RemoteDebugConnection.EnvironmentName);
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return new WorkerDebugConnection(name, paused); }
        catch (Exception error) when (error is IOException or TimeoutException) { return null; }
    }
    private void Apply(string line)
    {
        var command = JsonSerializer.Deserialize<DebugCommand>(line) ?? throw new InvalidDataException("Пустая команда отладки.");
        if (command.Version != 1) throw new InvalidDataException("Неподдерживаемая версия протокола отладки.");
        if (command.Command == "breakpoints") Debugger.Configure(command.Breakpoints ?? []); else Debugger.Control(command.Command);
    }
    internal void Publish(DebugSnapshot snapshot)
    {
        paused(snapshot.Paused);
        try { lock (writing) writer.WriteLine(JsonSerializer.Serialize(snapshot)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Debugger.Control("detach"); }
    }
    public void Dispose() { Debugger.Dispose(); lifetime.Cancel(); pipe.Dispose(); /* The reader finishes on pipe close. */ }
}
