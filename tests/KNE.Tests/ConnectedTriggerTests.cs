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
    [InlineData(ScriptNodeKind.TriggerSequence)]
    [InlineData(ScriptNodeKind.TriggerPointerMove)]
    [InlineData(ScriptNodeKind.TriggerPointerEnterZone)]
    [InlineData(ScriptNodeKind.TriggerPointerLeave)]
    [InlineData(ScriptNodeKind.TriggerDoubleClick)]
    [InlineData(ScriptNodeKind.TriggerLongPress)]
    [InlineData(ScriptNodeKind.TriggerWheel)]
    [InlineData(ScriptNodeKind.TriggerFocus)]
    [InlineData(ScriptNodeKind.TriggerResize)]
    [InlineData(ScriptNodeKind.TriggerClipboard)]
    [InlineData(ScriptNodeKind.TriggerGesture)]
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
            ScriptNodeKind.TriggerButton => new("button", Button: "save"),
            ScriptNodeKind.TriggerHotspot => new("hotspot", X: 50, Y: 50),
            ScriptNodeKind.TriggerTextClosed => new("text_closed"),
            ScriptNodeKind.TriggerCombination => new("key", "Ctrl+E"),
            ScriptNodeKind.TriggerSequence => new("sequence", "↑,↑,↓,↓,←,→,←,→,B,A"),
            ScriptNodeKind.TriggerPointerMove => new("pointer_move", X: 20, Y: 30),
            ScriptNodeKind.TriggerPointerEnterZone => new("pointer_enter_zone", X: 50, Y: 50, PreviousX: -1, PreviousY: 50),
            ScriptNodeKind.TriggerPointerLeave => new("pointer_leave"),
            ScriptNodeKind.TriggerDoubleClick => new("double_click", Button: "left", Elapsed: 300),
            ScriptNodeKind.TriggerLongPress => new("long_press", Keys: "E", Elapsed: 5),
            ScriptNodeKind.TriggerWheel => new("wheel", Button: "down"),
            ScriptNodeKind.TriggerFocus => new("focus", Button: "lost"),
            ScriptNodeKind.TriggerResize => new("resize", Button: "finished", X: 900, Y: 600),
            ScriptNodeKind.TriggerClipboard => new("clipboard", Button: "copy"),
            ScriptNodeKind.TriggerGesture => new("gesture", Keys: "circle", Button: "left"),
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
    public void TriggersRejectNearMissesSeparateFromValidInput()
    {
        var zone = new TriggerSettings { Kind = "pointer_enter_zone", X = 100, Y = 100 };
        Assert.False(zone.Matches(new("pointer_enter_zone", X: 120, Y: 120, PreviousX: 101, PreviousY: 101)));
        Assert.True(zone.Matches(new("pointer_enter_zone", X: 120, Y: 120, PreviousX: 99, PreviousY: 101)));
        Assert.False(zone.Matches(new("pointer_enter_zone", X: 200, Y: 100)));
        var clicks = new TriggerSettings { Kind = "double_click", MouseButton = "right" };
        Assert.False(clicks.Matches(new("double_click", Button: "right", Elapsed: 301)));
        Assert.False(clicks.Matches(new("double_click", Button: "left", Elapsed: 150)));
        Assert.True(clicks.Matches(new("double_click", Button: "right", Elapsed: 150)));
        var hold = new TriggerSettings { Kind = "long_press", Keys = "Esc", HoldSeconds = 2 };
        Assert.True(hold.Matches(new("long_press", Keys: "Escape", Elapsed: 2)));
        Assert.False(hold.Matches(new("long_press", Keys: "Escape", Elapsed: 5)));
        Assert.Throws<ArgumentException>(() => new TriggerSettings { Kind = "sequence", Keys = "A,Ctrl+B" }.Validate());
    }

    [Fact]
    public void MouseGestureRecognizesClosedCircleAndDirectionalLineButRejectsJitter()
    {
        var circle = Enumerable.Range(0, 33).Select(index => new System.Windows.Point(
            60 + Math.Cos(index * Math.Tau / 32) * 36, 70 + Math.Sin(index * Math.Tau / 32) * 37)).ToArray();
        Assert.Equal("circle", MouseGesture.Identify(circle));
        Assert.Equal("circle", MouseGesture.Identify(circle.Reverse().ToArray()));
        Assert.Null(MouseGesture.Identify(circle[..18]));
        Assert.Equal("right", MouseGesture.Identify([new(5, 12), new(20, 14), new(36, 11), new(70, 12)]));
        Assert.Null(MouseGesture.Identify([new(5, 5), new(7, 8), new(3, 7), new(9, 3)]));
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
