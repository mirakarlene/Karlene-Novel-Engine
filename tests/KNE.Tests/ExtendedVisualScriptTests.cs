using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ExtendedVisualScriptTests
{
    private static ScriptNode Node(string id, ScriptNodeKind kind, string? next = null) => new() { Id = id, Kind = kind, Next = next };

    [Fact]
    public async Task DetachedComment_IsPreservedWithoutExecutingTextOrRequiringAConnection()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        graph.Nodes.Add(new() { Id = "notes", Kind = ScriptNodeKind.Comment, Text = "error('must not execute')", X = -800 });
        var parsed = VisualScript.Parse(graph.Serialize());
        Assert.Equal("error('must not execute')", parsed.Nodes.Single(n => n.Id == "notes").Text);
        Assert.Null(Assert.Single(parsed.Nodes.Single(n => n.Id == "notes").Outputs()).Target);
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, parsed), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Добро пожаловать!", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData("2", "Matched")]
    [InlineData("3", "Default")]
    public async Task Switch_UsesTypedCasesAndRunsContinuationExactlyOnce(string value, string expected)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "set"),
            new() { Id = "set", Kind = ScriptNodeKind.SetVariable, Variable = "route", Value = value, Next = "switch" },
            new() { Id = "switch", Kind = ScriptNodeKind.Switch, Variable = "route", ValueKind = ScriptValueKind.Number,
                Options = [new() { Text = "2", Target = "match" }], False = "default", Next = "after" },
            new() { Id = "match", Kind = ScriptNodeKind.Dialogue, Text = "Matched", Next = "bodyend" },
            new() { Id = "default", Kind = ScriptNodeKind.Dialogue, Text = "Default", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End),
            new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "Continuation", Next = "end" },
            Node("end", ScriptNodeKind.End)
        ] };
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(expected, runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("Continuation", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FlagsAndWhile_SkipFalseConditionOrReevaluateAfterBody_AndReturnToFollowingFrame(bool initial)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "set"),
            new() { Id = "set", Kind = ScriptNodeKind.SetFlag, Variable = "again", Value = initial ? "true" : "false", Next = "loop" },
            new() { Id = "loop", Kind = ScriptNodeKind.While, Variable = "again", ValueKind = ScriptValueKind.Boolean,
                Value = "true", True = "body", Next = "check" },
            new() { Id = "body", Kind = ScriptNodeKind.Dialogue, Text = "Body", Next = "clear" },
            new() { Id = "clear", Kind = ScriptNodeKind.SetFlag, Variable = "again", Value = "false", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End),
            new() { Id = "check", Kind = ScriptNodeKind.CheckFlag, Variable = "again", Value = "false", True = "after", False = "bad" },
            new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "After", Next = "end" },
            new() { Id = "bad", Kind = ScriptNodeKind.Dialogue, Text = "Wrong", Next = "end" },
            Node("end", ScriptNodeKind.End)
        ] };
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        if (initial)
        {
            Assert.Equal("Body", runtime.Story.Text);
            await runtime.AdvanceAsync();
        }
        Assert.Equal("After", runtime.Story.Text);
        Assert.False((await runtime.GetCheckpointAsync()).State.GetProperty("again").GetBoolean());
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEach_PreservesOrderedElementsAndIndexes_AndEmptyArraySkipsBody(bool empty)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", empty ? "state.items = {}" : "state.items = {'first', 'second'}");
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "each"),
            new() { Id = "each", Kind = ScriptNodeKind.ForEach, Path = "items", Variable = "item", IterationKey = "itemIndex", True = "body", Next = "after" },
            new() { Id = "body", Kind = ScriptNodeKind.Dialogue, Text = "Item", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End),
            new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "After", Next = "end" },
            Node("end", ScriptNodeKind.End)
        ] };
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        if (!empty)
        {
            Assert.Equal("Item", runtime.Story.Text);
            var saved = await runtime.GetCheckpointAsync();
            var state = saved.State;
            Assert.Equal("first", state.GetProperty("item").GetString()); Assert.Equal(1, state.GetProperty("itemIndex").GetInt32());
            Assert.Equal(new[] { "first", "second" }, state.GetProperty("items").EnumerateArray().Select(e => e.GetString()));
            await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
            await restored.RestoreAsync(saved);
            Assert.Equal("Item", restored.Story.Text);
            await restored.AdvanceAsync();
            var restoredState = (await restored.GetCheckpointAsync()).State;
            Assert.Equal("second", restoredState.GetProperty("item").GetString()); Assert.Equal(2, restoredState.GetProperty("itemIndex").GetInt32());
            await runtime.AdvanceAsync();
            Assert.Equal("Item", runtime.Story.Text);
            state = (await runtime.GetCheckpointAsync()).State;
            Assert.Equal("second", state.GetProperty("item").GetString()); Assert.Equal(2, state.GetProperty("itemIndex").GetInt32());
            await runtime.AdvanceAsync();
        }
        Assert.Equal("After", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryCatch_RecordsFailureOrYieldsSuccessfulBody_ThenContinuesExactlyOnce(bool fail)
    {
        using var fixture = new TempProject();
        if (!fail) fixture.Write("game/scripts/gamelogic.lua", "state.unset = 2");
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "try"),
            new() { Id = "try", Kind = ScriptNodeKind.TryCatch, Variable = "failure", True = "bad", False = "caught", Next = "after" },
            new() { Id = "bad", Kind = ScriptNodeKind.Condition, Variable = "unset", Value = "1", Comparison = ScriptComparison.Greater, True = "bodyframe", False = "bodyend" },
            new() { Id = "bodyframe", Kind = ScriptNodeKind.Dialogue, Text = "Body", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End),
            new() { Id = "caught", Kind = ScriptNodeKind.Dialogue, Text = "Caught", Next = "catchend" },
            Node("catchend", ScriptNodeKind.End),
            new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "After", Next = "end" },
            Node("end", ScriptNodeKind.End)
        ] };
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(fail ? "Caught" : "Body", runtime.Story.Text);
        var state = (await runtime.GetCheckpointAsync()).State;
        if (fail) Assert.Contains("nil", state.GetProperty("failure").GetString());
        else Assert.False(state.TryGetProperty("failure", out _));
        await runtime.AdvanceAsync(); Assert.Equal("After", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task Random_AssignsWithinTypedRange_AndFixedRangeIsExact()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create(); graph.Nodes[0].Next = "random";
        graph.Nodes.Add(new() { Id = "random", Kind = ScriptNodeKind.Random, Variable = "sample", Minimum = -2.5, Maximum = 3.75, Next = "fixed" });
        graph.Nodes.Add(new() { Id = "fixed", Kind = ScriptNodeKind.Random, Variable = "fixed", Minimum = 7, Maximum = 7, Next = "dialogue" });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.InRange(state.GetProperty("sample").GetDouble(), -2.5, 3.75);
        Assert.Equal(7, state.GetProperty("fixed").GetDouble());
        var saved = await runtime.GetCheckpointAsync();
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(saved);
        Assert.Equal(state.GetProperty("sample").GetDouble(), (await restored.GetCheckpointAsync()).State.GetProperty("sample").GetDouble());
    }

    [Theory]
    [InlineData(-3, 4)]
    [InlineData(0, 1)]
    [InlineData(-2, -1)]
    public async Task Random_IntegerBoundsProduceOnlyIntegers_AndIncludeBothEndpoints(double minimum, double maximum)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript();
        graph.Nodes.Add(Node("start", ScriptNodeKind.Start, "r0"));
        for (int i = 0; i < 128; i++)
            graph.Nodes.Add(new() { Id = "r" + i, Kind = ScriptNodeKind.Random, Variable = "r" + i, Minimum = minimum, Maximum = maximum, Next = i == 127 ? "dialogue" : "r" + (i + 1) });
        graph.Nodes.Add(new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "Random samples", Next = "end" });
        graph.Nodes.Add(Node("end", ScriptNodeKind.End));
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true,
            editorSource: new EditorPreviewSource("game/scenes/visual.knegraph", graph.Compile(), "main", StepCommands: false, Seed: 12345));
        await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        var samples = Enumerable.Range(0, 128).Select(i => state.GetProperty("r" + i).GetDouble()).ToArray();
        Assert.All(samples, sample => { Assert.InRange(sample, minimum, maximum); Assert.Equal(Math.Truncate(sample), sample); });
        Assert.Contains(minimum, samples); Assert.Contains(maximum, samples);
    }

    [Theory]
    [InlineData(ScriptNodeKind.While, 2)]
    [InlineData(ScriptNodeKind.ForEach, 2)]
    [InlineData(ScriptNodeKind.TryCatch, 3)]
    [InlineData(ScriptNodeKind.Switch, 4)]
    [InlineData(ScriptNodeKind.Parallel, 3)]
    [InlineData(ScriptNodeKind.Async, 3)]
    public void StructuredPorts_KeepBranchDefaultAndContinuationIndexesDistinct(ScriptNodeKind kind, int count)
    {
        var node = new ScriptNode { Kind = kind, Options = [new() { Text = "A" }, new() { Text = "B" }] };
        Assert.Equal(count, node.Outputs().Count());
        for (int i = 0; i < count; i++) node.SetOutput(i, "destination" + i);
        Assert.Equal(Enumerable.Range(0, count).Select(i => "destination" + i), node.Outputs().Select(o => o.Target));
        node.SetOutput(count - 1, null); Assert.Null(node.Next);
        Assert.Throws<ArgumentOutOfRangeException>(() => node.SetOutput(count, "bad"));
        var roundtrip = VisualScript.Parse(new VisualScript { Nodes = [node] }.Serialize()).Nodes.Single();
        Assert.Equal(node.Outputs().ToArray(), roundtrip.Outputs().ToArray());
    }

    [Theory]
    [InlineData(ScriptNodeKind.Random)]
    [InlineData(ScriptNodeKind.SetFlag)]
    [InlineData(ScriptNodeKind.Camera)]
    [InlineData(ScriptNodeKind.AudioChannel)]
    [InlineData(ScriptNodeKind.ForEach)]
    [InlineData(ScriptNodeKind.Animation)]
    public void InvalidTypedProperties_RejectUnsafeOrAmbiguousValuesBeforeExecution(ScriptNodeKind kind)
    {
        var graph = VisualScript.Create(); var node = graph.Nodes[1]; node.Kind = kind;
        switch (kind)
        {
            case ScriptNodeKind.Random: node.Minimum = 5; node.Maximum = 4; break;
            case ScriptNodeKind.SetFlag: node.Value = "state.injected=true"; break;
            case ScriptNodeKind.Camera: node.Amount = 0; break;
            case ScriptNodeKind.AudioChannel: node.Channel = "unknown"; break;
            case ScriptNodeKind.ForEach: node.Path = "items"; node.IterationKey = node.Variable; break;
            case ScriptNodeKind.Animation: node.Target = "unknown"; break;
        }
        Assert.Throws<InvalidDataException>(() => graph.Compile());
    }

    [Fact]
    public void NewFields_PersistWithoutChangingExistingEnumOrdinalsOrLegacyEndPolicy()
    {
        Assert.Equal(13, (int)ScriptNodeKind.CallScene);
        var graph = VisualScript.Create(); var node = graph.Nodes[1];
        node.Kind = ScriptNodeKind.Animation; node.Text = "pulse"; node.Duration = 2.5; node.OffsetX = -42; node.OffsetY = 19; node.Amount = .4;
        node.Minimum = -3; node.Maximum = 8; node.Channel = "ambient"; node.Target = "background"; node.IterationKey = "ordinal";
        var parsed = VisualScript.Parse(graph.Serialize()).Nodes[1];
        Assert.Equal(2.5, parsed.Duration); Assert.Equal(-42, parsed.OffsetX); Assert.Equal(19, parsed.OffsetY);
        Assert.Equal(.4, parsed.Amount); Assert.Equal(-3, parsed.Minimum); Assert.Equal(8, parsed.Maximum);
        Assert.Equal("ambient", parsed.Channel); Assert.Equal("background", parsed.Target); Assert.Equal("ordinal", parsed.IterationKey);
        Assert.Equal(ScriptEndAction.Return, VisualScript.Parse("{\"Nodes\":[{\"Id\":\"end\",\"Kind\":\"End\"}]}").Nodes.Single().EndAction);
    }

    [Fact]
    public async Task While_NeverYieldingTrueBody_ReportsBoundedIterationFailureInsteadOfHanging()
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "set"),
            new() { Id = "set", Kind = ScriptNodeKind.SetFlag, Variable = "again", Value = "true", Next = "loop" },
            new() { Id = "loop", Kind = ScriptNodeKind.While, Variable = "again", ValueKind = ScriptValueKind.Boolean,
                Value = "true", True = "body", Next = "after" },
            new() { Id = "body", Kind = ScriptNodeKind.SetFlag, Variable = "again", Value = "true", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End), Node("after", ScriptNodeKind.End)
        ] };
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("10000", error.Message);
    }

    [Theory]
    [InlineData(ScriptNodeKind.While)]
    [InlineData(ScriptNodeKind.ForEach)]
    [InlineData(ScriptNodeKind.TryCatch)]
    [InlineData(ScriptNodeKind.Parallel)]
    [InlineData(ScriptNodeKind.Async)]
    public void StructuredBodies_RejectExplicitBackEdgesAndMissingPorts(ScriptNodeKind kind)
    {
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "control"),
            new() { Id = "control", Kind = kind, ValueKind = ScriptValueKind.Boolean, Value = "true", Path = "items",
                Variable = "value", True = "body", False = "end", Next = "end", Options = [new() { Text = "Branch", Target = "body" }] },
            Node("body", ScriptNodeKind.SetVariable, "control"), Node("end", ScriptNodeKind.End)
        ] };
        Assert.Contains("циклы", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        graph.Nodes[2].Next = "end";
        graph.Nodes[1].Next = null;
        Assert.Contains("соедините", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    [Theory]
    [InlineData("state.items = nil")]
    [InlineData("state.items = {name = 'value'}")]
    [InlineData("state.items = {[1]='one', [3]='three'}")]
    public async Task ForEach_RejectsMissingDictionaryOrSparseCollection_InsteadOfSilentlySkippingValues(string source)
    {
        using var fixture = new TempProject(); fixture.Write("game/scripts/gamelogic.lua", source);
        var graph = new VisualScript { Nodes =
        [
            Node("start", ScriptNodeKind.Start, "each"),
            new() { Id = "each", Kind = ScriptNodeKind.ForEach, Path = "items", Variable = "item", True = "body", Next = "end" },
            new() { Id = "body", Kind = ScriptNodeKind.Dialogue, Text = "Item", Next = "bodyend" },
            Node("bodyend", ScriptNodeKind.End), Node("end", ScriptNodeKind.End)
        ] };
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("For Each", error.Message);
    }

    [Fact]
    public async Task ShowScreen_EmptyIdentifierClearsOverlay_ButEndScreenRequiresAnIdentifier()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create();
        graph.Nodes[0].Next = "open";
        graph.Nodes.Add(new() { Id = "open", Kind = ScriptNodeKind.ShowScreen, Path = "main_menu", Next = "before" });
        graph.Nodes.Add(new() { Id = "before", Kind = ScriptNodeKind.Dialogue, Text = "Before", Next = "screen" });
        graph.Nodes.Add(new() { Id = "screen", Kind = ScriptNodeKind.ShowScreen, Path = "", Next = "dialogue" });
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Before", runtime.Story.Text);
        Assert.Equal("main_menu", runtime.Visuals.OverlayScreen);
        long version = runtime.Visuals.ScreenVersion;
        await runtime.AdvanceAsync();
        Assert.Equal("Добро пожаловать!", runtime.Story.Text);
        Assert.Null(runtime.Visuals.OverlayScreen);
        Assert.True(runtime.Visuals.ScreenVersion > version);
        graph.Nodes.Single(n => n.Kind == ScriptNodeKind.End).EndAction = ScriptEndAction.Screen;
        Assert.Contains("идентификатор экрана", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }
}
