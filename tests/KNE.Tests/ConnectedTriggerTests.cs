using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ConnectedTriggerTests
{
    [Theory]
    [InlineData(ScriptNodeKind.TriggerKey)]
    [InlineData(ScriptNodeKind.TriggerButton)]
    [InlineData(ScriptNodeKind.TriggerHotspot)]
    [InlineData(ScriptNodeKind.TriggerTextClosed)]
    [InlineData(ScriptNodeKind.TriggerCombination)]
    public async Task ConnectedTrigger_WaitsForMatchingEventThenContinuesExactlyOnce_AndCheckpointReplays(ScriptNodeKind kind)
    {
        using var fixture = new Fixture();
        var graph = VisualScript.Create();
        graph.Nodes.Single(node => node.Id == "start").Next = "trigger";
        graph.Nodes.Single(node => node.Id == "dialogue").Text = "after event";
        var trigger = BlockLibrary.Create(kind); trigger.Id = "trigger"; trigger.Next = "dialogue";
        graph.Nodes.Add(trigger);
        fixture.Write("game/scenes/main.krl", "-- visual main");
        fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        var project = await ProjectLoader.LoadProjectAsync(fixture.Project);
        project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.False(runtime.Story.Completed);
        Assert.False(runtime.Story.CanAdvance);
        Assert.DoesNotContain("after event", runtime.Story.Text);
        Assert.Equal(trigger.Trigger, Assert.Single(runtime.Story.Triggers));
        var waiting = await runtime.GetCheckpointAsync();
        await using (var pendingReplay = new LuaRuntime(project, _ => { }, interactive: true))
        {
            await pendingReplay.RestoreAsync(waiting);
            Assert.False(pendingReplay.Story.Completed);
            Assert.Equal(trigger.Trigger, Assert.Single(pendingReplay.Story.Triggers));
            Assert.False(pendingReplay.Story.CanAdvance);
        }
        await runtime.TriggerAsync(new TriggerSignal("button", Button: "unrelated"));
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        Assert.DoesNotContain("after event", runtime.Story.Text);
        TriggerSignal signal = kind switch
        {
            ScriptNodeKind.TriggerButton => new("button", Button: "next"),
            ScriptNodeKind.TriggerHotspot => new("hotspot", X: 50, Y: 50),
            ScriptNodeKind.TriggerTextClosed => new("text_closed"),
            ScriptNodeKind.TriggerCombination => new("key", "Ctrl+E"),
            _ => new("key", "E")
        };
        await runtime.TriggerAsync(signal);
        Assert.Equal("after event", runtime.Story.Text);
        Assert.Empty(runtime.Story.Triggers);
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal("trigger", Assert.Single(checkpoint.History).Kind);
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("after event", restored.Story.Text);
        Assert.Empty(restored.Story.Triggers);
        await restored.TriggerAsync(signal);
        Assert.Single((await restored.GetCheckpointAsync()).History);
        Assert.Equal("after event", restored.Story.Text);
        await restored.AdvanceAsync();
        Assert.True(restored.Story.Completed);
    }

    [Fact]
    public async Task ConnectedTrigger_IsNotArmedUntilBranchReachesIt_AndWaitDoesNotFreezeParallelBranch()
    {
        using var fixture = new Fixture();
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.background=0
                kne_async({function()
                    kne_pause(1)
                    state.background=7
                end})
                d(nil,'before')
                kne_await_event('{"kind":"key","keys":"E"}')
                d(nil,'after:'..state.background)
            end
            """);
        await using var runtime = new LuaRuntime(await ProjectLoader.LoadProjectAsync(fixture.Project), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("before", runtime.Story.Text); Assert.Empty(runtime.Story.Triggers);
        await runtime.TriggerAsync(new TriggerSignal("key", "E"));
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        await runtime.AdvanceAsync();
        Assert.Equal("E", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.TickAsync(1);
        Assert.Equal(7, (await runtime.GetCheckpointAsync()).State.GetProperty("background").GetInt32());
        Assert.Single(runtime.Story.Triggers);
        await runtime.TriggerAsync(new TriggerSignal("key", "E"));
        Assert.Equal("after:7", runtime.Story.Text);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kne-calculations-theme-tests");
        public string Root { get; } = Path.Combine(Scratch, "connected-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public Fixture() { Directory.CreateDirectory(Root); Project = ProjectCreator.CreateProject("Triggers", Root); }
        public void Write(string relative, string content)
        {
            string path = PathSafety.ResolveRelative(Project, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content);
        }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(target, true);
        }
    }
}
