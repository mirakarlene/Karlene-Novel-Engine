using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class GraphInitializationTests
{
    [Theory]
    [InlineData(-10000)] [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(10000)]
    public void UnconnectedInitializationMetadataRoundTripsItsPriorityWithoutChangingExecutableFlow(int priority)
    {
        var graph = VisualScript.Create(); Assert.Null(graph.InitPriority);
        string original = graph.Compile(); var metadata = Initialization(priority); graph.Nodes.Add(metadata);
        Assert.Empty(metadata.Outputs()); Assert.Equal(priority, graph.InitPriority);
        string encoded = graph.Serialize(); var parsed = VisualScript.Parse(encoded);
        Assert.Equal(priority, parsed.InitPriority); Assert.Equal(priority, parsed.Nodes.Single(node => node.Kind == ScriptNodeKind.Initialization).InitPriority);
        Assert.Equal(encoded, parsed.Serialize()); Assert.Equal(original, parsed.Compile());
        Assert.Equal("dialogue", parsed.Nodes.Single(node => node.Id == "start").Next);
    }

    [Theory]
    [InlineData(-10001)] [InlineData(10001)] [InlineData(int.MinValue)] [InlineData(int.MaxValue)]
    public void InitializationRejectsPrioritiesImmediatelyOutsideTheSupportedRangeAndIntegerExtremes(int priority)
    {
        var graph = VisualScript.Create(); graph.Nodes.Add(Initialization(priority));
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("incoming")] [InlineData("outgoing")]
    public void InitializationRejectsDuplicateMetadataAndConnectionsInEitherDirection(string invalid)
    {
        var graph = VisualScript.Create(); var metadata = Initialization(7); graph.Nodes.Add(metadata);
        if (invalid == "duplicate") { var duplicate = Initialization(-7); duplicate.Id = "second_init"; graph.Nodes.Add(duplicate); }
        if (invalid == "incoming") graph.Nodes.Single(node => node.Id == "start").Next = metadata.Id;
        if (invalid == "outgoing") metadata.Next = "dialogue";
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
    }

    [Fact]
    public async Task CatalogSortsGraphAndCodeInitializersTogetherByPriorityThenNormalizedOrdinalPath()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "-- legacy zero");
        fixture.Write("before.lua", "init(-10000) state.before=true"); fixture.Write("after.lua", "init(10000) state.after=true");
        foreach (var (path, function) in new[] { ("Z.knegraph", "upper"), ("a.knegraph", "lower"), ("folder/nested.knegraph", "nested") })
        {
            var graph = VisualScript.Create(); graph.FunctionName = function; graph.Nodes.Add(Initialization(0)); fixture.Write(path, graph.Serialize());
        }
        var unmarked = VisualScript.Create(); unmarked.FunctionName = "ordinary"; fixture.Write("ordinary.knegraph", unmarked.Serialize());
        var scripts = GameScripts.Load(await fixture.LoadAsync());
        Assert.Equal(new[] { "before.lua", "Z.knegraph", "a.knegraph", "folder/nested.knegraph", "game/scripts/gamelogic.lua", "after.lua" }, scripts.Initialization.Select(script => script.Path));
        Assert.Equal(new int?[] { -10000, 0, 0, 0, 0, 10000 }, scripts.Initialization.Select(script => script.Priority));
        Assert.Null(scripts.FindFile("ordinary.knegraph").Priority); Assert.All(scripts.Initialization, script => Assert.DoesNotContain('\\', script.Path));
        Assert.Equal(0, scripts.FindFile("folder\\nested.knegraph").Graph!.InitPriority);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LoadingAnInitializerGraphDeclaresItsFunctionWithoutExecutingItsBodyUntilTheSceneIsCalled(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("early.lua", "init(-10) state.order='early'");
        fixture.Write("game/scripts/gamelogic.lua", "state.order=state.order..'|legacy'");
        fixture.Write("late.lua", "init(10) state.order=state.order..'|late'");
        fixture.Write("graph-body.lua", "state.graph_calls=(state.graph_calls or 0)+1; state.order=state.order..'|graph'");
        var graph = new VisualScript { FunctionName = "helper", Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "body" },
            new() { Id = "body", Kind = ScriptNodeKind.Script, Path = "graph-body.lua", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End },
            Initialization(-5)
        ] };
        fixture.Write("helper.knegraph", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'before:'..state.order); call_scene(helper); d(nil,'after:'..state.order) end");
        var project = await fixture.LoadAsync(); Assert.Equal(new[] { "early.lua", "helper.knegraph", "game/scripts/gamelogic.lua", "late.lua" }, GameScripts.Load(project).Initialization.Select(script => script.Path));
        await using var runtime = NamedListRuntimeTests.Runtime(project, worker); await runtime.StartAsync();
        Assert.Equal("before:early|legacy|late", runtime.Story.Text); Assert.False((await runtime.GetCheckpointAsync()).State.TryGetProperty("graph_calls", out _));
        await runtime.AdvanceAsync(); Assert.Equal("after:early|legacy|late|graph", runtime.Story.Text); Assert.Equal(1, (await runtime.GetCheckpointAsync()).State.GetProperty("graph_calls").GetInt32());
    }

    static ScriptNode Initialization(int priority)
    {
        var node = BlockLibrary.Create(ScriptNodeKind.Initialization); node.Id = "init"; node.InitPriority = priority; return node;
    }
}
