using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectProgressSettingsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SaveStore_UsesConfiguredRelativeOrExternalDirectoryWithoutMovingOrDeletingOldProgress(bool external)
    {
        using var fixture = new TempProject();
        var originalStore = new GameSaveStore(fixture.ProjectPath); await originalStore.SaveAsync("quick", Checkpoint(fixture.ProjectPath, "original"));
        byte[] originalBytes = File.ReadAllBytes(originalStore.SlotPath("quick"));
        string target = external ? Path.Combine(fixture.Root, "external-progress") : Path.Combine(fixture.ProjectPath, "custom", "slots");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { SavesPath = external ? target : "custom/slots" });
        var configured = new GameSaveStore(fixture.ProjectPath);
        Assert.Equal(target, configured.DirectoryPath); Assert.False(configured.Exists("quick"));
        await configured.SaveAsync("manual", Checkpoint(fixture.ProjectPath, "custom"));
        Assert.Equal("custom", (await configured.LoadAsync("manual")).Frame.Text);
        Assert.Equal(originalBytes, File.ReadAllBytes(originalStore.SlotPath("quick"))); Assert.False(originalStore.Exists("manual"));
        Assert.Equal(Path.Combine(fixture.ProjectPath, "saves"), originalStore.DirectoryPath);
        string explicitDirectory = Path.Combine(fixture.Root, "explicit-override");
        Assert.Equal(explicitDirectory, new GameSaveStore(fixture.ProjectPath, explicitDirectory).DirectoryPath);
    }

    [Fact]
    public async Task DeleteAllProgress_RemovesOnlyOwnedCheckpointsAndPreservesForeignMalformedReservedAndNestedFiles()
    {
        using var fixture = new TempProject(); using var foreign = new TempProject();
        string directory = Path.Combine(fixture.Root, "shared-progress");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { SavesPath = directory });
        var store = new GameSaveStore(fixture.ProjectPath);
        await store.SaveAsync("quick", Checkpoint(fixture.ProjectPath, "one"));
        await store.SaveAsync("manual", Checkpoint(fixture.ProjectPath.ToUpperInvariant(), "two"));
        await store.SaveAsync("foreign", Checkpoint(foreign.ProjectPath, "belongs elsewhere"));
        var keep = new Dictionary<string, string>
        {
            ["preferences.json"] = JsonSerializer.Serialize(Checkpoint(fixture.ProjectPath, "reserved preferences")),
            ["settings.json"] = JsonSerializer.Serialize(Checkpoint(fixture.ProjectPath, "reserved settings")), ["notes.txt"] = "keep notes",
            ["broken.json"] = "{ damaged", ["unrelated.json"] = "{\"not\":\"a checkpoint\"}",
            ["nested/owned.json"] = JsonSerializer.Serialize(Checkpoint(fixture.ProjectPath, "nested")),
            ["invalid-version.json"] = JsonSerializer.Serialize(Checkpoint(fixture.ProjectPath, "unsupported") with { Version = 2 })
        };
        foreach (var item in keep) { string path = Path.Combine(directory, item.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, item.Value); }
        byte[] foreignBytes = File.ReadAllBytes(store.SlotPath("foreign"));
        Assert.Equal(2, await store.DeleteAllProgressAsync());
        Assert.False(store.Exists("quick")); Assert.False(store.Exists("manual")); Assert.Equal(foreignBytes, File.ReadAllBytes(store.SlotPath("foreign")));
        foreach (var item in keep) Assert.Equal(item.Value, File.ReadAllText(Path.Combine(directory, item.Key)));
        Assert.True(Directory.Exists(directory)); Assert.Equal(0, await store.DeleteAllProgressAsync());
        Assert.Equal(directory, PerformanceSettingsStore.LoadProject(fixture.ProjectPath).ResolveSavesPath(fixture.ProjectPath));
    }

    [Fact]
    public async Task DeleteAllProgress_CancelledEnumerationLeavesEverySaveIntact_AndMissingDirectoryDoesNotGetCreated()
    {
        using var fixture = new TempProject(); string directory = Path.Combine(fixture.Root, "isolated-progress");
        var store = new GameSaveStore(fixture.ProjectPath, directory);
        Assert.Equal(0, await store.DeleteAllProgressAsync()); Assert.False(Directory.Exists(directory));
        await store.SaveAsync("quick", Checkpoint(fixture.ProjectPath, "one")); await store.SaveAsync("auto", Checkpoint(fixture.ProjectPath, "two"));
        var before = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAllProgressAsync(canceled.Token));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory).Select(Path.GetFileName).Order());
        foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(Path.Combine(directory, item.Key!)));
    }

    [Fact]
    public async Task ProjectFileListing_ExcludesConfiguredSaveTreeIgnoringCaseWithoutHidingSiblingProjectFiles()
    {
        using var fixture = new TempProject();
        fixture.Write("progress/slots/manual.json", "private save"); fixture.Write("progress/slots/nested/hidden.krl", "not project code");
        string sibling = fixture.Write("progress/slots-backup/visible.txt", "ordinary project file"), parent = fixture.Write("progress/visible.txt", "ordinary parent file");
        fixture.Write("saves/legacy.json", "old save");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { SavesPath = "PrOgReSs/SlOtS" });
        var project = await fixture.LoadAsync();
        string target = Path.Combine(fixture.ProjectPath, "progress", "slots") + Path.DirectorySeparatorChar;
        Assert.DoesNotContain(project.Files, file => file.FilePath.StartsWith(target, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Files, file => file.FilePath.StartsWith(Path.Combine(fixture.ProjectPath, "saves") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(project.Files, file => file.FilePath == sibling); Assert.Contains(project.Files, file => file.FilePath == parent);
        Assert.Contains(project.Files, file => file.FilePath == Path.Combine(fixture.ProjectPath, "engine", "performance.json"));
        Assert.Equal("private save", File.ReadAllText(Path.Combine(target, "manual.json")));
    }

    [Fact]
    public async Task ResetProgress_DoesNotResurrectLegacyQuickOrAutoWhenTheStoreIsReopened()
    {
        using var fixture = new TempProject(); string directory = Path.Combine(fixture.Root, "current"), legacy = Path.Combine(fixture.Root, "legacy");
        var old = new GameSaveStore(fixture.ProjectPath, legacy);
        await old.SaveAsync("quick", Checkpoint(fixture.ProjectPath, "legacy quick")); await old.SaveAsync("auto", Checkpoint(fixture.ProjectPath, "legacy auto"));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { SavesPath = directory });
        var imported = new GameSaveStore(fixture.ProjectPath, legacyDirectory: legacy);
        Assert.True(imported.Exists("quick")); Assert.True(imported.Exists("auto")); Assert.Equal(2, await imported.DeleteAllProgressAsync());
        var reopened = new GameSaveStore(fixture.ProjectPath, legacyDirectory: legacy);
        Assert.False(reopened.Exists("quick")); Assert.False(reopened.Exists("auto")); Assert.Empty(await reopened.ListAsync());
        Assert.Equal("legacy quick", (await old.LoadAsync("quick")).Frame.Text); Assert.Equal("legacy auto", (await old.LoadAsync("auto")).Frame.Text);
        await reopened.SaveAsync("manual", Checkpoint(fixture.ProjectPath, "new progress"));
        Assert.Equal("new progress", (await new GameSaveStore(fixture.ProjectPath, legacyDirectory: legacy).LoadAsync("manual")).Frame.Text);
    }

    [Fact]
    public async Task CustomSaveDirectory_LegacyImportIsNonDestructiveAndDoesNotReplaceExistingProjectSlot()
    {
        using var fixture = new TempProject(); string target = Path.Combine(fixture.Root, "current"), legacy = Path.Combine(fixture.Root, "legacy");
        var old = new GameSaveStore(fixture.ProjectPath, legacy);
        await old.SaveAsync("quick", Checkpoint(fixture.ProjectPath, "legacy quick")); await old.SaveAsync("auto", Checkpoint(fixture.ProjectPath, "legacy auto"));
        await new GameSaveStore(fixture.ProjectPath, target).SaveAsync("quick", Checkpoint(fixture.ProjectPath, "current quick"));
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { SavesPath = target });
        var imported = new GameSaveStore(fixture.ProjectPath, legacyDirectory: legacy);
        Assert.Equal(target, imported.DirectoryPath); Assert.Equal("current quick", (await imported.LoadAsync("quick")).Frame.Text); Assert.Equal("legacy auto", (await imported.LoadAsync("auto")).Frame.Text);
        Assert.Equal("legacy quick", (await old.LoadAsync("quick")).Frame.Text); Assert.Equal("legacy auto", (await old.LoadAsync("auto")).Frame.Text);
    }

    private static GameCheckpoint Checkpoint(string project, string text) => new(1, project, "test-fingerprint", 7, [], new("dialogue", "", text, []), JsonSerializer.SerializeToElement(new { score = 1 }));
}
