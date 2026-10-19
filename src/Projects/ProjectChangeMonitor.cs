using System.IO;

namespace KNE;

/// <summary>Debounces filesystem notifications and disposes the native watcher with the project.</summary>
public sealed class ProjectChangeMonitor : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly object sync = new();
    private bool disposed;
    private string savesPrefix;
    private readonly string defaultSavesPrefix;
    public event EventHandler? Changed;

    public ProjectChangeMonitor(string path)
    {
        defaultSavesPrefix = Path.Combine(PathSafety.NormalizeDirectory(path), "saves") + Path.DirectorySeparatorChar;
        savesPrefix = PerformanceSettingsStore.LoadProject(path).ResolveSavesPath(path) + Path.DirectorySeparatorChar;
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
        bool IsSavePath(string value) => value.StartsWith(savesPrefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(defaultSavesPrefix, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(Path.TrimEndingDirectorySeparator(savesPrefix), StringComparison.OrdinalIgnoreCase) || value.Equals(Path.TrimEndingDirectorySeparator(defaultSavesPrefix), StringComparison.OrdinalIgnoreCase);
        if (e != null && IsSavePath(e.FullPath) && (e is not RenamedEventArgs renamed || IsSavePath(renamed.OldFullPath))) return;
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
    internal void SetSaveDirectory(string path) => savesPrefix = PathSafety.NormalizeDirectory(path) + Path.DirectorySeparatorChar;
}
