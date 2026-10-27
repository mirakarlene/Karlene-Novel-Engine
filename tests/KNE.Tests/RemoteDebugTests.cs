using System.IO;
using System.Threading.Channels;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class RemoteDebugTests
{
    [Fact]
    public async Task Krl_BreaksBeforeLine_InspectsValues_StepsAndExcludesPauseFromTimeout()
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main()\n state.count = 1\n state.count = state.count + 1\n d(nil, tostring(state.count))\nend");
        var events = Channel.CreateUnbounded<DebugSnapshot>(); using var debugger = new RuntimeDebugger(snapshot => events.Writer.TryWrite(snapshot));
        debugger.Configure([new("game/scenes/main.krl", 3)]);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(2), interactive: true, debugger: debugger);
        Task started = runtime.StartAsync();
        try
        {
            await Paused(events); debugger.Control("continue");
            var hit = await Paused(events); Assert.Equal(3, hit.Location!.Line); Assert.Equal("game/scenes/main.krl", hit.Location.File); Assert.Null(hit.Location.NodeId);
            Assert.Equal(1, hit.Variables.GetProperty("Globals").GetProperty("state").GetProperty("count").GetInt32()); Assert.False(started.IsCompleted);
            await Task.Delay(2200); Assert.False(started.IsCompleted);
            debugger.Control("step"); var step = await Paused(events); Assert.Equal(4, step.Location!.Line); Assert.Equal(2, step.Variables.GetProperty("Globals").GetProperty("state").GetProperty("count").GetInt32());
            debugger.Control("continue"); await started.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("2", runtime.Story.Text);
        }
        finally { debugger.Control("detach"); }
    }

    [Fact]
    public async Task Graph_BreaksBeforeSelectedBlockAndStepTargetsNextBlock_DisconnectResumes()
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { FunctionName = "child", Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "set" }, new() { Id = "set", Kind = ScriptNodeKind.SetVariable, Variable = "count", Value = "7", Next = "say" }, new() { Id = "say", Kind = ScriptNodeKind.Dialogue, Text = "{count}", Next = "end" }, new() { Id = "end", Kind = ScriptNodeKind.End }] };
        fixture.Write("game/scenes/child.knegraph", graph.Serialize()); fixture.Write("game/scenes/main.krl", "function main() call_scene(child) end");
        var events = Channel.CreateUnbounded<DebugSnapshot>(); using var debugger = new RuntimeDebugger(snapshot => events.Writer.TryWrite(snapshot)); debugger.Configure([new("game/scenes/child.knegraph", NodeId: "set")]);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, debugger: debugger);
        Task started = runtime.StartAsync();
        try
        {
            await Paused(events); debugger.Control("continue"); var hit = await Paused(events); Assert.Equal("set", hit.Location!.NodeId); Assert.Equal("game/scenes/child.knegraph", hit.Location.File); Assert.False(hit.Variables.GetProperty("Globals").GetProperty("state").TryGetProperty("count", out _));
            debugger.Control("step"); var step = await Paused(events); Assert.Equal("say", step.Location!.NodeId); Assert.Equal(7, step.Variables.GetProperty("Globals").GetProperty("state").GetProperty("count").GetInt32());
            debugger.Control("detach"); await started.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("7", runtime.Story.Text);
        }
        finally { debugger.Control("detach"); }
    }

    [Fact]
    public async Task JsonPipe_RealWorkerBreakpointsPauseAndDisconnectKeepGameRunnable()
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main()\n state.value = 4\n d(nil, 'first')\n d(nil, 'second')\nend");
        await using var connection = new RemoteDebugConnection(); var events = Channel.CreateUnbounded<DebugSnapshot>(); connection.Changed += snapshot => events.Writer.TryWrite(snapshot);
        await connection.SetBreakpointsAsync([new("game/scenes/main.krl", 3)]);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(2), debuggerPipe: connection.PipeName);
        Task started = session.StartAsync();
        await Paused(events); await connection.SendAsync("continue"); var hit = await Paused(events); Assert.Equal(3, hit.Location!.Line); Assert.Equal(4, hit.Variables.GetProperty("Globals").GetProperty("state").GetProperty("value").GetInt32());
        await Task.Delay(2200); Assert.False(started.IsCompleted);
        await connection.DisposeAsync(); await started.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("first", session.Story.Text);
        await session.AdvanceAsync(); Assert.Equal("second", session.Story.Text);
    }

    [Fact]
    public void Breakpoints_ValidateKindsPathsAndCapacity_AndDetachDisablesStops()
    {
        using var debug = new RuntimeDebugger(_ => { });
        foreach (var point in new[] { new DebugBreakpoint("../escape.krl", 1), new("scene.lua", 1), new("scene.krl", 0), new("scene.krl", NodeId: "node"), new("scene.knegraph", 1), new("scene.knegraph", NodeId: "") }) Assert.Throws<InvalidDataException>(() => debug.Configure([point]));
        Assert.Throws<InvalidDataException>(() => debug.Configure(Enumerable.Range(1, 1001).Select(i => new DebugBreakpoint("a.krl", i)).ToArray()));
        debug.Configure([new("scene.krl", 2), new("scene.knegraph", NodeId: "target")]); debug.Control("continue");
        Assert.True(debug.ShouldPause("SCENE.KRL", 2, "")); Assert.False(debug.ShouldPause("scene.krl", 3, "")); Assert.True(debug.ShouldPause("scene.knegraph", 99, "target")); Assert.False(debug.ShouldPause("scene.knegraph", 99, ""));
        debug.Control("pause"); Assert.True(debug.ShouldPause("scene.krl", 999, "")); debug.Control("detach"); Assert.False(debug.ShouldPause("scene.krl", 2, "")); Assert.Throws<InvalidDataException>(() => debug.Control("eval"));
    }

    private static async Task<DebugSnapshot> Paused(Channel<DebugSnapshot> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { var value = await events.Reader.ReadAsync(timeout.Token); if (value.Paused) return value; }
    }
}
