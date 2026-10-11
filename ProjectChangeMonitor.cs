using System.IO;

namespace KNE;

/// <summary>Debounces filesystem notifications and disposes the native watcher with the project.</summary>
public sealed class ProjectChangeMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly object sync = new();
    private bool disposed;
    public event EventHandler? Changed;

    public ProjectChangeMonitor(string path)
    {
        watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        timer = new Timer(_ => Notify(), null, Timeout.Infinite, Timeout.Infinite);
        watcher.Changed += Schedule;
        watcher.Created += Schedule;
        watcher.Deleted += Schedule;
        watcher.Renamed += Schedule;
        watcher.Error += (_, _) => Schedule(null, null);
        try { watcher.EnableRaisingEvents = true; }
        catch { watcher.Dispose(); timer.Dispose(); throw; }
    }

    private void Schedule(object? sender, FileSystemEventArgs? e)
    {
        lock (sync) { if (!disposed) timer.Change(400, Timeout.Infinite); }
    }

    private void Notify()
    {
        lock (sync) { if (disposed) return; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            watcher.Dispose();
            timer.Dispose();
        }
    }
}
