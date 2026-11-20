using System.IO;

namespace KNE;

internal static class ProjectLifecycle
{
    internal const double DefaultSplashSeconds = 2;
    internal const double DefaultIdleSeconds = 60;

    internal static string? Script(EntryPoints? entries, string kind) => kind switch
    {
        "menu" => entries?.MenuScript,
        "idle" => entries?.IdleScript,
        "save" => entries?.SaveScript,
        "exit" => entries?.ExitScript,
        "error" => entries?.ErrorScript,
        _ => throw new ArgumentException("Неизвестное событие проекта: " + kind)
    };

    internal static string? Resolve(ProjectInfo project, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : PathSafety.ResolveRelative(project.Path, path);

    internal static IEnumerable<string> Paths(EntryPoints? entries)
    {
        if (entries == null) yield break;
        foreach (string? path in new[] { entries.PreMenuScene, entries.SplashImage, entries.IdleScript, entries.SaveScript,
                     entries.ExitScript, entries.MenuScript, entries.ErrorScript })
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
    }

    internal static bool SupportsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".ico" or ".tif" or ".tiff";

    internal static double Duration(double? value, double fallback, double maximum)
    {
        if (value is not double seconds) return fallback;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > maximum)
            throw new InvalidDataException($"Укажите время больше нуля и до {maximum} секунд.");
        return seconds;
    }
}

internal sealed record ProjectLifecycleEvent(string Kind, string? Value = null);
