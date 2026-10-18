using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class TriggerAndSubsystemRuntimeTests
{
    [Fact]
    public async Task Trigger_CompletedCallSubscriptionsDoNotConsumeTheActiveSceneLimit()
    {
        using var fixture = new FlowFixture();
        fixture.Write("game/scenes/main.krl", """
            function child()
                kne_on_event('{"kind":"key","keys":"E"}', function() state.stale = true end)
            end
            function main()
                for i = 1, 513 do call_scene(child) end
                kne_on_event('{"kind":"key","keys":"C"}', function() state.current = true end)
                d(nil, "done")
            end
            """);
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal("done", runtime.Story.Text);
        Assert.Equal("C", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.TriggerAsync(new TriggerSignal("key", "E"));
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        await runtime.TriggerAsync(new TriggerSignal("key", "C"));
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.True(state.GetProperty("current").GetBoolean());
        Assert.False(state.TryGetProperty("stale", out _));
        Assert.Equal("done", runtime.Story.Text);
    }

    [Fact]
    public async Task Trigger_ActiveSceneSubscriptionLimitStillRejectsThe513thLiveHandler()
    {
        using var fixture = new FlowFixture();
        fixture.Scene("""
            for i = 1, 513 do
                kne_on_event('{"kind":"key","keys":"E"}', function() end)
            end
            d(nil, "unreachable")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("512 триггеров", error.ToString());
    }

    [Fact]
    public async Task TextClosed_ClearTextAutomaticallyFiresOnce_AndReplaysWithoutAnExtraPlayerAction()
    {
        using var fixture = new FlowFixture();
        fixture.Scene("""
            state.hits=0
            kne_on_event('{"kind":"text_closed","once":true}', function() state.hits=state.hits+1 end)
            clear_text(false,0)
            d(nil,'main:'..state.hits)
            """);
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project); await runtime.StartAsync();
        Assert.Equal("main:1", runtime.Story.Text); Assert.Empty(runtime.Story.Triggers);
        var checkpoint = await runtime.GetCheckpointAsync(); Assert.Empty(checkpoint.History);
        Assert.Equal(1, checkpoint.State.GetProperty("hits").GetInt32());
        await using var replay = Runtime(project); await replay.RestoreAsync(checkpoint);
        Assert.Equal("main:1", replay.Story.Text); Assert.Empty(replay.Story.Triggers);
        Assert.Equal(1, (await replay.GetCheckpointAsync()).State.GetProperty("hits").GetInt32());
    }

    [Fact]
    public async Task Subsystem_FailureAfterTerminalInGeneratedOrder_RetainsTheNestedBlockFrameIdentity()
    {
        using var fixture = new FlowFixture();
        var child = new VisualScript { FunctionName = "child", Nodes = [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "a" },
            new() { Id = "a", Kind = ScriptNodeKind.Dialogue, Text = "nested", Next = "z" },
            new() { Id = "z", Kind = ScriptNodeKind.Background, Path = "missing.png", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End, EndAction = ScriptEndAction.Menu }] };
        fixture.Write("game/scenes/child.knegraph", child.Serialize());
        fixture.Scene("kne_subsystem('game/scenes/child.knegraph',true)");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal("nested", runtime.Story.Text);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/child.knegraph", report.File);
        Assert.Equal("z", report.Frame!.BlockId);
        Assert.True(report.Frame.SceneFrameId > 0); Assert.True(report.Frame.GlobalFrameId > 0);
    }

    [Theory]
    [InlineData(ScriptNodeKind.TriggerKey)]
    [InlineData(ScriptNodeKind.TriggerButton)]
    [InlineData(ScriptNodeKind.TriggerHotspot)]
    [InlineData(ScriptNodeKind.TriggerTextClosed)]
    [InlineData(ScriptNodeKind.TriggerCombination)]
    public async Task TriggerGraph_RegistersDisconnectedRootsAndMatchesSignalsWithoutAdvancingMain(ScriptNodeKind kind)
    {
        using var fixture = new FlowFixture();
        var graph = VisualScript.Create();
        graph.Nodes.Single(node => node.Id == "dialogue").Text = "main";
        var trigger = BlockLibrary.Create(kind); trigger.Id = "trigger"; trigger.Next = "handler";
        graph.Nodes.Add(trigger);
        graph.Nodes.Add(new() { Id = "handler", Kind = ScriptNodeKind.ConsoleWrite, Text = "handled", Next = "handlerend" });
        graph.Nodes.Add(new() { Id = "handlerend", Kind = ScriptNodeKind.End });
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadGraphAsync(graph), output.Enqueue, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(trigger.Trigger, Assert.Single(runtime.Story.Triggers));
        Assert.Equal("main", runtime.Story.Text);
        long presentation = runtime.Story.PresentationId;
        await runtime.TriggerAsync(new TriggerSignal("button", Button: "unmatched"));
        Assert.DoesNotContain("handled", output);
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        var signal = kind switch
        {
            ScriptNodeKind.TriggerButton => new TriggerSignal("button", Button: "NEXT"),
            ScriptNodeKind.TriggerHotspot => new TriggerSignal("hotspot", X: 50, Y: 50),
            ScriptNodeKind.TriggerTextClosed => new TriggerSignal("text_closed"),
            ScriptNodeKind.TriggerCombination => new TriggerSignal("key", "e+CONTROL"),
            _ => new TriggerSignal("key", "e")
        };
        await runtime.TriggerAsync(signal);
        Assert.Single(output, line => line == "handled");
        Assert.Equal("main", runtime.Story.Text);
        Assert.Equal(presentation, runtime.Story.PresentationId);
        Assert.Single(runtime.Story.Triggers);
        Assert.Equal("trigger", Assert.Single((await runtime.GetCheckpointAsync()).History).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trigger_ActiveHandlerCannotReenter_ThenRepeatOrOncePreservesMainDialogue(bool once)
    {
        using var fixture = new FlowFixture();
        string specification = JsonSerializer.Serialize(new TriggerSettings { Keys = "Ctrl+E", Once = once }, EngineSettings.JsonOptions);
        fixture.Scene($$"""
            state.hits = 0
            kne_on_event([=[{{specification}}]=], function()
                state.hits = state.hits + 1
                d(nil, "handler:" .. state.hits)
            end)
            d(nil, "main")
            d(nil, "after")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        long mainPresentation = runtime.Story.PresentationId;
        await runtime.TriggerAsync(new TriggerSignal("key", "E+Control"));
        Assert.Equal("handler:1", runtime.Story.Text);
        Assert.Empty(runtime.Story.Triggers);
        int history = (await runtime.GetCheckpointAsync()).History.Count;
        await runtime.TriggerAsync(new TriggerSignal("key", "Ctrl+E"));
        Assert.Equal("handler:1", runtime.Story.Text);
        Assert.Equal(history, (await runtime.GetCheckpointAsync()).History.Count);
        await runtime.AdvanceAsync();
        Assert.Equal("main", runtime.Story.Text);
        Assert.Equal(mainPresentation, runtime.Story.PresentationId);
        Assert.Equal(once ? 0 : 1, runtime.Story.Triggers.Count);
        await runtime.TriggerAsync(new TriggerSignal("key", "Ctrl+E"));
        if (!once)
        {
            Assert.Equal("handler:2", runtime.Story.Text);
            await runtime.AdvanceAsync();
            Assert.Equal("main", runtime.Story.Text);
        }
        Assert.Equal(once ? 1 : 2, (await runtime.GetCheckpointAsync()).State.GetProperty("hits").GetInt32());
        await runtime.AdvanceAsync();
        Assert.Equal("after", runtime.Story.Text);
    }

    [Fact]
    public async Task Trigger_SubscriptionsFollowCallReturnGotoAndEndScopes()
    {
        using var fixture = new FlowFixture();
        fixture.Write("game/scenes/main.krl", """
            function child()
                kne_on_event('{"kind":"key","keys":"C"}', function() state.child = true end)
                d(nil, "child")
            end
            function replacement()
                kne_on_event('{"kind":"key","keys":"R"}', function() state.replacement = true end)
                d(nil, "replacement")
                kne_end("menu", "")
            end
            function main()
                kne_on_event('{"kind":"key","keys":"E"}', function() state.caller = true end)
                call_scene(child)
                d(nil, "caller")
                goto_scene(replacement)
            end
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("child", runtime.Story.Text);
        Assert.Equal(new[] { "E", "C" }, runtime.Story.Triggers.Select(trigger => trigger.Keys));
        await runtime.TriggerAsync(new TriggerSignal("key", "C"));
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("child").GetBoolean());
        Assert.Equal("child", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("caller", runtime.Story.Text);
        Assert.Equal("E", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.AdvanceAsync();
        Assert.Equal("replacement", runtime.Story.Text);
        Assert.Equal("R", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
        Assert.Empty(runtime.Story.Triggers);
        int history = (await runtime.GetCheckpointAsync()).History.Count;
        await runtime.TriggerAsync(new TriggerSignal("key", "R"));
        Assert.Equal(history, (await runtime.GetCheckpointAsync()).History.Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Trigger_WorkerTransportsSubscriptionAndSignal_AndCheckpointReplaysActiveOnceHandler()
    {
        using var fixture = new FlowFixture();
        var subscription = new TriggerSettings { Kind = "hotspot", X = 10, Y = 20, Width = 30, Height = 40, Once = true };
        string specification = JsonSerializer.Serialize(subscription, EngineSettings.JsonOptions);
        fixture.Scene($$"""
            state.hits = 0
            kne_on_event([=[{{specification}}]=], function()
                state.hits = state.hits + 1
                d(nil, "event")
            end)
            d(nil, "main")
            d(nil, "after")
            """);
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal(subscription, Assert.Single(worker.Story.Triggers));
        await worker.TriggerAsync(new TriggerSignal("hotspot", X: 10, Y: 20));
        Assert.Equal("event", worker.Story.Text);
        Assert.Empty(worker.Story.Triggers);
        var checkpoint = JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(await worker.GetCheckpointAsync()))!;
        var action = Assert.Single(checkpoint.History);
        Assert.Equal("trigger", action.Kind);
        Assert.Equal(new TriggerSignal("hotspot", X: 10, Y: 20), JsonSerializer.Deserialize<TriggerSignal>(action.Command!, EngineSettings.JsonOptions));
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(worker.Story.Text, restored.Story.Text);
        Assert.Empty(restored.Story.Triggers);
        await restored.AdvanceAsync();
        Assert.Equal("main", restored.Story.Text);
        Assert.Empty(restored.Story.Triggers);
        await restored.TriggerAsync(new TriggerSignal("hotspot", X: 10, Y: 20));
        Assert.Equal("main", restored.Story.Text);
        Assert.Equal(1, (await restored.GetCheckpointAsync()).State.GetProperty("hits").GetInt32());
        await restored.AdvanceAsync();
        Assert.Equal("after", restored.Story.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Subsystem_WaitOrBackground_UsesPrivateReturnWithoutReplacingItsPublicFunction(bool wait)
    {
        using var fixture = new FlowFixture();
        var child = VisualScript.Create(); child.FunctionName = "child";
        child.Nodes.Single(node => node.Id == "dialogue").Text = "child dialogue";
        child.Nodes.Single(node => node.Id == "end").EndAction = ScriptEndAction.Menu;
        string original = child.Serialize();
        fixture.Write("game/scenes/child.knegraph", original);
        fixture.Scene($$"""
            kne_subsystem("game/scenes/child.knegraph", {{(wait ? "true" : "false")}})
            d(nil, "after subsystem")
            call_scene(child)
            d(nil, "must not return from public menu end")
            """);
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue, interactive: true);
        await runtime.StartAsync();
        if (wait)
        {
            Assert.Equal("child dialogue", runtime.Story.Text);
            await runtime.AdvanceAsync();
        }
        else Assert.Contains("child dialogue", output);
        Assert.Equal("after subsystem", runtime.Story.Text);
        Assert.False(runtime.Story.Completed);
        Assert.False(runtime.Story.HasBackgroundTasks);
        Assert.Equal(original, File.ReadAllText(Path.Combine(fixture.ProjectPath, "game/scenes/child.knegraph")));
        await runtime.AdvanceAsync();
        Assert.Equal("child dialogue", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
        Assert.DoesNotContain("must not return from public menu end", output);
    }

    [Fact]
    public async Task Subsystem_BackgroundPauseAndDialogue_UseLogicalTicksWithoutReplacingForeground()
    {
        using var fixture = new FlowFixture();
        var child = VisualScript.Create(); child.FunctionName = "child";
        child.Nodes[0].Next = "pause";
        child.Nodes.Add(new() { Id = "pause", Kind = ScriptNodeKind.Pause, Duration = 1, Next = "dialogue" });
        child.Nodes.Single(node => node.Id == "dialogue").Text = "background child";
        fixture.Write("game/scenes/child.knegraph", child.Serialize());
        fixture.Scene("kne_subsystem('game/scenes/child.knegraph',false); d(nil,'foreground')");
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("foreground", runtime.Story.Text);
        Assert.True(runtime.Story.NeedsTick);
        Assert.DoesNotContain("background child", output);
        await runtime.TickAsync(.999);
        Assert.DoesNotContain("background child", output);
        await runtime.TickAsync(.001);
        Assert.Contains("background child", output);
        Assert.Equal("foreground", runtime.Story.Text);
        Assert.False(runtime.Story.NeedsTick);
    }

    [Fact]
    public async Task Subsystem_BackgroundChoice_FailsWithAnActionableDiagnostic()
    {
        using var fixture = new FlowFixture();
        var child = new VisualScript { FunctionName = "child", Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "choice" },
            new() { Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Choose", Options = [new() { Text = "One", Target = "end" }] },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        fixture.Write("game/scenes/child.knegraph", child.Serialize());
        fixture.Scene("kne_subsystem('game/scenes/child.knegraph',false); d(nil,'foreground')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Выбор недоступен в фоновой", error.Message);
        Assert.NotNull(GameErrorReport.TryGet(error));
    }

    [Theory]
    [InlineData("game/scenes/missing.knegraph", "не найден")]
    [InlineData("../outside.knegraph", "внутри папки проекта")]
    [InlineData("game/scenes/main.krl", ".knegraph")]
    public async Task Subsystem_InvalidSource_ReportsOriginalCallSite(string path, string diagnostic)
    {
        using var fixture = new FlowFixture();
        fixture.Scene($"kne_subsystem('{path}',true); d(nil,'unreachable')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File);
        Assert.Equal(2, report.Line);
    }

    [Fact]
    public async Task Subsystem_RecursiveCall_StopsAtSceneDepthLimitWithDiagnostic()
    {
        using var fixture = new FlowFixture();
        var child = new VisualScript { FunctionName = "child", Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "recurse" },
            new() { Id = "recurse", Kind = ScriptNodeKind.Subsystem, Path = "game/scenes/child.knegraph", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        fixture.Write("game/scenes/child.knegraph", child.Serialize());
        fixture.Scene("kne_subsystem('game/scenes/child.knegraph',true)");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Глубина вызовов сцен больше 64", error.Message);
        Assert.NotNull(GameErrorReport.TryGet(error));
    }

    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true);

    private sealed class FlowFixture : IDisposable
    {
        private static readonly string Scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kne-flows-triggers-tests"));
        public string Root { get; } = Path.Combine(Scratch, "flow-" + Guid.NewGuid().ToString("N"));
        public string ProjectPath { get; }
        public FlowFixture()
        {
            Directory.CreateDirectory(Root);
            ProjectPath = ProjectCreator.CreateProject("Flows", Root);
        }
        public void Write(string relative, string content)
        {
            string target = PathSafety.ResolveRelative(ProjectPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
        public void Scene(string body) => Write("game/scenes/main.krl", "function main()\n" + body + "\nend");
        public Task<ProjectInfo> LoadAsync() => ProjectLoader.LoadProjectAsync(ProjectPath);
        public async Task<ProjectInfo> LoadGraphAsync(VisualScript graph)
        {
            Write("game/scenes/visual.knegraph", graph.Serialize());
            Write("game/scenes/main.krl", "-- main is exported by visual.knegraph");
            var project = await LoadAsync();
            project.Configuration.EntryPoints!.MainScene = "game/scenes/visual.knegraph";
            return project;
        }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("flow-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
