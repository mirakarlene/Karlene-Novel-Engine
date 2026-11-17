using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class LogicBlockTests
{
    public static IEnumerable<object[]> Commands()
    {
        object[] Case(ScriptNodeKind kind, LogicSettings? settings = null) => [kind, settings ?? new()];
        yield return Case(ScriptNodeKind.CreateList, new() { InitialValues = "[\"sword\",{\"hp\":7},true]" });
        foreach (var kind in new[] { ScriptNodeKind.DeleteList, ScriptNodeKind.ClearList, ScriptNodeKind.ListLength, ScriptNodeKind.ListIsEmpty, ScriptNodeKind.ListShuffle,
            ScriptNodeKind.ListSort, ScriptNodeKind.ListMerge, ScriptNodeKind.ListCopy, ScriptNodeKind.ListExists, ScriptNodeKind.ListRandom, ScriptNodeKind.ResetVariable }) yield return Case(kind);
        yield return Case(ScriptNodeKind.ListAdd, new() { A = new("string", "potion") });
        yield return Case(ScriptNodeKind.ListRemove, new() { A = new("string", "sword") });
        yield return Case(ScriptNodeKind.ListRemoveAt, new() { Index = new("number", "1") });
        yield return Case(ScriptNodeKind.ListInsert, new() { Index = new("number", "1"), A = new("string", "potion") });
        yield return Case(ScriptNodeKind.ListSet, new() { Index = new("number", "0"), A = new("string", "new") });
        yield return Case(ScriptNodeKind.ListGet, new() { Index = new("number", "1") });
        yield return Case(ScriptNodeKind.ListContains, new() { A = new("string", "shield") });
        yield return Case(ScriptNodeKind.ListIndexOf, new() { A = new("string", "shield") });
        yield return Case(ScriptNodeKind.StringConcat, new() { A = new("string", "Привет "), B = new("string", "мир") });
        yield return Case(ScriptNodeKind.StringLength, new() { A = new("string", "ёж🙂") });
        yield return Case(ScriptNodeKind.StringContains, new() { A = new("string", "a.*b"), B = new("string", ".*") });
        yield return Case(ScriptNodeKind.StringReplace, new() { A = new("string", "a.*b.*"), B = new("string", ".*"), C = new("string", "%") });
        yield return Case(ScriptNodeKind.StringUpper, new() { A = new("string", "ёж") });
        yield return Case(ScriptNodeKind.StringLower, new() { A = new("string", "ЁЖ") });
        yield return Case(ScriptNodeKind.ConvertType, new() { A = new("string", "12.5"), Conversion = "number" });
        yield return Case(ScriptNodeKind.ConvertType, new() { A = new("boolean", "false"), Conversion = "string" });
        yield return Case(ScriptNodeKind.ConvertType, new() { A = new("number", "0"), Conversion = "boolean" });
        yield return Case(ScriptNodeKind.CopyVariable, new() { A = new("variable", "source") });
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task EveryLogicBlockRoundTripsAndExecutesTheSameTypedOperationAsItsCodeApi(ScriptNodeKind kind, LogicSettings settings)
    {
        using var fixture = new TempProject(); fixture.Write("engine/state.json", "{\"result\":5}");
        fixture.Write("game/scripts/gamelogic.lua", (kind == ScriptNodeKind.CreateList ? "" : "create_list('inventory',{'sword','shield'}); ") + "create_list('loot',{'coin'}); state.source='source value'; state.result='temporary'");
        var graph = Graph(kind, settings); string encoded = graph.Serialize(); var parsed = VisualScript.Parse(encoded); Assert.Equal(encoded, parsed.Serialize());
        var block = parsed.Nodes.Single(n => n.Id == "logic"); Assert.Equal(kind, block.Kind); Assert.Equal("dialogue", block.Next); Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(block.Logic));
        var project = await GraphProject(fixture, parsed); await using var runtime = NamedListRuntimeTests.Runtime(project, false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        Assert.Equal("done", runtime.Story.Text);
        if (kind == ScriptNodeKind.DeleteList) { Assert.False(saved.Lists!.Value.TryGetProperty("inventory", out _)); return; }
        var list = NamedListRuntimeTests.List(saved, "inventory");
        switch (kind)
        {
            case ScriptNodeKind.CreateList: Assert.Equal(3, list.GetArrayLength()); Assert.Equal(7, list[1].GetProperty("hp").GetInt32()); Assert.True(list[2].GetBoolean()); break;
            case ScriptNodeKind.ClearList: Assert.Equal(0, list.GetArrayLength()); break;
            case ScriptNodeKind.ListAdd: AssertStrings(list, "sword", "shield", "potion"); break;
            case ScriptNodeKind.ListRemove: AssertStrings(list, "shield"); break;
            case ScriptNodeKind.ListRemoveAt: AssertStrings(list, "sword"); break;
            case ScriptNodeKind.ListInsert: AssertStrings(list, "sword", "potion", "shield"); break;
            case ScriptNodeKind.ListSet: AssertStrings(list, "new", "shield"); break;
            case ScriptNodeKind.ListShuffle: Assert.Equal(new[] { "shield", "sword" }, list.EnumerateArray().Select(v => v.GetString()).Order()); break;
            case ScriptNodeKind.ListSort: AssertStrings(list, "shield", "sword"); break;
            case ScriptNodeKind.ListMerge: AssertStrings(list, "sword", "shield", "coin"); AssertStrings(NamedListRuntimeTests.List(saved, "loot"), "coin"); break;
            case ScriptNodeKind.ListCopy: AssertStrings(list, "sword", "shield"); AssertStrings(NamedListRuntimeTests.List(saved, "loot"), "sword", "shield"); break;
            case ScriptNodeKind.ListLength: Assert.Equal(2, saved.State.GetProperty("result").GetInt32()); break;
            case ScriptNodeKind.ListGet: Assert.Equal("shield", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.ListContains: case ScriptNodeKind.ListExists: case ScriptNodeKind.StringContains: Assert.True(saved.State.GetProperty("result").GetBoolean()); break;
            case ScriptNodeKind.ListIndexOf: Assert.Equal(1, saved.State.GetProperty("result").GetInt32()); break;
            case ScriptNodeKind.ListIsEmpty: Assert.False(saved.State.GetProperty("result").GetBoolean()); break;
            case ScriptNodeKind.ListRandom: Assert.Contains(saved.State.GetProperty("result").GetString(), new[] { "sword", "shield" }); break;
            case ScriptNodeKind.StringConcat: Assert.Equal("Привет мир", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.StringLength: Assert.Equal(3, saved.State.GetProperty("result").GetInt32()); break;
            case ScriptNodeKind.StringReplace: Assert.Equal("a%b%", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.StringUpper: Assert.Equal("ЁЖ", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.StringLower: Assert.Equal("ёж", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.ConvertType:
                if (settings.Conversion == "number") Assert.Equal(12.5, saved.State.GetProperty("result").GetDouble());
                else if (settings.Conversion == "string") Assert.Equal("false", saved.State.GetProperty("result").GetString());
                else Assert.False(saved.State.GetProperty("result").GetBoolean()); break;
            case ScriptNodeKind.CopyVariable: Assert.Equal("source value", saved.State.GetProperty("result").GetString()); break;
            case ScriptNodeKind.ResetVariable: Assert.Equal(5, saved.State.GetProperty("result").GetInt32()); break;
            default: throw new InvalidOperationException("Missing operation witness: " + kind);
        }
    }

    [Fact]
    public async Task TypedStringOperandsPreserveControlCharactersAndCodeLookingTextWithoutExecutingIt()
    {
        using var fixture = new TempProject(); const string text = "ёж\r\n\t\"\\\0\u2028\"); state.injected=true; --";
        var settings = new LogicSettings { A = new("string", text), B = new("string", "suffix") };
        var graph = VisualScript.Parse(Graph(ScriptNodeKind.StringConcat, settings).Serialize());
        await using var runtime = NamedListRuntimeTests.Runtime(await GraphProject(fixture, graph), false); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(text + "suffix", state.GetProperty("result").GetString()); Assert.False(state.TryGetProperty("injected", out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompoundConditionsNestAndOrNotWithShortCircuitingAndHaveNoFixedRowCount(bool worker)
    {
        using var fixture = new TempProject();
        var invalid = new LogicCondition { A = new("variable", "missing"), B = new("number", "0"), Comparison = ">" };
        var yes = Compare("7", "7"); var no = Compare("7", "8");
        var conditions = new LogicCondition { Operator = "and", Children =
        [
            new() { Operator = "or", Children = [yes, invalid] },
            new() { Operator = "not", Children = [new() { Operator = "and", Children = [no, invalid] }] },
            new() { Operator = "and", Children = Enumerable.Range(0, 65).Select(_ => Compare("1", "1")).ToList() }
        ] };
        var graph = Graph(ScriptNodeKind.CompoundCondition, new() { Condition = conditions }); var block = graph.Nodes.Single(n => n.Id == "logic"); block.Next = null; block.True = "dialogue"; block.False = "failed";
        graph.Nodes.Add(new() { Id = "failed", Kind = ScriptNodeKind.Dialogue, Text = "wrong", Next = "end" });
        Assert.Equal(new[] { "Да", "Нет" }, block.Outputs().Select(p => p.Label)); block.SetOutput(1, "failed");
        var restored = VisualScript.Parse(graph.Serialize()); Assert.Equal(65, restored.Nodes.Single(n => n.Id == "logic").Logic!.Condition.Children[2].Children.Count);
        await using var runtime = NamedListRuntimeTests.Runtime(await GraphProject(fixture, restored), worker); await runtime.StartAsync(); Assert.Equal("done", runtime.Story.Text);
    }

    [Theory]
    [InlineData("and", false)] [InlineData("or", false)] [InlineData("not", true)]
    public async Task CompoundConditionsChooseTheirFalsePortWhenTheLogicalResultIsFalse(string op, bool compare)
    {
        using var fixture = new TempProject(); var condition = new LogicCondition { Operator = op, Children = [Compare("1", compare ? "1" : "2")] };
        var graph = Graph(ScriptNodeKind.CompoundCondition, new() { Condition = condition }); var block = graph.Nodes.Single(n => n.Id == "logic"); block.Next = null; block.True = "failed"; block.False = "dialogue";
        graph.Nodes.Add(new() { Id = "failed", Kind = ScriptNodeKind.Dialogue, Text = "wrong", Next = "end" });
        await using var runtime = NamedListRuntimeTests.Runtime(await GraphProject(fixture, graph), false); await runtime.StartAsync(); Assert.Equal("done", runtime.Story.Text);
    }

    [Theory]
    [InlineData("unknown", "x")] [InlineData("variable", "x;error('bad')")] [InlineData("list", "bad name")] [InlineData("number", "1e999")] [InlineData("number", "1;error('bad')")] [InlineData("boolean", "1")]
    public void InvalidTypedOperandsCannotBeCompiledIntoExecutableLua(string kind, string value) => Assert.Throws<ArgumentException>(() => LogicCompiler.Operand(new(kind, value)));

    [Theory]
    [InlineData("xor", 2)] [InlineData("not", 0)] [InlineData("not", 2)] [InlineData("and", 0)] [InlineData("or", 0)]
    public void InvalidCompoundOperatorsAndAritiesAreRejectedBeforeCompiling(string op, int count) => Assert.Throws<ArgumentException>(() => LogicCompiler.CompileCondition(new() { Operator = op, Children = Enumerable.Range(0, count).Select(_ => Compare("1", "1")).ToList() }));

    [Fact]
    public void InvalidOperationOptionsAndIdentifiersAreRejectedBeforeGraphSave()
    {
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.CreateList, new() { InitialValues = "{}" }, "result"));
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.CreateList, new() { InitialValues = "[null]" }, "result"));
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.ListLength, new() { List = "bad name" }, "result"));
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.ListLength, new(), "bad;name"));
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.ConvertType, new() { Conversion = "execute" }, "result"));
        Assert.Throws<ArgumentException>(() => LogicCompiler.Compile(ScriptNodeKind.CopyVariable, new() { A = new("string", "source") }, "result"));
        var graph = Graph(ScriptNodeKind.ListLength, new() { List = "bad name" }); Assert.Throws<InvalidDataException>(() => graph.Serialize());
    }

    [Fact]
    public async Task RuntimeLogicFailuresRetainTheOriginalGraphPathAndBlockIdentity()
    {
        using var fixture = new TempProject(); fixture.Write("game/scripts/gamelogic.lua", "create_list('inventory',{'sword'})");
        await using var runtime = NamedListRuntimeTests.Runtime(await GraphProject(fixture, Graph(ScriptNodeKind.ListGet, new() { Index = new("number", "99") })), false);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync()); var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.knegraph", report.File); Assert.Equal("logic", report.Frame!.BlockId); Assert.Contains("индекс", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingRandomBlockReadsANamedListWhenConfiguredAndKeepsItsLegacyNumericRangeOtherwise(bool fromList)
    {
        using var fixture = new TempProject(); fixture.Write("game/scripts/gamelogic.lua", "create_list('inventory',{'potion'})");
        var graph = Graph(ScriptNodeKind.Random, new() { List = fromList ? "inventory" : "" }); var block = graph.Nodes.Single(n => n.Id == "logic"); block.Minimum = block.Maximum = 7;
        await using var runtime = NamedListRuntimeTests.Runtime(await GraphProject(fixture, graph), false); await runtime.StartAsync(); var result = (await runtime.GetCheckpointAsync()).State.GetProperty("result");
        if (fromList) Assert.Equal("potion", result.GetString()); else Assert.Equal(7, result.GetDouble());
    }

    static LogicCondition Compare(string a, string b) => new() { A = new("number", a), B = new("number", b) };
    static void AssertStrings(JsonElement array, params string[] values) => Assert.Equal(values, array.EnumerateArray().Select(v => v.GetString()));
    static VisualScript Graph(ScriptNodeKind kind, LogicSettings settings) => new() { Nodes =
    [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "logic" }, new() { Id = "logic", Kind = kind, Logic = settings, Variable = "result", Next = "dialogue" },
        new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "done", Next = "end" }, new() { Id = "end", Kind = ScriptNodeKind.End }] };
    static async Task<ProjectInfo> GraphProject(TempProject fixture, VisualScript graph)
    {
        fixture.Write("game/scenes/main.krl", "-- main graph"); fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph"; return project;
    }
}
