using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class GameplayRollbackPolicyTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public async Task RollbackDepthBoundsPastPositionsButNeverTruncatesTheFullReplayHistory(int depth)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.value=0; for i=1,6 do state.value=i; d(nil,'position '..i) end end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); var history = new GameRollbackHistory(depth);
        for (int step = 0; step < 5; step++) { history.Push(await runtime.GetCheckpointAsync()); await runtime.AdvanceAsync(); }
        var current = await runtime.GetCheckpointAsync(); var checkpoint = history.Attach(current);
        Assert.Equal(5, checkpoint.History.Count); Assert.Equal("position 6", checkpoint.Frame.Text);
        Assert.Equal(Math.Min(depth, 5), history.Count); Assert.Equal(Math.Min(depth, 5), checkpoint.Rollback!.Count);
        Assert.All(checkpoint.Rollback, previous => Assert.Null(previous.Rollback));
        var restored = new GameRollbackHistory(depth); restored.Restore(checkpoint); Assert.Equal(history.Count, restored.Count);
        for (int expected = 5; restored.Count > 0; expected--)
        {
            var previous = restored.Peek(); Assert.Equal("position " + expected, previous.Frame.Text); Assert.Equal(expected - 1, previous.History.Count);
            await runtime.RestoreAsync(previous); Assert.Equal(expected, (await runtime.GetCheckpointAsync()).State.GetProperty("value").GetInt32());
            restored.Pop();
        }
        Assert.Throws<InvalidOperationException>(() => restored.Peek()); Assert.Throws<InvalidOperationException>(() => restored.Pop());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RollbackLoadedFromSaveRestoresVariablesLocalsRandomSeedAndAllowsAChangedChoice(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                local suffix = math.random(100,999)
                state.score = 0
                d(nil,'seed:'..suffix)
                local answer = choice('Choose', {'Left','Right'})
                state.score = answer * 10
                d(nil,'score:'..state.score..':'..suffix)
                local second = choice('Second', {'One','Two'})
                state.score = state.score + second
                d(nil,'final:'..state.score..':'..suffix)
            end
            """);
        var project = await fixture.LoadAsync();
        await using IGameSession original = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await original.StartAsync(); string suffix = original.Story.Text[5..];
        var past = new GameRollbackHistory(2); past.Push(await original.GetCheckpointAsync()); await original.AdvanceAsync();
        past.Push(await original.GetCheckpointAsync()); await original.ChooseAsync(2);
        past.Push(await original.GetCheckpointAsync()); await original.AdvanceAsync();
        Assert.Equal("choice", original.Story.Kind);
        var store = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots"));
        await store.SaveAsync("quick", past.Attach(await original.GetCheckpointAsync())); var saved = await store.LoadAsync("quick");
        Assert.Equal(3, saved.History.Count); Assert.Equal(2, saved.Rollback!.Count);
        var restoredPast = new GameRollbackHistory(2); restoredPast.Restore(saved);
        await using IGameSession restored = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(saved); Assert.Equal("choice", restored.Story.Kind);
        await restored.RestoreAsync(restoredPast.Peek()); restoredPast.Pop();
        Assert.Equal("score:20:" + suffix, restored.Story.Text); Assert.Equal(20, (await restored.GetCheckpointAsync()).State.GetProperty("score").GetInt32());
        await restored.RestoreAsync(restoredPast.Peek()); restoredPast.Pop(); Assert.Equal("choice", restored.Story.Kind);
        restoredPast.Push(await restored.GetCheckpointAsync()); await restored.ChooseAsync(1);
        Assert.Equal("score:10:" + suffix, restored.Story.Text); Assert.Equal(10, (await restored.GetCheckpointAsync()).State.GetProperty("score").GetInt32());
        await restored.AdvanceAsync(); await restored.ChooseAsync(2); Assert.Equal("final:12:" + suffix, restored.Story.Text);
        Assert.Equal(1, restoredPast.Count); Assert.Equal("choice", restoredPast.Peek().Frame.Kind);
        Assert.DoesNotContain(restoredPast.Attach(await restored.GetCheckpointAsync()).Rollback!, previous => previous.Frame.Text.StartsWith("score:20:"));
    }

    [Fact]
    public async Task LoadingWithSmallerDepthRetainsOnlyMostRecentPastPositions()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'one'); d(nil,'two'); d(nil,'three'); d(nil,'four') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true); await runtime.StartAsync();
        var past = new GameRollbackHistory(3);
        for (int index = 0; index < 3; index++) { past.Push(await runtime.GetCheckpointAsync()); await runtime.AdvanceAsync(); }
        var saved = past.Attach(await runtime.GetCheckpointAsync()); var one = new GameRollbackHistory(1); one.Restore(saved);
        Assert.Equal(1, one.Count); Assert.Equal("three", one.Peek().Frame.Text); one.Pop(); Assert.Equal(0, one.Count);
        var disabled = new GameRollbackHistory(0); disabled.Restore(saved); Assert.Equal(0, disabled.Count);
    }

    [Theory]
    [InlineData("project")] [InlineData("fingerprint")] [InlineData("nested")] [InlineData("state")] [InlineData("frame")]
    public async Task MalformedRollbackPositionsAreRejectedBeforeOverwritingAnExistingSlot(string corruption)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true); await runtime.StartAsync();
        var good = await runtime.GetCheckpointAsync(); var invalid = corruption switch
        {
            "project" => good with { ProjectPath = fixture.Root },
            "fingerprint" => good with { Fingerprint = "different" },
            "nested" => good with { Rollback = new[] { good } },
            "state" => good with { State = JsonSerializer.SerializeToElement(new[] { 1, 2 }) },
            _ => good with { Frame = null! }
        };
        var store = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); await store.SaveAsync("quick", good);
        byte[] before = File.ReadAllBytes(store.SlotPath("quick"));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync("quick", good with { Rollback = new[] { invalid } }));
        Assert.Equal(before, File.ReadAllBytes(store.SlotPath("quick")));
    }

    [Theory]
    [InlineData("dialogue", "dialogue", true)] [InlineData("dialogue", "choice", false)] [InlineData("dialogue", "input", false)]
    [InlineData("choice", "choice", true)] [InlineData("choice", "dialogue", false)] [InlineData("choice", "wait", false)]
    public void AutosaveIntervalRespondsOnlyToTheSelectedCompletedAction(string interval, string previousKind, bool expected)
    {
        var policy = new GameAutosavePolicy(); Assert.Equal(expected, policy.Completed(new() { AutoSaveInterval = interval }, previousKind));
    }

    [Fact]
    public void ActionAutosaveRespectsExactCountDisableAndResetWithoutCountingNonActions()
    {
        var policy = new GameAutosavePolicy(); var settings = new GameDefaults { AutoSaveInterval = "actions", AutoSaveActions = 3 };
        Assert.False(policy.Completed(settings, "dialogue")); Assert.False(policy.Completed(settings, "choice")); Assert.True(policy.Completed(settings, "input"));
        settings.AutoSave = false; for (int index = 0; index < 5; index++) Assert.False(policy.Completed(settings, "dialogue"));
        settings.AutoSave = true; Assert.False(policy.Completed(settings, "dialogue")); Assert.False(policy.Completed(settings, "dialogue")); Assert.True(policy.Completed(settings, "choice"));
        policy.Reset(); Assert.False(policy.Completed(settings, "dialogue")); Assert.False(policy.Completed(settings, "dialogue")); Assert.True(policy.Completed(settings, "dialogue"));
    }
}
