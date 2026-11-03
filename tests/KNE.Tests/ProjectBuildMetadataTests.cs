using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBuildMetadataTests
{
    [Fact]
    public void LoadUsesProjectJsonPrecedenceAndMissingFieldFallbackWithoutWritingEitherFile()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/build.json", JsonSerializer.Serialize(new ProjectBuildSettings { Author = "Legacy author", Team = "Legacy team", Version = "2.3.4" }, EngineSettings.JsonOptions));
        string path = Path.Combine(fixture.ProjectPath, "project.json"); var json = Read(path); var metadata = (JsonObject)json["metadata"]!;
        metadata["author"] = "JSON author"; metadata["version"] = "3.4.5"; metadata.Remove("team"); Write(path, json);
        byte[] projectBefore = File.ReadAllBytes(path), buildBefore = File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/build.json"));
        var value = ProjectBuildSettingsStore.Load(fixture.ProjectPath); Assert.Equal("JSON author", value.Author); Assert.Equal("Legacy team", value.Team); Assert.Equal("3.4.5", value.Version);
        Assert.Equal(projectBefore, File.ReadAllBytes(path)); Assert.Equal(buildBefore, File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/build.json")));
        metadata.Remove("version"); metadata.Remove("author"); Write(path, json);
        value = ProjectBuildSettingsStore.Load(fixture.ProjectPath); Assert.Equal("Legacy author", value.Author); Assert.Equal("2.3.4", value.Version);
        metadata["author"] = ""; metadata["team"] = ""; Write(path, json);
        value = ProjectBuildSettingsStore.Load(fixture.ProjectPath); Assert.Equal("", value.Author); Assert.Equal("", value.Team);
    }

    [Fact]
    public Task WindowReadsExternalJsonOnReopen_AndSaveMirrorsMetadataPreservingUnknownFieldsAndFreshEdits() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "First", Team = "Team", Version = "2.3.4" });
        var project = await fixture.LoadAsync(); string path = Path.Combine(fixture.ProjectPath, "project.json");
        var json = Read(path); json["custom"] = new JsonObject { ["values"] = new JsonArray(1, 2, 3) }; json["metadata"]!["custom"] = "keep"; json["metadata"]!["author"] = "External"; json["metadata"]!["team"] = "External team"; json["metadata"]!["version"] = "4.5.6"; Write(path, json);
        byte[] before = File.ReadAllBytes(path); var cancel = new ProjectParametersWindow(project);
        try { Assert.Equal("External", cancel.BuildInput.Author.Text); Assert.Equal("4.5.6", cancel.BuildInput.Version.Text); cancel.BuildInput.Author.Text = "Not saved"; }
        finally { cancel.Close(); }
        Assert.Equal(before, File.ReadAllBytes(path));
        var window = new ProjectParametersWindow(project);
        try
        {
            window.BuildInput.Author.Text = "From window"; window.BuildInput.Team.Text = ""; window.BuildInput.Version.Text = "5.6.7";
            json = Read(path); json["external_while_open"] = true; Write(path, json); // Unrelated external fields are merged, not lost.
            await window.SaveAsync();
            var stored = ProjectBuildSettingsStore.Load(fixture.ProjectPath); Assert.Equal("From window", stored.Author); Assert.Equal("", stored.Team); Assert.Equal("5.6.7", stored.Version);
            json = Read(path); Assert.Equal("From window", (string?)json["metadata"]!["author"]); Assert.Equal("", (string?)json["metadata"]!["team"]); Assert.Equal("5.6.7", (string?)json["metadata"]!["version"]);
            Assert.Equal("keep", (string?)json["metadata"]!["custom"]); Assert.Equal(new[] { 1, 2, 3 }, ((JsonArray)json["custom"]!["values"]!).Select(node => (int)node!)); Assert.True((bool)json["external_while_open"]!);
            Assert.Equal("Новелла с пробелами", (string?)json["metadata"]!["name"]); Assert.Equal("game/scenes/main.krl", (string?)json["entry_points"]!["main_scene"]);
            Assert.Equal("From window", project.Configuration.Metadata!.Author); Assert.Equal("5.6.7", project.Configuration.Metadata.Version);
            var mirror = JsonSerializer.Deserialize<ProjectBuildSettings>(File.ReadAllText(Path.Combine(fixture.ProjectPath, "engine/build.json")), EngineSettings.JsonOptions)!;
            Assert.Equal(stored.Author, mirror.Author); Assert.Equal(stored.Team, mirror.Team); Assert.Equal(stored.Version, mirror.Version);
            window.BuildInput.Author.Text = "Second save"; await window.SaveAsync(); Assert.Equal("Second save", ProjectBuildSettingsStore.Load(fixture.ProjectPath).Author);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ConcurrentMetadataEditRejectsSaveAndKeepsDraftAndAllExternalFiles() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "First", Team = "Team", Version = "2.3" });
        var project = await fixture.LoadAsync(); var window = new ProjectParametersWindow(project); string path = Path.Combine(fixture.ProjectPath, "project.json");
        try
        {
            window.BuildInput.Author.Text = "Draft"; window.TitleInput.Text = "New title"; window.SavesInput.Text = "different";
            var json = Read(path); json["metadata"]!["team"] = "External"; Write(path, json);
            byte[] beforeProject = File.ReadAllBytes(path), beforeBuild = File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/build.json")), beforePerformance = File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/performance.json"));
            await Assert.ThrowsAsync<IOException>(window.SaveAsync);
            Assert.Equal(beforeProject, File.ReadAllBytes(path)); Assert.Equal(beforeBuild, File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/build.json"))); Assert.Equal(beforePerformance, File.ReadAllBytes(Path.Combine(fixture.ProjectPath, "engine/performance.json")));
            Assert.Equal("Draft", window.BuildInput.Author.Text); Assert.Equal("New title", window.TitleInput.Text); Assert.Equal("Новелла с пробелами", project.Title);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task FailedPerformanceSaveRollsBackMetadataBuildAndTitleWithOrWithoutPreviousBuild(bool existing) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); if (existing) await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "Before", Version = "2.3" });
        string projectFile = Path.Combine(fixture.ProjectPath, "project.json"), buildFile = Path.Combine(fixture.ProjectPath, "engine/build.json"), performanceFile = Path.Combine(fixture.ProjectPath, "engine/performance.json");
        byte[] projectBefore = File.ReadAllBytes(projectFile), performanceBefore = File.ReadAllBytes(performanceFile); byte[]? buildBefore = File.Exists(buildFile) ? File.ReadAllBytes(buildFile) : null;
        var project = await fixture.LoadAsync(); var window = new ProjectParametersWindow(project);
        try
        {
            window.TitleInput.Text = "Changed title"; window.BuildInput.Author.Text = "Changed author"; window.BuildInput.Version.Text = "9.8"; window.SavesInput.Text = "different";
            using (var locked = new FileStream(performanceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = await Record.ExceptionAsync(window.SaveAsync); Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file error.");
            }
            Assert.Equal(projectBefore, File.ReadAllBytes(projectFile)); Assert.Equal(performanceBefore, File.ReadAllBytes(performanceFile)); Assert.Equal(buildBefore, File.Exists(buildFile) ? File.ReadAllBytes(buildFile) : null);
            Assert.Equal("Новелла с пробелами", project.Title); Assert.Equal("Changed author", window.BuildInput.Author.Text); Assert.Empty(Directory.EnumerateFiles(fixture.ProjectPath, "*.tmp", SearchOption.AllDirectories));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedProjectJsonReplacementRestoresPreviousBuild_AndCancellationChangesNeitherFile(bool existing)
    {
        using var fixture = new TempProject(); if (existing) await ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "Before" });
        string projectFile = Path.Combine(fixture.ProjectPath, "project.json"), buildFile = Path.Combine(fixture.ProjectPath, "engine/build.json"); byte[] before = File.ReadAllBytes(projectFile); byte[]? buildBefore = File.Exists(buildFile) ? File.ReadAllBytes(buildFile) : null;
        using (var locked = new FileStream(projectFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "Changed" })); Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a locked-file error.");
        }
        Assert.Equal(before, File.ReadAllBytes(projectFile)); Assert.Equal(buildBefore, File.Exists(buildFile) ? File.ReadAllBytes(buildFile) : null);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectBuildSettingsStore.SaveAsync(fixture.ProjectPath, new() { Author = "Cancelled" }, cancellation.Token));
        Assert.Equal(before, File.ReadAllBytes(projectFile)); Assert.Equal(buildBefore, File.Exists(buildFile) ? File.ReadAllBytes(buildFile) : null); Assert.Empty(Directory.EnumerateFiles(fixture.ProjectPath, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BomEncodedProjectJsonIsReadAndMalformedMetadataNeverWritesFiles(bool utf16)
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "project.json"); var json = Read(path); json["metadata"]!["author"] = "Автор";
        await File.WriteAllTextAsync(path, json.ToJsonString(), utf16 ? Encoding.Unicode : new UTF8Encoding(true));
        string build = Path.Combine(fixture.ProjectPath, "engine/build.json");
        await File.WriteAllTextAsync(build, JsonSerializer.Serialize(new ProjectBuildSettings { Team = "Legacy BOM" }, EngineSettings.JsonOptions), utf16 ? Encoding.Unicode : new UTF8Encoding(true));
        byte[] buildBefore = File.ReadAllBytes(build);
        Assert.Equal("Автор", ProjectBuildSettingsStore.Load(fixture.ProjectPath).Author); Assert.Equal("Legacy BOM", ProjectBuildSettingsStore.Load(fixture.ProjectPath).Team);
        json["metadata"]!["author"] = 123; Write(path, json); byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => ProjectBuildSettingsStore.Load(fixture.ProjectPath)); Assert.Equal(before, File.ReadAllBytes(path));
        json["metadata"]!["author"] = "Author"; json["metadata"]!["version"] = "invalid"; Write(path, json);
        Assert.Throws<InvalidDataException>(() => ProjectBuildSettingsStore.Load(fixture.ProjectPath)); Assert.Equal(buildBefore, File.ReadAllBytes(build));
    }

    private static JsonObject Read(string file) => JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    private static void Write(string file, JsonObject json) => File.WriteAllText(file, json.ToJsonString(ProjectConfiguration.JsonOptions));
}
