using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KNE;

internal static partial class GameCrashReports
{
    [GeneratedRegex(@"^crash-\d{8}-\d{6}-[0-9a-f]{32}\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex ReportName();

    internal static string DirectoryPath(GameSaveStore saves) => PathSafety.ResolveRelative(saves.DirectoryPath, "diagnostics");

    internal static async Task<int> DeleteAsync(ProjectInfo project)
    {
        string directory = DirectoryPath(StandalonePlayer.RuntimeSaves(project));
        if (!Directory.Exists(directory)) return 0;
        int count = 0;
        foreach (string candidate in Directory.EnumerateFiles(directory, "crash-*.json", SearchOption.TopDirectoryOnly))
        {
            if (!ReportName().IsMatch(Path.GetFileName(candidate)) || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
            string path = PathSafety.EnsureInside(directory, candidate);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > 16_000_000) continue;
            GameErrorReport? report;
            try { report = await JsonSerializer.DeserializeAsync<GameErrorReport>(file, ProjectConfiguration.JsonOptions); }
            catch (JsonException) { continue; }
            if (report == null || !string.Equals(report.ProjectPath, project.Path, StringComparison.OrdinalIgnoreCase)) continue;
            File.Delete(path);
            count++;
        }
        return count;
    }
}
