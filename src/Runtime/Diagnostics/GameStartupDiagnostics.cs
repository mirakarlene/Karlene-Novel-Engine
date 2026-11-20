using System.IO;
using System.Runtime.InteropServices;

namespace KNE;

internal sealed record GameStorageStatus(string Directory, bool Writable, string Code, string Message, long? AvailableBytes);

internal static class GameStartupDiagnostics
{
    internal static (GameStorageStatus Requested, GameStorageStatus Effective) SelectStorage(ProjectInfo project, string directory)
    {
        var manifest = StandalonePlayer.RuntimeManifest;
        string? fallback = null;
        if (project.Engine.Performance.UnavailableSaves == "appdata" && manifest?.ReleaseMode != "portable")
            fallback = manifest == null ? GameSaveStore.LegacyDirectory(project.Path) :
                (manifest with { SaveLocation = "appdata" }).SavesDirectory(AppContext.BaseDirectory);
        return SelectStorage(directory, fallback);
    }

    internal static (GameStorageStatus Requested, GameStorageStatus Effective) SelectStorage(string directory, string? fallback)
    {
        var requested = CheckStorage(directory);
        if (requested.Writable || fallback == null) return (requested, requested);
        if (PathSafety.NormalizeDirectory(directory).Equals(PathSafety.NormalizeDirectory(fallback), StringComparison.OrdinalIgnoreCase))
            return (requested, requested);
        var candidate = CheckStorage(fallback);
        return (requested, candidate.Writable ? candidate : requested);
    }

    internal static GameStorageStatus CheckStorage(string directory)
    {
        try
        {
            PathSafety.RejectLinks(directory);
            Directory.CreateDirectory(directory);
            // A create-exclusive probe exercises the process's actual access and never opens an existing save.
            string path = PathSafety.ResolveRelative(directory, ".kne-write-check-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var probe = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                probe.WriteByte(0);
                probe.Flush();
            }
            return new(directory, true, "ok", "Папка доступна для записи.", FreeSpace(directory));
        }
        catch (UnauthorizedAccessException)
        {
            bool protectedInstall = InProgramFiles(directory);
            string message = protectedInstall
                ? "Windows ограничивает запись в Program Files для текущего пользователя. Выберите доступную папку сохранений."
                : "Текущему пользователю запрещена запись в выбранную папку сохранений.";
            return new(directory, false, "access_denied", message, null);
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        {
            return new(directory, false, "storage_unavailable", "Не удалось записать проверочный файл в папку сохранений. " + error.Message, null);
        }
    }

    internal static string RuntimeEnvironment()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("ntdll.dll", out var library)) return "unknown";
        try
        {
            if (!NativeLibrary.TryGetExport(library, "wine_get_version", out _)) return "windows";
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")) ? "wine" : "proton";
        }
        finally { NativeLibrary.Free(library); }
    }

    private static long? FreeSpace(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool InProgramFiles(string directory) =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Where(root => root.Length > 0)
            .Any(root =>
            {
                string relative = Path.GetRelativePath(root, directory);
                return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
            });
}
