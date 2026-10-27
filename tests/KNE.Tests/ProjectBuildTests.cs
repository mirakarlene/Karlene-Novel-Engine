using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBuildTests
{
    [Fact]
    public async Task Settings_LegacyDefaultsAndAtomicValidationKeepVmAndStableGameIdentity()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "engine", "build.json");
        Assert.False(File.Exists(path)); Assert.Equal("vm", ProjectBuildSettingsStore.Load(fixture.ProjectPath).TestMode); Assert.False(File.Exists(path));
        await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "A & Б", Team = "Team", Version = "2.3.4" });
        var saved = ProjectBuildSettingsStore.Load(fixture.ProjectPath); Assert.True(Guid.TryParseExact(saved.GameId, "N", out _)); Assert.Equal("A & Б", saved.Author);
        byte[] before = File.ReadAllBytes(path);
        foreach (var invalid in new[] { saved with { TestMode = "bad" }, saved with { ReleaseMode = "bad" }, saved with { Version = "1.2.65536" }, saved with { TestMode = "standalone", TestPath = "relative" }, saved with { SaveLocation = "bad" }, saved with { GameId = "../escape" } })
        { await Assert.ThrowsAsync<InvalidDataException>(() => ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, invalid)); Assert.Equal(before, File.ReadAllBytes(path)); }
        await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, saved with { Version = "2.3.5" }); Assert.Equal(saved.GameId, ProjectBuildSettingsStore.Load(fixture.ProjectPath).GameId);
    }

    [Theory]
    [InlineData("framework_dependent", "false", "appdata")]
    [InlineData("self_contained", "true", "appdata")]
    [InlineData("single_file", "true", "appdata")]
    [InlineData("msix", "true", "appdata")]
    [InlineData("portable", "true", "local")]
    public void Profiles_SetRuntimeStorageMetadataAndNeverEnableTrimming(string mode, string runtime, string location)
    {
        var settings = new ProjectBuildSettings { ReleaseMode = mode, Author = "Nick & <author>", Team = "Команда", Version = "2.3.4" };
        var args = GameBuildExporter.PublishArguments("C:\\src", "C:\\publish", settings); int self = Array.IndexOf(args, "--self-contained"); Assert.Equal(runtime, args[self + 1]); Assert.Contains("win-x64", args); Assert.Contains("-p:KnePlayerBuild=true", args);
        var props = GameBuildExporter.CreatePlayerProperties("Игра & <title>", settings); var group = props.Root!.Element("PropertyGroup")!;
        Assert.Equal("false", group.Element("PublishTrimmed")!.Value); Assert.Equal("2.3.4.0", group.Element("FileVersion")!.Value); Assert.Equal(settings.Team, group.Element("Company")!.Value); Assert.Equal(settings.Author, group.Element("Copyright")!.Value); Assert.Equal("Игра & <title>", group.Element("Product")!.Value);
        Assert.Equal(mode == "single_file", group.Element("PublishSingleFile")?.Value == "true"); Assert.Equal(location, settings.EffectiveSaveLocation);
        if (mode == "single_file") { Assert.Equal("true", group.Element("EnableCompressionInSingleFile")!.Value); Assert.Equal("true", group.Element("IncludeAllContentForSelfExtract")!.Value); Assert.Contains("KNE.Game.zip", props.ToString()); }
        if (mode == "msix") { var manifest = MsixPackage.Manifest(new(Guid.NewGuid().ToString("N"), "Игра", location, mode, "Nick", "Команда", "2.3.4"), settings.FourPartVersion); Assert.Contains("Windows.FullTrustApplication", manifest.ToString()); Assert.Contains("2.3.4.0", manifest.ToString()); Assert.Contains("Команда", manifest.ToString()); }
    }

    [Fact]
    public void Paths_DefaultBesideStorageAndProject_RejectRecursiveBuildsAndKeepPortableSavesLocal()
    {
        Assert.Equal("C:\\work\\output\\Game", ProjectBuildSettings.DefaultOutput("C:\\work\\projects\\Game", "C:\\work\\projects"));
        Assert.Equal("C:\\work\\output\\Game", ProjectBuildSettings.DefaultOutput("C:\\work\\Game"));
        foreach (string output in new[] { "C:\\", "C:\\work", "C:\\work\\Game", "C:\\work\\Game\\output" }) Assert.ThrowsAny<Exception>(() => GameBuildExporter.ValidateOutput("C:\\work\\Game", output));
        Assert.Equal("C:\\output", GameBuildExporter.ValidateOutput("C:\\work\\Game", "C:\\output"));
        string id = Guid.NewGuid().ToString("N"); var game = new PlayerManifest(id, "T", "local", "portable", "A", "", "1.0");
        Assert.Equal("C:\\game\\saves", game.SavesDirectory("C:\\game", "C:\\profile"));
        Assert.Equal($"C:\\profile\\KNE\\games\\{id}\\saves", (game with { SaveLocation = "appdata" }).SavesDirectory("C:\\game", "C:\\profile"));
    }

    [Fact]
    public void ArchiveExtraction_RejectsTraversalAndDuplicateFiles()
    {
        using var fixture = new TempProject(); using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true)) { using var text = new StreamWriter(zip.CreateEntry("../outside.txt").Open()); text.Write("bad"); }
        data.Position = 0; using var read = new ZipArchive(data); Assert.Throws<ArgumentException>(() => GameBuildExporter.ExtractZip(read, fixture.ProjectPath, default)); Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public async Task Export_RefusesForeignDestinationWithoutChangingFiles()
    {
        using var fixture = new TempProject(); string output = Path.Combine(fixture.Root, "output"); Directory.CreateDirectory(Path.Combine(output, "release-self_contained")); string sentinel = Path.Combine(output, "release-self_contained", "user.txt"); await File.WriteAllTextAsync(sentinel, "keep");
        var settings = new ProjectBuildSettings { GameId = Guid.NewGuid().ToString("N") };
        var project = await fixture.LoadAsync();
        await Assert.ThrowsAsync<IOException>(() => new GameBuildExporter().BuildAsync(project, settings, output));
        Assert.Equal("keep", File.ReadAllText(sentinel)); Assert.Single(Directory.EnumerateDirectories(output));
    }

    [Fact]
    public async Task ContentCopy_ExcludesProgressAndPrivateConfiguration_PreservesPluginAssetsAndOriginalFiles()
    {
        using var fixture = new TempProject();
        var performance = PerformanceSettingsStore.LoadProject(fixture.ProjectPath); performance.SavesPath = "custom-progress"; await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, performance);
        string before = File.ReadAllText(Path.Combine(fixture.ProjectPath, "engine", "performance.json"));
        foreach (string file in new[] { "custom-progress/slot.json", "saves/slot.json", ".GIT/config", ".kne/private.txt", ".agents/private.txt" }) fixture.Write(file, "private");
        await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new()); fixture.Write(".kne/plugin-assets/image.png", "asset"); fixture.Write("game/assets/obj/model.obj", "model");
        string target = Path.Combine(fixture.Root, "content"); Directory.CreateDirectory(target);
        await GameBuildExporter.CopyProjectAsync(await fixture.LoadAsync(), target, default);
        foreach (string file in new[] { "custom-progress/slot.json", "saves/slot.json", ".GIT/config", ".kne/private.txt", ".agents/private.txt", "engine/build.json" }) Assert.False(File.Exists(Path.Combine(target, file)), file);
        Assert.Equal("asset", File.ReadAllText(Path.Combine(target, ".kne", "plugin-assets", "image.png"))); Assert.Equal("model", File.ReadAllText(Path.Combine(target, "game", "assets", "obj", "model.obj")));
        Assert.Equal("saves", PerformanceSettingsStore.LoadProject(target).SavesPath); Assert.Equal(before, File.ReadAllText(Path.Combine(fixture.ProjectPath, "engine", "performance.json"))); Assert.Equal("private", File.ReadAllText(Path.Combine(fixture.ProjectPath, "custom-progress", "slot.json")));
    }

    [ExportSmokeTheory]
    [InlineData("framework_dependent")]
    [InlineData("self_contained")]
    [InlineData("single_file")]
    [InlineData("portable")]
    [InlineData("msix")]
    public async Task Publish_RealPlayerRunsWithoutEditorAndKeepsExeMetadataAndSavesPolicy(string mode)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main()\n include_cs('game/plugins/smoke.cs')\n d(nil, state.exported)\nend");
        fixture.Write("game/plugins/smoke.cs", "using KNE; public sealed class ExportPlugin : IKnePlugin { public void Execute(IPluginContext context) { context.Set(\"exported\", \"exported game\"); } }");
        fixture.Write("saves/private.json", "developer-progress"); fixture.Write(".kne/private.txt", "private"); fixture.Write(".git/config", "private"); fixture.Write(".kne/plugin-assets/resource.txt", "asset");
        var settings = new ProjectBuildSettings { GameId = Guid.NewGuid().ToString("N"), ReleaseMode = mode, Author = "Автор $(UNDEFINED); 100%", Team = "Команда", Version = "2.3.4", ReleasePath = Path.Combine(fixture.Root, "output") };
        await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, settings);
        var logs = new List<string>();
        GameBuildResult built;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { built = await new GameBuildExporter().BuildAsync(await fixture.LoadAsync(), settings, settings.ReleasePath, log: line => { lock (logs) logs.Add(line); }, token: timeout.Token); }
        catch (Exception error) { throw new Exception(error + "\n" + string.Join("\n", logs)); }
        var metadata = FileVersionInfo.GetVersionInfo(built.Executable);
        Assert.Equal("2.3.4.0", metadata.FileVersion); Assert.Equal(settings.Author, metadata.LegalCopyright); Assert.Equal("Команда", metadata.CompanyName); Assert.Equal("Новелла с пробелами", metadata.ProductName);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(built.Executable)!, "project", "saves", "private.json")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(built.Executable)!, "project", "engine", "build.json")));
        if (mode == "single_file") Assert.Single(Directory.GetFiles(Path.GetDirectoryName(built.Executable)!));
        else { Assert.NotNull(built.Package); using var zip = ZipFile.OpenRead(built.Package!); Assert.Contains(zip.Entries, entry => entry.FullName == "Game.exe"); Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("private", StringComparison.Ordinal)); }
        Assert.Equal(mode != "framework_dependent" && mode != "single_file", File.Exists(Path.Combine(Path.GetDirectoryName(built.Executable)!, "coreclr.dll")));
        var info = new ProcessStartInfo(built.Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true }; info.ArgumentList.Add("--verify-player"); info.Environment.Remove(RemoteDebugConnection.EnvironmentName);
        using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(true); throw; }
        Assert.True(process.ExitCode == 0, await stderr); using var result = JsonDocument.Parse(await stdout);
        Assert.Equal("exported game", result.RootElement.GetProperty("Story").GetProperty("Text").GetString());
        Assert.Equal("Новелла с пробелами", result.RootElement.GetProperty("Title").GetString());
        Assert.Equal("kne-game:" + settings.GameId, result.RootElement.GetProperty("SaveIdentity").GetString());
        string saves = result.RootElement.GetProperty("Saves").GetString()!;
        if (mode == "portable") Assert.Equal(Path.Combine(Path.GetDirectoryName(built.Executable)!, "saves"), saves);
        else Assert.Equal(new PlayerManifest(settings.GameId, "", "appdata", mode, "", "", "").SavesDirectory("unused"), saves);
    }
}

internal sealed class ExportSmokeTheoryAttribute : TheoryAttribute
{
    public ExportSmokeTheoryAttribute() { if (Environment.GetEnvironmentVariable("KNE_EXPORT_SMOKE") != "1") Skip = "Opt-in: publish real Windows x64 players with KNE_EXPORT_SMOKE=1."; }
}
