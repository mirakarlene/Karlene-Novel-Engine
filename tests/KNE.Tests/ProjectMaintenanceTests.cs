using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectMaintenanceTests
{
    [Fact]
    public async Task Rename_UpdatesOnlyDisplayNamesAndPreservesFolderEntryPointsAndManifestExtensionData()
    {
        using var fixture = new TempProject();
        string manifest = Path.Combine(fixture.ProjectPath, "project.json");
        var json = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        json["custom_root"] = new JsonObject { ["keep"] = 73 };
        json["metadata"]!["custom_metadata"] = new JsonArray("one", "two");
        json["game_info"]!["custom_game"] = true; json["entry_points"]!["custom_entry"] = "retained";
        File.WriteAllText(manifest, json.ToJsonString());
        string entryPoints = json["entry_points"]!.ToJsonString();
        string version = json["metadata"]!["version"]!.GetValue<string>();
        var project = await fixture.LoadAsync(); string path = project.Path;
        string scene = File.ReadAllText(project.MainScenePath);
        await ProjectMaintenance.RenameAsync(project, "  Новое имя: глава / 2  ");
        var renamed = JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal(path, project.Path); Assert.True(Directory.Exists(path));
        Assert.Equal("Новое имя: глава / 2", project.Title); Assert.Equal("Новое имя: глава / 2", project.Name);
        Assert.Equal("Новое имя: глава / 2", renamed["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal("Новое имя: глава / 2", renamed["game_info"]!["title"]!.GetValue<string>());
        Assert.Equal(73, renamed["custom_root"]!["keep"]!.GetValue<int>());
        Assert.Equal(new[] { "one", "two" }, renamed["metadata"]!["custom_metadata"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.True(renamed["game_info"]!["custom_game"]!.GetValue<bool>());
        Assert.Equal(entryPoints, renamed["entry_points"]!.ToJsonString());
        Assert.Equal(version, renamed["metadata"]!["version"]!.GetValue<string>());
        Assert.Equal(scene, File.ReadAllText(project.MainScenePath)); Assert.Equal(project.Title, (await fixture.LoadAsync()).Title);
        Assert.Empty(Directory.GetFiles(fixture.ProjectPath, "*.tmp"));
    }

    [Fact]
    public async Task Rename_CancellationPreservesManifestAndLoadedNames()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string manifest = Path.Combine(project.Path, "project.json"); byte[] before = File.ReadAllBytes(manifest); string title = project.Title;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectMaintenance.RenameAsync(project, "Cancelled title", canceled.Token));
        Assert.Equal(before, File.ReadAllBytes(manifest)); Assert.Equal(title, project.Title); Assert.Equal(title, project.Name);
        Assert.Empty(Directory.GetFiles(project.Path, "*.tmp"));
    }

    [Fact]
    public async Task Delete_RemovesOnlyTheLoadedOwnedTemporaryProjectAndLeavesSiblingAndExternalSavesUntouched()
    {
        using var fixture = new TempProject();
        string projectRoot = Path.GetFullPath(fixture.ProjectPath);
        Assert.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, projectRoot, StringComparison.OrdinalIgnoreCase);
        string sibling = Path.Combine(fixture.Root, "sibling", "keep.txt"), external = Path.Combine(fixture.Root, "external-saves", "manual.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!); File.WriteAllText(sibling, "sibling content");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!); File.WriteAllText(external, "external content");
        await PerformanceSettingsStore.SaveProjectAsync(projectRoot, new() { SavesPath = Path.GetDirectoryName(external)! });
        fixture.Write("saves/owned.json", "owned save"); fixture.Write("nested/deep/content.txt", "owned content");
        var project = await fixture.LoadAsync();
        await ProjectMaintenance.DeleteAsync(project);
        Assert.False(Directory.Exists(projectRoot)); Assert.True(Directory.Exists(fixture.Root));
        Assert.Equal("sibling content", File.ReadAllText(sibling)); Assert.Equal("external content", File.ReadAllText(external));
    }

    [Fact]
    public async Task Delete_CancellationDoesNotRemoveAnyProjectFile()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var before = Directory.GetFiles(project.Path, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(project.Path, file), File.ReadAllBytes);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectMaintenance.DeleteAsync(project, canceled.Token));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(project.Path, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(project.Path, file)).Order());
        foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(project.Path, item.Key)));
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("line\nbreak")] [InlineData("tab\tname")]
    public async Task Rename_RejectsBlankOrControlCharactersWithoutChangingManifestOrLoadedNames(string name)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string manifest = Path.Combine(project.Path, "project.json"); byte[] before = File.ReadAllBytes(manifest); string title = project.Title;
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectMaintenance.RenameAsync(project, name));
        Assert.Equal(before, File.ReadAllBytes(manifest)); Assert.Equal(title, project.Title); Assert.Equal(title, project.Name);
    }

    [Fact]
    public async Task Rename_AcceptsThe128CharacterBoundaryAndRejectsTheNextCharacter()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string title = new('я', 128); await ProjectMaintenance.RenameAsync(project, " " + title + " ");
        Assert.Equal(title, project.Title); Assert.Equal(title, (await fixture.LoadAsync()).Name);
        string manifest = Path.Combine(project.Path, "project.json"); byte[] before = File.ReadAllBytes(manifest);
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectMaintenance.RenameAsync(project, title + "x"));
        Assert.Equal(before, File.ReadAllBytes(manifest)); Assert.Equal(title, project.Title);
    }

    [Fact]
    public async Task Rename_LegacyManifestCreatesModernNamesAndRetainsUnknownFields()
    {
        using var fixture = new TempProject();
        string manifest = fixture.Write("project.json", "{\"name\":\"Legacy\",\"plugin\":{\"enabled\":true}}");
        var project = await fixture.LoadAsync(); await ProjectMaintenance.RenameAsync(project, "Modern");
        var json = JsonNode.Parse(File.ReadAllText(manifest))!;
        Assert.Equal("Modern", json["name"]!.GetValue<string>());
        Assert.Equal("Modern", json["metadata"]!["name"]!.GetValue<string>());
        Assert.Equal("Modern", json["game_info"]!["title"]!.GetValue<string>());
        Assert.True(json["plugin"]!["enabled"]!.GetValue<bool>()); Assert.Equal("Modern", project.Title);
    }

    [Theory]
    [InlineData("missing")] [InlineData("malformed")] [InlineData("null")] [InlineData("renamed")]
    public async Task Delete_MissingMalformedOrChangedManifestCannotRemoveTheOwnedProject(string state)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string manifest = Path.Combine(project.Path, "project.json"), sentinel = fixture.Write("keep.txt", "preserved");
        if (state == "missing") File.Delete(manifest);
        else if (state == "renamed")
        {
            var json = JsonNode.Parse(File.ReadAllText(manifest))!; json["metadata"]!["name"] = "Changed elsewhere";
            File.WriteAllText(manifest, json.ToJsonString());
        }
        else File.WriteAllText(manifest, state == "null" ? "null" : "{");
        if (state == "missing") await Assert.ThrowsAsync<FileNotFoundException>(() => ProjectMaintenance.DeleteAsync(project));
        else if (state == "renamed") await Assert.ThrowsAsync<IOException>(() => ProjectMaintenance.DeleteAsync(project));
        else await Assert.ThrowsAsync<InvalidDataException>(() => ProjectMaintenance.DeleteAsync(project));
        Assert.True(Directory.Exists(project.Path)); Assert.Equal("preserved", File.ReadAllText(sentinel));
        Assert.Equal(state != "missing", File.Exists(manifest));
    }

    [Fact]
    public async Task Delete_RejectsAJunctionBeforeRemovingAnyOwnedOrLinkedContent()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string outside = Path.Combine(fixture.Root, "outside"), link = Path.Combine(project.Path, "linked");
        Directory.CreateDirectory(outside); string sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "external");
        string owned = fixture.Write("owned.txt", "owned");
        Assert.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, Path.GetFullPath(link), StringComparison.OrdinalIgnoreCase);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => ProjectMaintenance.DeleteAsync(project));
            Assert.Equal("external", File.ReadAllText(sentinel)); Assert.Equal("owned", File.ReadAllText(owned));
            Assert.True(File.Exists(Path.Combine(project.Path, "project.json")));
        }
        finally { Directory.Delete(link); }
    }
}
