using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>A breakpoint in an authored KRL line or a visual block, relative to the game content root.</summary>
public sealed record DebugBreakpoint(string File, int Line = 0, string? NodeId = null)
{
    public override string ToString() => File + " · " + (NodeId == null ? "строка " + Line : "блок " + NodeId);
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(File) || File.Length > 2000 || Path.IsPathRooted(File) || File.Replace('\\', '/').Split('/').Any(p => p is ".." or "." or "")) throw new InvalidDataException("Точка останова требует относительный путь сцены.");
        if (NodeId == null ? !Path.GetExtension(File).Equals(".krl", StringComparison.OrdinalIgnoreCase) || Line is < 1 or > 1000000 : !ScriptDocument.IsVisualPath(File) || NodeId.Length is < 1 or > 128 || Line != 0) throw new InvalidDataException("Точка останова: строка .krl либо ID блока .knegraph.");
    }
}

/// <summary>A bounded read-only debugger snapshot, sent only over a local per-user JSON pipe.</summary>
public sealed record DebugSnapshot(bool Paused, PreviewLocation? Location, JsonElement Variables, string Status);

/// <summary>Thread-safe worker control; only the Lua execution thread inspects variables and waits at source locations.</summary>
public sealed class RuntimeDebugger : IDisposable
{
    private readonly object sync = new();
    private readonly ManualResetEventSlim resume = new(true);
    private DebugBreakpoint[] breakpoints = [];
    private bool pauseRequested = true, step, attached = true;
    private readonly Action<DebugSnapshot> publish;
    public bool Paused { get; private set; }

    public RuntimeDebugger(Action<DebugSnapshot> publish) => this.publish = publish;
    public void Configure(IReadOnlyList<DebugBreakpoint> points)
    {
        if (points.Count > 1000) throw new InvalidDataException("Не более 1000 точек останова.");
        foreach (var point in points) { if (point == null) throw new InvalidDataException("Пустая точка останова."); point.Validate(); }
        lock (sync) breakpoints = points.ToArray();
    }
    public void Control(string command)
    {
        lock (sync)
        {
            switch (command)
            {
                case "pause": pauseRequested = true; break;
                case "continue": pauseRequested = false; step = false; Paused = false; resume.Set(); break;
                case "step": pauseRequested = false; step = true; Paused = false; resume.Set(); break;
                case "detach": attached = false; pauseRequested = step = Paused = false; resume.Set(); break;
                default: throw new InvalidDataException("Неизвестная команда отладки.");
            }
        }
    }
    public bool ShouldPause(string file, int line, string node)
    {
        lock (sync)
        {
            if (!attached) return false;
            bool graph = ScriptDocument.IsVisualPath(file);
            if (graph ? node.Length == 0 : !Path.GetExtension(file).Equals(".krl", StringComparison.OrdinalIgnoreCase) || node.Length != 0) return false;
            return pauseRequested || step || breakpoints.Any(point => point.File.Replace('\\', '/').Equals(file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) && (graph ? point.NodeId == node : point.NodeId == null && point.Line == line));
        }
    }
    internal long Wait(PreviewLocation location, JsonElement variables, CancellationToken token)
    {
        lock (sync)
        {
            if (!attached) return 0;
            pauseRequested = step = false; Paused = true; resume.Reset();
        }
        long start = Stopwatch.GetTimestamp();
        publish(new(true, location, variables, "Пауза"));
        try { resume.Wait(token); }
        finally { publish(new(false, location, variables, attached ? "Выполнение" : "Редактор отключён")); }
        return Stopwatch.GetTimestamp() - start;
    }
    public void Dispose() { Control("detach"); /* The Lua thread may still be leaving Wait; the event is owned until process shutdown. */ }
}
