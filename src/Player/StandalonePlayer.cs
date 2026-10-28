using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace KNE;

/// <summary>Embedded game identity; build-machine paths and credentials are never shipped.</summary>
public sealed record PlayerManifest(string GameId, string Title, string SaveLocation, string ReleaseMode, string Author, string Team, string Version, string ContentRevision = "")
{
    public string SavesDirectory(string executableDirectory, string? appData = null) => Path.Combine(SaveLocation == "local" ? executableDirectory : Path.Combine(appData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "games", GameId), "saves");
}

internal static class StandalonePlayer
{
    private static readonly Lazy<PlayerManifest?> Embedded = new(() =>
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("KNE.Player.json");
        return stream == null ? null : JsonSerializer.Deserialize<PlayerManifest>(stream);
    });
    internal static PlayerManifest? Current { get; private set; }
    internal static string SaveIdentity(string projectPath) => (Current ?? Embedded.Value) is { } game ? "kne-game:" + game.GameId : projectPath;
    internal static string DiagnosticsDirectory => Current is { } game ? Path.Combine(Path.GetDirectoryName(game.SavesDirectory(AppContext.BaseDirectory))!, "diagnostics") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KNE", "diagnostics");

    internal static async Task<ProjectInfo> LoadAsync(CancellationToken token = default)
    {
        var assembly = typeof(App).Assembly;
        using var manifest = assembly.GetManifestResourceStream("KNE.Player.json") ?? throw new InvalidDataException("Отсутствует описание игры.");
        Current = await JsonSerializer.DeserializeAsync<PlayerManifest>(manifest, cancellationToken: token).ConfigureAwait(false) ?? throw new InvalidDataException("Пустое описание игры.");
        if (!Guid.TryParseExact(Current.GameId, "N", out _) || Current.SaveLocation is not ("local" or "appdata")) throw new InvalidDataException("Некорректное описание игры.");
        string projectPath = Path.Combine(AppContext.BaseDirectory, "project");
        if (assembly.GetManifestResourceStream("KNE.Game.zip") is { } content)
        {
            using (content)
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false)); content.Position = 0;
                projectPath = Path.Combine(Path.GetTempPath(), "KNE", "games", Current.GameId, hash);
                PathSafety.RejectLinks(projectPath);
                if (!File.Exists(Path.Combine(projectPath, ".complete")))
                {
                    string stage = projectPath + "." + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
                    try
                    {
                        using var zip = new ZipArchive(content); GameBuildExporter.ExtractZip(zip, stage, token);
                        await File.WriteAllTextAsync(Path.Combine(stage, ".complete"), hash, token).ConfigureAwait(false);
                        try { Directory.Move(stage, projectPath); }
                        catch (IOException) when (File.Exists(Path.Combine(projectPath, ".complete"))) { }
                    }
                    finally { if (Directory.Exists(stage)) { PathSafety.EnsureInside(Path.GetDirectoryName(projectPath)!, stage); Directory.Delete(stage, true); } }
                }
            }
        }
        else if (Current.ReleaseMode == "msix")
        {
            if (!Guid.TryParseExact(Current.ContentRevision, "N", out _)) throw new InvalidDataException("Некорректная версия содержимого пакета.");
            string cache = Path.Combine(Path.GetDirectoryName(Current.SavesDirectory(AppContext.BaseDirectory))!, "content", Current.ContentRevision);
            PathSafety.RejectLinks(cache);
            if (!File.Exists(Path.Combine(cache, ".complete")))
            {
                string stage = cache + "." + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(stage);
                try
                {
                    foreach (string file in Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested(); PathSafety.EnsureInside(projectPath, file);
                        string target = PathSafety.ResolveRelative(stage, Path.GetRelativePath(projectPath, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                    }
                    await File.WriteAllTextAsync(Path.Combine(stage, ".complete"), Current.ContentRevision, token).ConfigureAwait(false);
                    try { Directory.Move(stage, cache); } catch (IOException) when (File.Exists(Path.Combine(cache, ".complete"))) { }
                }
                finally { if (Directory.Exists(stage)) { PathSafety.EnsureInside(Path.GetDirectoryName(cache)!, stage); Directory.Delete(stage, true); } }
            }
            projectPath = cache;
        }
        var project = await ProjectLoader.LoadProjectAsync(projectPath, token).ConfigureAwait(false);
        // Scene metrics and plugin asset extraction also stay in the content folder. Portable content is beside the EXE.
        return project;
    }

    internal static GameSaveStore Saves(ProjectInfo project) => new(project.Path, Current!.SavesDirectory(AppContext.BaseDirectory));
}
