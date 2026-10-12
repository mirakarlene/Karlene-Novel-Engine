using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectSaveStoreTests
{
    [Fact]
    public async Task ProjectSlots_MigrateLegacyWithoutReplacingFiles_AndListNamedSlots()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync(); var first = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync(); var second = await runtime.GetCheckpointAsync();
        string legacy = Path.Combine(fixture.Root, "legacy");
        var oldStore = new GameSaveStore(project.Path, legacy);
        await oldStore.SaveAsync("quick", first); await oldStore.SaveAsync("auto", first);
        var store = new GameSaveStore(project.Path);
        Assert.Equal(Path.Combine(project.Path, "saves"), store.DirectoryPath);
        await store.SaveAsync("quick", second);
        _ = new GameSaveStore(project.Path, store.DirectoryPath, legacy);
        Assert.True(store.Exists("auto"));
        store.ImportLegacySlots(legacy);
        Assert.Equal(second.Frame.Text, (await store.LoadAsync("quick")).Frame.Text);
        Assert.Equal(first.Frame.Text, (await store.LoadAsync("auto")).Frame.Text);
        Assert.True(oldStore.Exists("quick")); Assert.True(oldStore.Exists("auto"));
        await store.SaveAsync("Глава 1", second);
        File.WriteAllText(Path.Combine(store.DirectoryPath, "preferences.json"), "{}");
        File.WriteAllText(store.SlotPath("broken"), "broken json");
        var slots = await store.ListAsync();
        Assert.Equal(4, slots.Count);
        Assert.Equal(second.Frame.Text, slots.Single(s => s.Slot == "Глава 1").Preview);
        Assert.True(slots.Single(s => s.Slot == "broken").Damaged);
        Assert.DoesNotContain(slots, s => s.Slot == "preferences");
        store.Delete("Глава 1"); Assert.False(store.Exists("Глава 1")); Assert.True(store.Exists("quick"));
        var reloaded = await fixture.LoadAsync();
        Assert.DoesNotContain(reloaded.Files, f => f.FilePath.Contains(Path.DirectorySeparatorChar + "saves" + Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("slot.json")]
    [InlineData("CON")]
    [InlineData("preferences")]
    [InlineData("Preferences")]
    [InlineData("settings")]
    [InlineData("")]
    public void SlotNames_RejectTraversalAndReservedPreferences(string slot)
    {
        using var fixture = new TempProject();
        var store = new GameSaveStore(fixture.ProjectPath);
        Assert.Throws<ArgumentException>(() => store.SlotPath(slot));
    }

    [Fact]
    public async Task MigratedPreview_ImportsOnlyItsOwnSceneSlots()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { }); await runtime.StartAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        var source = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "legacy", "preview", "one"));
        await source.SaveAsync("quick", checkpoint);
        var destination = new GameSaveStore(project.Path, Path.Combine(project.Path, "saves", "preview", "one"), source.DirectoryPath);
        Assert.Equal(checkpoint.Frame.Text, (await destination.LoadAsync("quick")).Frame.Text);
        var sibling = new GameSaveStore(project.Path, Path.Combine(project.Path, "saves", "preview", "two"));
        Assert.False(sibling.Exists("quick"));
    }

    [Fact]
    public void SlotName_LengthBoundary_IsAcceptedAndAdjacentLengthRejected()
    {
        using var fixture = new TempProject(); var store = new GameSaveStore(fixture.ProjectPath);
        Assert.EndsWith(new string('n', 80) + ".json", store.SlotPath(new string('n', 80)));
        Assert.Throws<ArgumentException>(() => store.SlotPath(new string('n', 81)));
    }
}
