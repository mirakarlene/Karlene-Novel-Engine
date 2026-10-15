using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class VisualScriptTests
{
    [Theory]
    [InlineData("game/scenes/chapter.krl")]
    [InlineData("goto")]
    [InlineData("chapter); error('injected')")]
    public void SceneBlocks_RejectPathsAndInvalidFunctionNames(string target)
    {
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "jump" },
            new() { Id = "jump", Kind = ScriptNodeKind.GotoScene, Path = target }] };
        Assert.Contains("именем функции", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        graph.FunctionName = target;
        Assert.Contains("Имя функции блок-схемы", Assert.Throws<InvalidDataException>(() => graph.Serialize()).Message);
    }

    [Theory]
    [InlineData(ScriptNodeKind.CallScene)]
    [InlineData(ScriptNodeKind.GotoScene)]
    public async Task SceneBlocks_TransitionBetweenGraphsAndLua_AndRestoreReturnPosition(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var child = VisualScript.Create(); child.FunctionName = "child"; child.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue).Text = "Дочерняя блок-схема";
        fixture.Write("game/scenes/дочерняя.knegraph", child.Serialize());
        fixture.Write("game/scenes/bridge.lua", "function bridge() call_scene(child) end");
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "transition" },
            new() { Id = "transition", Kind = kind, Path = "bridge", Next = kind == ScriptNodeKind.CallScene ? "after" : null }] };
        if (kind == ScriptNodeKind.CallScene)
        {
            graph.Nodes.Add(new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "После вызова", Next = "end" });
            graph.Nodes.Add(new() { Id = "end", Kind = ScriptNodeKind.End });
        }
        var project = await LoadGraphAsync(fixture, graph);
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync(); Assert.Equal("Дочерняя блок-схема", runtime.Story.Text);
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(await runtime.GetCheckpointAsync());
        await restored.AdvanceAsync();
        if (kind == ScriptNodeKind.CallScene)
        {
            Assert.Equal("После вызова", restored.Story.Text);
            await restored.AdvanceAsync();
        }
        Assert.True(restored.Story.Completed);
    }

    [Fact]
    public void SceneBlocks_ValidateDestinationAndContinuation_AndExposeMatchingPorts()
    {
        var call = new ScriptNode { Id = "call", Kind = ScriptNodeKind.CallScene, Path = "chapter" };
        var jump = new ScriptNode { Id = "jump", Kind = ScriptNodeKind.GotoScene, Path = "chapter" };
        Assert.Single(call.Outputs()); Assert.Empty(jump.Outputs());
        Assert.Throws<ArgumentOutOfRangeException>(() => jump.SetOutput(0, "end"));
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "jump" }, jump] };
        jump.Path = "";
        Assert.Contains("имя функции", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        jump.Path = "chapter"; jump.Next = "start";
        Assert.Contains("нет продолжения", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        jump.Next = null; Assert.Contains("goto_scene(\"chapter\")", graph.Compile());
        graph.Nodes[0].Next = "call"; graph.Nodes[1] = call;
        Assert.Contains("соедините", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        call.Path = "";
        Assert.Contains("имя функции", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    internal static async Task<ProjectInfo> LoadGraphAsync(TempProject fixture, VisualScript graph)
    {
        fixture.Write("game/scenes/visual.knegraph", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "-- main is exported by visual.knegraph");
        var project = await fixture.LoadAsync();
        project.Configuration.EntryPoints!.MainScene = "game/scenes/visual.knegraph";
        return project;
    }

    [Theory]
    [InlineData(1, "Дом", 0)]
    [InlineData(2, "Парк", 2)]
    public async Task Graph_ChoiceAndCondition_RunBranchAndRestoreCheckpoint(int answer, string expected, int score)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript
        {
            Nodes =
            [
                new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "init" },
                new()
                {
                    Id = "init", Kind = ScriptNodeKind.SetVariable, Variable = "score", Value = "0", Next = "first"
                },
                new() { Id = "first", Kind = ScriptNodeKind.Dialogue, Text = "Начало", Next = "choice" },
                new()
                {
                    Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Куда?",
                    Options = [new() { Text = "Домой", Target = "home" }, new() { Text = "В парк", Target = "score" }]
                },
                new()
                {
                    Id = "score", Kind = ScriptNodeKind.SetVariable, Variable = "score", Value = "2", Next = "condition"
                },
                new()
                {
                    Id = "condition", Kind = ScriptNodeKind.Condition, Variable = "score", Value = "2",
                    Comparison = ScriptComparison.GreaterOrEqual, True = "park", False = "home"
                },
                new() { Id = "park", Kind = ScriptNodeKind.Dialogue, Text = "Парк", Next = "end" },
                new() { Id = "home", Kind = ScriptNodeKind.Dialogue, Text = "Дом", Next = "end" },
                new() { Id = "end", Kind = ScriptNodeKind.End }
            ]
        };
        // Execution must follow connections, not JSON order or card coordinates.
        graph.Nodes.Reverse();
        var project = await LoadGraphAsync(fixture, graph);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Начало", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal(new[] { "Домой", "В парк" }, runtime.Story.Options);
        await runtime.ChooseAsync(answer);
        Assert.Equal(expected, runtime.Story.Text);
        var saved = await runtime.GetCheckpointAsync();
        Assert.Equal(score, saved.State.GetProperty("score").GetInt32());
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(saved);
        Assert.Equal(expected, restored.Story.Text);
        await restored.AdvanceAsync();
        Assert.True(restored.Story.Completed);
    }

    [Theory]
    [InlineData(ScriptComparison.Equal, "2", "Да")]
    [InlineData(ScriptComparison.Equal, "3", "Нет")]
    [InlineData(ScriptComparison.NotEqual, "2", "Нет")]
    [InlineData(ScriptComparison.NotEqual, "3", "Да")]
    [InlineData(ScriptComparison.Greater, "2", "Нет")]
    [InlineData(ScriptComparison.Greater, "1", "Да")]
    [InlineData(ScriptComparison.Less, "2", "Нет")]
    [InlineData(ScriptComparison.Less, "3", "Да")]
    [InlineData(ScriptComparison.GreaterOrEqual, "2", "Да")]
    [InlineData(ScriptComparison.GreaterOrEqual, "3", "Нет")]
    [InlineData(ScriptComparison.LessOrEqual, "2", "Да")]
    [InlineData(ScriptComparison.LessOrEqual, "1", "Нет")]
    public async Task Condition_ComparisonBoundaries_SelectCorrectOutput(ScriptComparison comparison, string value,
        string expected)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript
        {
            Nodes =
            [
                new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "set" },
                new() { Id = "set", Kind = ScriptNodeKind.SetVariable, Variable = "score", Value = "2", Next = "if" },
                new()
                {
                    Id = "if", Kind = ScriptNodeKind.Condition, Variable = "score", Value = value,
                    Comparison = comparison, True = "yes", False = "no"
                },
                new() { Id = "yes", Kind = ScriptNodeKind.Dialogue, Text = "Да", Next = "end" },
                new() { Id = "no", Kind = ScriptNodeKind.Dialogue, Text = "Нет", Next = "end" },
                new() { Id = "end", Kind = ScriptNodeKind.End }
            ]
        };
        await using var runtime = new LuaRuntime(await LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(expected, runtime.Story.Text);
        Assert.Equal(2, (await runtime.GetCheckpointAsync()).State.GetProperty("score").GetInt32());
    }

    [Theory]
    [InlineData(ScriptValueKind.String, "Алиса 🌸\n\"\\\t\r\0\u000112\"); state.injected = true; --")]
    [InlineData(ScriptValueKind.Boolean, "true")]
    [InlineData(ScriptValueKind.Boolean, "false")]
    [InlineData(ScriptValueKind.Number, "-1.25e2")]
    public async Task Literals_UnicodeEscapesAndTypedValues_AreData(ScriptValueKind kind, string value)
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var first = graph.Nodes.Single(n => n.Id == "dialogue");
        first.Speaker = "Алиса\"\\\n";
        first.Text = "Привет 🌸\n\"\\\t\r\0\u000112";
        graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Start).Next = "set";
        graph.Nodes.Add(new()
        {
            Id = "set", Kind = ScriptNodeKind.SetVariable, Variable = "value", ValueKind = kind, Value = value,
            Next = first.Id
        });
        await using var runtime = new LuaRuntime(await LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Алиса\"\\\n", runtime.Story.Speaker);
        Assert.Equal("Привет 🌸\n\"\\\t\r\0\u000112", runtime.Story.Text);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.False(state.TryGetProperty("injected", out _));
        if (kind == ScriptValueKind.String) Assert.Equal(value, state.GetProperty("value").GetString());
        else if (kind == ScriptValueKind.Boolean)
            Assert.Equal(value == "true", state.GetProperty("value").GetBoolean());
        else Assert.Equal(-125, state.GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task ResourceBlocks_PublishAndClearVisualsAndAudio()
    {
        using var fixture = new TempProject();
        string image = fixture.Write("game/backgrounds/фон.png", "fixture");
        string audio = fixture.Write("game/audio/theme.wav", "fixture");
        var graph = new VisualScript();
        var commands = new[]
        {
            ScriptNodeKind.Start, ScriptNodeKind.Background, ScriptNodeKind.Character, ScriptNodeKind.Music,
            ScriptNodeKind.Sound,
            ScriptNodeKind.Dialogue, ScriptNodeKind.HideCharacter, ScriptNodeKind.StopMusic, ScriptNodeKind.Background,
            ScriptNodeKind.Dialogue, ScriptNodeKind.End
        };
        for (int i = 0; i < commands.Length; i++)
            graph.Nodes.Add(new()
            {
                Id = "n" + i, Kind = commands[i], Next = i == commands.Length - 1 ? null : "n" + (i + 1),
                Path = i == 8 ? "" :
                    commands[i] is ScriptNodeKind.Music or ScriptNodeKind.Sound ? "game/audio/theme.wav" :
                    "game/backgrounds/фон.png",
                Position = "right", Loop = false, Text = i == 5 ? "Ресурсы" : "Очищено"
            });
        await using var runtime = new LuaRuntime(await LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(image, runtime.Visuals.Background);
        Assert.Equal(image, runtime.Visuals.Character);
        Assert.Equal("right", runtime.Visuals.Position);
        Assert.Equal(audio, runtime.Visuals.Music);
        Assert.False(runtime.Visuals.LoopMusic);
        Assert.Equal(audio, runtime.Visuals.Sound);
        await runtime.AdvanceAsync();
        Assert.Equal("Очищено", runtime.Story.Text);
        Assert.Null(runtime.Visuals.Background);
        Assert.Null(runtime.Visuals.Character);
        Assert.Null(runtime.Visuals.Music);
    }

    [Theory]
    [InlineData("start", "ровно один")]
    [InlineData("missing", "соедините")]
    [InlineData("cycle", "циклы")]
    [InlineData("unreachable", "недостижимые")]
    [InlineData("to-start", "Начало")]
    [InlineData("empty-choice", "вариантов")]
    [InlineData("variable", "имя переменной")]
    [InlineData("number", "конечным числом")]
    [InlineData("boolean", "true/false")]
    [InlineData("position", "позиция персонажа")]
    [InlineData("path", "путь к ресурсу")]
    [InlineData("typed-comparison", "числового значения")]
    public void Compile_InvalidGraphs_ReportActionableError(string scenario, string expected)
    {
        var graph = VisualScript.Create();
        var node = graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue);
        switch (scenario)
        {
            case "start": graph.Nodes.RemoveAll(n => n.Kind == ScriptNodeKind.Start); break;
            case "missing": node.Next = "missing"; break;
            case "cycle":
                graph.Nodes.Single(n => n.Kind == ScriptNodeKind.End).Kind = ScriptNodeKind.Dialogue;
                graph.Nodes.Last().Next = node.Id;
                break;
            case "unreachable": graph.Nodes.Add(new() { Id = "orphan", Kind = ScriptNodeKind.End }); break;
            case "to-start": node.Next = "start"; break;
            case "empty-choice":
                node.Kind = ScriptNodeKind.Choice;
                node.Options = [];
                break;
            case "variable":
                node.Kind = ScriptNodeKind.SetVariable;
                node.Variable = "x']; os.exit(); --";
                break;
            case "number":
                node.Kind = ScriptNodeKind.SetVariable;
                node.Value = "NaN";
                break;
            case "boolean":
                node.Kind = ScriptNodeKind.SetVariable;
                node.ValueKind = ScriptValueKind.Boolean;
                node.Value = "yes";
                break;
            case "position":
                node.Kind = ScriptNodeKind.Character;
                node.Path = "image.png";
                node.Position = "outside";
                break;
            case "path": node.Kind = ScriptNodeKind.Sound; break;
            case "typed-comparison":
                node.Kind = ScriptNodeKind.Condition;
                node.ValueKind = ScriptValueKind.String;
                node.Comparison = ScriptComparison.Greater;
                break;
        }

        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Version\":2,\"Nodes\":[]}")]
    [InlineData("{\"Version\":1,\"Nodes\":null}")]
    [InlineData("{\"Nodes\":[null]}")]
    [InlineData("{\"Nodes\":[{\"Id\":\"x\",\"Kind\":\"FutureCommand\"}]}")]
    [InlineData("{\"Nodes\":[{\"Id\":\"x\"},{\"Id\":\"x\"}]}")]
    [InlineData("{\"Nodes\":[{\"X\":-1000000001}]}")]
    [InlineData("{\"Nodes\":[{\"Options\":[null]}]}")]
    [InlineData("{\"Nodes\":[{\"Text\":null}]}")]
    [InlineData("{\"UnknownFeature\":true}")]
    public void Parse_MalformedOrFutureDocuments_AreRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => VisualScript.Parse(json));

    [Fact]
    public async Task Graph_RunsInWorkerAndChangedGraphInvalidatesSave()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var project = await LoadGraphAsync(fixture, graph);
        await using var session = new ProcessGameSession(project, _ => { });
        await session.StartAsync();
        Assert.Equal("Добро пожаловать!", session.Story.Text);
        Assert.NotEqual(Environment.ProcessId, session.WorkerProcessId);
        var saved = await session.GetCheckpointAsync();
        graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue).Text = "Изменено";
        fixture.Write("game/scenes/visual.knegraph", graph.Serialize());
        await using var candidate = new LuaRuntime(project, _ => { }, interactive: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => candidate.RestoreAsync(saved));
        Assert.Equal("idle", candidate.Story.Kind);
    }

    [Fact]
    public async Task PreviewingDifferentScene_RejectsSiblingSave()
    {
        using var fixture = new TempProject();
        var project = await LoadGraphAsync(fixture, VisualScript.Create());
        await using var original = new LuaRuntime(project, _ => { }, interactive: true);
        await original.StartAsync();
        var saved = await original.GetCheckpointAsync();
        project.Configuration.EntryPoints!.MainScene = "game/scenes/main.krl";
        await using var sibling = new LuaRuntime(project, _ => { }, interactive: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => sibling.RestoreAsync(saved));
        Assert.Equal("idle", sibling.Story.Kind);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 1)]
    [InlineData(20, 20)]
    public async Task Choice_OneAndTwentyOptions_PreserveOneBasedAnswerOrder(int count, int answer)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript
        {
            Nodes =
            [
                new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "choice" },
                new() { Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Выбор" },
                new() { Id = "end", Kind = ScriptNodeKind.End }
            ]
        };
        var choice = graph.Nodes.Single(n => n.Id == "choice");
        for (int i = 1; i <= count; i++)
        {
            choice.Options.Add(new() { Text = "Вариант " + i, Target = "answer" + i });
            graph.Nodes.Add(new()
                { Id = "answer" + i, Kind = ScriptNodeKind.Dialogue, Text = "Ответ " + i, Next = "end" });
        }

        await using var runtime = new LuaRuntime(await LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(count, runtime.Story.Options.Count);
        Assert.Equal("Вариант " + count, runtime.Story.Options.Last());
        await runtime.ChooseAsync(answer);
        Assert.Equal("Ответ " + answer, runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public void DraftLimits_AllowBoundariesAndRejectExcess()
    {
        var graph = new VisualScript
        {
            Nodes = Enumerable.Range(0, 500).Select(i => new ScriptNode
                { Id = "n" + i, Kind = ScriptNodeKind.End, X = 10000, Y = 10000 }).ToList()
        };
        Assert.Equal(500, VisualScript.Parse(graph.Serialize()).Nodes.Count);
        graph.Nodes.Add(new() { Id = "extra", Kind = ScriptNodeKind.End });
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
        graph = VisualScript.Create();
        graph.Nodes[1].Kind = ScriptNodeKind.Choice;
        graph.Nodes[1].Options = Enumerable.Range(0, 20)
            .Select(i => new ScriptOption { Text = "Ответ " + i, Target = "end" }).ToList();
        Assert.Contains("choice(", graph.Compile());
        graph.Nodes[1].Options.Add(new() { Text = "Лишний", Target = "end" });
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
        graph = VisualScript.Create();
        graph.Nodes[1].X = 1_000_000_001;
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
    }
}
