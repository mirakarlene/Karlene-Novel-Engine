using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class GameSaveTests
{
    [Fact]
    public async Task SaveReload_RestoresChoicePositionStateAndFutureBranch()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.score = 0
                d(nil, "First")
                local answer = choice("Choose", {"Left", "Right"})
                state.score = answer * 10
                state.name = "Алиса\n\"текст\""
                state.flags = { seen = true }
                d(nil, "Checkpoint")
                d(nil, "Result: " .. tostring(state.score))
            end
            """);
        var project = await fixture.LoadAsync();
        GameCheckpoint checkpoint;
        await using (var original = new LuaRuntime(project, _ => { }, interactive: true))
        {
            await original.StartAsync();
            await original.AdvanceAsync();
            await original.ChooseAsync(2);
            checkpoint = await original.GetCheckpointAsync();
            Assert.Equal(20, checkpoint.State.GetProperty("score").GetInt32());
        }
        var store = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves"));
        await store.SaveAsync("quick", checkpoint);
        await store.SaveAsync("auto", checkpoint);
        Assert.True(store.Exists("quick"));
        Assert.True(store.Exists("auto"));
        var log = new ConcurrentQueue<string>();
        await using var restored = new LuaRuntime(project, log.Enqueue, interactive: true);
        await restored.RestoreAsync(await store.LoadAsync("quick"));
        Assert.Equal("Checkpoint", restored.Story.Text);
        Assert.Empty(log);
        var state = (await restored.GetCheckpointAsync()).State;
        Assert.Equal("Алиса\n\"текст\"", state.GetProperty("name").GetString());
        Assert.True(state.GetProperty("flags").GetProperty("seen").GetBoolean());
        await restored.AdvanceAsync();
        Assert.Equal("Result: 20", restored.Story.Text);
        Assert.Equal(2, checkpoint.History.Count);
        Assert.Equal(2, Directory.GetFiles(store.DirectoryPath).Length);
    }

    [Fact]
    public async Task Restore_ChangedScriptAndDifferentProject_AreRejectedBeforeExecution()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        await using var candidate = new LuaRuntime(project, _ => { }, interactive: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => candidate.RestoreAsync(checkpoint with { ProjectPath = fixture.Root }));
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'changed') end");
        await Assert.ThrowsAsync<InvalidDataException>(() => candidate.RestoreAsync(checkpoint));
        Assert.Equal("idle", candidate.Story.Kind);
    }

    [Fact]
    public async Task SaveStore_InvalidFileAndSlot_AreRejected()
    {
        using var fixture = new TempProject();
        var store = new GameSaveStore(fixture.ProjectPath, Path.Combine(fixture.Root, "saves"));
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.SlotPath("quick"), "{broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync("quick"));
        Assert.Throws<ArgumentException>(() => store.SlotPath("../outside"));
    }

    [Theory]
    [InlineData("state = nil")]
    [InlineData("state.item = function() end")]
    [InlineData("state[1] = 'numeric key'")]
    [InlineData("state.loop = state")]
    public async Task UnsupportedState_CannotProduceUnusableSave(string assignment)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() " + assignment + "; d(nil, 'Here') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.GetCheckpointAsync());
        Assert.Equal("Here", runtime.Story.Text);
    }

    [Fact]
    public async Task OversizedState_DoesNotOverwritePreviousSave()
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        var store = new GameSaveStore(fixture.ProjectPath, Path.Combine(fixture.Root, "saves"));
        await store.SaveAsync("quick", checkpoint);
        byte[] before = File.ReadAllBytes(store.SlotPath("quick"));
        var oversized = checkpoint with { State = JsonSerializer.SerializeToElement(new { text = new string('a', 8_000_000) }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("quick", oversized));
        Assert.Equal(before, File.ReadAllBytes(store.SlotPath("quick")));
        Assert.Single(Directory.GetFiles(store.DirectoryPath));
    }
}
