using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace KNE;

/// <summary>A completed, owned output folder and its launchable Windows x64 executable.</summary>
public sealed record GameBuildResult(string Directory, string Executable, string? Package);

/// <summary>Publishes the shared player using the installed .NET 10 SDK; stages outputs without replacing user files.</summary>
public sealed class GameBuildExporter
{
    public Task<GameBuildResult> BuildAsync(ProjectInfo project, ProjectBuildSettings settings, string outputRoot,
        bool test = false, Action<string>? log = null, CancellationToken token = default) => Task.Run(() => BuildCoreAsync(project, settings, outputRoot, test, log, token), token);

    private async Task<GameBuildResult> BuildCoreAsync(ProjectInfo project, ProjectBuildSettings settings, string outputRoot,
        bool test, Action<string>? log, CancellationToken token)
    {
        settings.Validate();
        if (settings.GameId == "") throw new InvalidDataException("Сначала сохраните параметры сборки проекта.");
        outputRoot = ValidateOutput(project.Path, outputRoot);
        // Compilation and project enumeration do not execute scripts/plugins.
        _ = GameScripts.Load(project);
        string leaf = (test ? "test-" : "release-") + settings.ReleaseMode;
        string destination = PathSafety.ResolveRelative(outputRoot, leaf);
        if (Directory.Exists(destination) && !OwnedBy(destination, settings.GameId)) throw new IOException("Папка билда содержит посторонние файлы: " + destination);
        Directory.CreateDirectory(outputRoot);
        string stage = PathSafety.ResolveRelative(outputRoot, ".kne-build-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(stage, "source"), publish = Path.Combine(stage, "payload"), result = Path.Combine(stage, "result");
        Directory.CreateDirectory(source); Directory.CreateDirectory(result);
        try
        {
            using (var archive = typeof(App).Assembly.GetManifestResourceStream("KNE.BuildSource.zip") ?? throw new InvalidOperationException("В этой версии KNE отсутствует набор исходников для экспорта. Пересоберите редактор."))
            using (var zip = new ZipArchive(archive)) ExtractZip(zip, source, token);
            string game = Path.Combine(source, "PlayerContent"); Directory.CreateDirectory(game);
            await CopyProjectAsync(project, game, token).ConfigureAwait(false);
            var manifest = new PlayerManifest(settings.GameId, project.Title, settings.EffectiveSaveLocation, settings.ReleaseMode, settings.Author, settings.Team, settings.Version, Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(Path.Combine(source, "kne-player.json"), JsonSerializer.Serialize(manifest), token).ConfigureAwait(false);
            if (settings.ReleaseMode == "single_file")
            {
                ZipFile.CreateFromDirectory(game, Path.Combine(source, "kne-game.zip"), CompressionLevel.Optimal, false);
                Directory.Delete(game, true);
            }
            CreatePlayerProperties(project.Title, settings).Save(Path.Combine(source, "KnePlayer.props"));
            log?.Invoke("Публикация Windows x64 · " + settings.ReleaseMode);
            await RunToolAsync("dotnet", PublishArguments(source, publish, settings), source, log, token).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(publish, "Game.exe"))) throw new IOException("Публикация не создала Game.exe.");
            string? packageName = null;
            if (!test && settings.ReleaseMode == "msix")
            {
                packageName = "Game.msix";
                await MsixPackage.CreateAsync(publish, Path.Combine(result, packageName), manifest, settings.FourPartVersion, log, token).ConfigureAwait(false);
            }
            else
            {
                if (!test && settings.ReleaseMode is "framework_dependent" or "self_contained" or "portable")
                { packageName = "Game.zip"; ZipFile.CreateFromDirectory(publish, Path.Combine(result, packageName), CompressionLevel.Optimal, false); }
            }
            Directory.Move(publish, Path.Combine(result, "game"));
            await File.WriteAllTextAsync(Path.Combine(result, ".kne-build.json"), JsonSerializer.Serialize(new { manifest.GameId }), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string backup = destination + ".old-" + Guid.NewGuid().ToString("N");
            bool previous = Directory.Exists(destination);
            if (previous) { if (!OwnedBy(destination, settings.GameId)) throw new IOException("Папка билда изменилась во время сборки."); Directory.Move(destination, backup); }
            try { Directory.Move(result, destination); }
            catch { if (previous) Directory.Move(backup, destination); throw; }
            if (previous) DeleteStage(outputRoot, backup);
            return new(destination, Path.Combine(destination, "game", "Game.exe"), packageName == null ? null : Path.Combine(destination, packageName));
        }
        finally { DeleteStage(outputRoot, stage); }
    }

    internal static string ValidateOutput(string projectPath, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathFullyQualified(outputRoot)) throw new ArgumentException("Укажите полный путь к папке билда.");
        string path = PathSafety.NormalizeDirectory(outputRoot), project = PathSafety.NormalizeDirectory(projectPath);
        PathSafety.RejectLinks(path);
        if (path.Equals(project, StringComparison.OrdinalIgnoreCase) || path.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || project.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetPathRoot(path) == path)
            throw new ArgumentException("Билд должен находиться в отдельной папке вне проекта, не в корне диска и не в папке-предке проекта.");
        return path;
    }

    internal static string[] PublishArguments(string source, string publish, ProjectBuildSettings settings) =>
        ["publish", Path.Combine(source, "KNE.csproj"), "-c", "Release", "-r", "win-x64", "--self-contained", settings.ReleaseMode == "framework_dependent" ? "false" : "true", "-p:KnePlayerBuild=true", "-o", publish, "--verbosity", "minimal"];

    internal static XDocument CreatePlayerProperties(string title, ProjectBuildSettings settings)
    {
        static string Escape(string text) => text.Replace("%", "%25").Replace("$", "%24").Replace("@", "%40").Replace(";", "%3B").Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("?", "%3F").Replace("*", "%2A");
        var group = new XElement("PropertyGroup",
            new XElement("AssemblyName", "Game"), new XElement("RootNamespace", "KNE"), new XElement("DefineConstants", "$(DefineConstants);KNE_PLAYER"),
            new XElement("Product", Escape(title)), new XElement("Title", Escape(title)), new XElement("Description", Escape(title + " · " + settings.Author)),
            new XElement("Company", Escape(string.IsNullOrWhiteSpace(settings.Team) ? settings.Author : settings.Team)), new XElement("Authors", Escape(settings.Author)),
            new XElement("Copyright", Escape(settings.Author)), new XElement("Version", settings.FourPartVersion), new XElement("FileVersion", settings.FourPartVersion), new XElement("AssemblyVersion", settings.FourPartVersion),
            new XElement("InformationalVersion", settings.Version), new XElement("IncludeSourceRevisionInInformationalVersion", "false"),
            new XElement("PublishTrimmed", "false"), new XElement("DebugType", "embedded"));
        if (settings.ReleaseMode == "single_file")
        { group.Add(new XElement("PublishSingleFile", "true"), new XElement("IncludeNativeLibrariesForSelfExtract", "true"), new XElement("IncludeAllContentForSelfExtract", "true"), new XElement("EnableCompressionInSingleFile", "true")); }
        var items = new XElement("ItemGroup", new XElement("EmbeddedResource", new XAttribute("Include", "kne-player.json"), new XAttribute("LogicalName", "KNE.Player.json")));
        if (settings.ReleaseMode == "single_file") items.Add(new XElement("EmbeddedResource", new XAttribute("Include", "kne-game.zip"), new XAttribute("LogicalName", "KNE.Game.zip")));
        else items.Add(new XElement("Content", new XAttribute("Include", "PlayerContent/**/*"), new XElement("Link", "project/%(RecursiveDir)%(Filename)%(Extension)"), new XElement("CopyToPublishDirectory", "PreserveNewest")));
        return new(new XElement("Project", group, items));
    }

    internal static async Task CopyProjectAsync(ProjectInfo project, string target, CancellationToken token)
    {
        string saves = project.Engine.Performance.ResolveSavesPath(project.Path);
        foreach (string file in ProjectLoader.EnumerateFiles(project.Path, token).Files)
        {
            token.ThrowIfCancellationRequested();
            if (file.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            string relative = Path.GetRelativePath(project.Path, file).Replace('\\', '/');
            bool privateFolder = relative.Split('/').Any(part => new[] { ".git", ".hg", ".svn", ".vs", ".idea", ".agents", ".codex" }.Contains(part, StringComparer.OrdinalIgnoreCase));
            bool localCache = relative.StartsWith(".kne/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith(".kne/plugin-assets/", StringComparison.OrdinalIgnoreCase);
            if (privateFolder || localCache || relative.Equals("engine/build.json", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = PathSafety.ResolveRelative(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = File.OpenRead(file); using var output = File.Create(destination); await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
        // Do not distribute the developer's absolute save path.
        var performance = PerformanceSettingsStore.LoadProject(target); performance.SavesPath = "saves";
        await PerformanceSettingsStore.SaveProjectAsync(target, performance, token).ConfigureAwait(false);
    }

    internal static void ExtractZip(ZipArchive zip, string root, CancellationToken token)
    {
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string file = PathSafety.ResolveRelative(root, entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(file); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); entry.ExtractToFile(file, false);
        }
    }

    private static bool OwnedBy(string directory, string gameId)
    {
        string marker = PathSafety.ResolveRelative(directory, ".kne-build.json");
        try { using var json = JsonDocument.Parse(File.ReadAllText(marker)); return json.RootElement.GetProperty("GameId").GetString() == gameId; }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }

    private static void DeleteStage(string root, string path)
    {
        path = PathSafety.EnsureInside(root, path); if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    internal static async Task RunToolAsync(string executable, IEnumerable<string> arguments, string workingDirectory, Action<string>? log, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Не удалось запустить " + executable);
        var tail = new Queue<string>(); object sync = new();
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line) { if (line.Length > 2000) line = line[..2000]; lock (sync) { if (tail.Count == 12) tail.Dequeue(); tail.Enqueue(line); } log?.Invoke(line); } }
        try { await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(token)).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(executable)} завершился с кодом {process.ExitCode}. Для экспорта нужен .NET 10 SDK, для MSIX — Windows SDK (MakeAppx.exe).\n" + string.Join("\n", tail));
    }
}
