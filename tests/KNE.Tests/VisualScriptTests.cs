using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.IO.Path;
using Xunit;

namespace KNE.Tests;

public sealed class VisualScriptTests
{
    [Fact]
    public async Task StartOnlyProjectReportsMissingBlocksAndPreservesTheDiagnosticForBrokenLinks()
    {
        using var fixture = new TempProject();
        string root = ProjectCreator.CreateProject("Только начало", fixture.Root, ProjectAuthoringMode.Visual);
        var project = await ProjectLoader.LoadProjectAsync(root);
        var graph = new VisualScript { Nodes = [new ScriptNode { Id = "start", Kind = ScriptNodeKind.Start }] };
        await File.WriteAllTextAsync(project.MainScenePath, graph.Serialize());
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        var startup = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Другие блоки не найдены", startup.ToString());
        Assert.DoesNotContain("соедините все выходы", startup.ToString());

        graph.Nodes.Add(new ScriptNode { Id = "end", Kind = ScriptNodeKind.End });
        Assert.Contains("соедините все выходы", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    [Theory]
    [InlineData(".knegraph")]
    [InlineData(".kneshader")]
    [InlineData(".vlua")]
    public Task InspectorShowsOnlyTheEmptyHintWithoutASelectedBlock(string extension) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        string path = Path.Combine(fixture.ProjectPath, "game", "scenes", "inspector" + extension);
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        var editor = new ScriptEditor();
        await editor.OpenAsync(fixture.ProjectPath, path);
        var hint = WorkspaceTabsTests.Find<System.Windows.Controls.TextBlock>(editor, "NoBlockSelected");
        var panel = WorkspaceTabsTests.Find<System.Windows.Controls.StackPanel>(editor, "PropertiesPanel");
        var detach = WorkspaceTabsTests.Find<Button>(editor, "DetachBlockPropertiesButton");
        editor.SelectGraphBlocks([]);
        Assert.Equal(System.Windows.Visibility.Collapsed, panel.Visibility);
        Assert.Equal(System.Windows.Visibility.Visible, hint.Visibility);
        Assert.Equal(Visibility.Collapsed, detach.Visibility);
        Assert.Equal("Выберите блок чтоб здесь отобразились его опции", hint.Text);

        var node = editor.Document!.Graph!.Nodes[0];
        editor.SelectGraphBlocks([node.Id]);
        Assert.Equal(System.Windows.Visibility.Visible, panel.Visibility);
        Assert.Equal(System.Windows.Visibility.Collapsed, hint.Visibility);
        Assert.Equal(node.Title, WorkspaceTabsTests.Find<System.Windows.Controls.TextBlock>(editor, "SelectedTitle").Text);
        var apply = WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton");
        var delete = WorkspaceTabsTests.Find<Button>(editor, "DeleteBlockButton");
        Assert.Equal(node.Kind == ScriptNodeKind.Start ? Visibility.Collapsed : Visibility.Visible, apply.Visibility);
        Assert.Equal(Visibility.Visible, delete.Visibility);
        Assert.True(delete.IsEnabled);
        Assert.Equal(Visibility.Visible, detach.Visibility);
        var dock = Assert.IsType<DockPanel>(detach.Parent);
        Assert.Equal(Dock.Top, DockPanel.GetDock(detach));
        Assert.Same(detach, dock.Children[0]);
        Assert.DoesNotContain(panel.Children.OfType<TextBlock>(), text => text.Text.Contains("Ctrl+колесо"));
    });

    [Theory]
    [InlineData(ScriptNodeKind.Dialogue)]
    [InlineData(ScriptNodeKind.AutoDialogue)]
    [InlineData(ScriptNodeKind.EffectDialogue)]
    [InlineData(ScriptNodeKind.ReverseDialogue)]
    public Task DialogueText_UpdatesModelAndCardDuringTypingAndCanBeUndoneWithoutSaving(ScriptNodeKind kind) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var dialogue = graph.Nodes.Single(node => node.Id == "dialogue");
        dialogue.Kind = kind;
        dialogue.Text = "Исходная реплика";
        string original = graph.Serialize();
        string path = fixture.Write("game/scenes/dialogue.knegraph", original);
        var editor = new ScriptEditor();
        await editor.OpenAsync(fixture.ProjectPath, path);
        editor.SelectGraphBlocks(["dialogue"]);
        Assert.Equal(Visibility.Visible, WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton").Visibility);
        var options = WorkspaceTabsTests.Find<StackPanel>(editor, "ExtendedProperties");
        Assert.Equal("Игнорировать спикера", options.Children.OfType<CheckBox>().Single(check => check.Name == "ExplicitNarratorInput").Content);
        var input = WorkspaceTabsTests.Find<TextBox>(editor, "SceneTextInput");
        var canvas = WorkspaceTabsTests.Find<Canvas>(editor, "GraphCanvas");
        Border Card() => canvas.Children.OfType<Border>().Single(card => Equals(card.Tag, "dialogue"));
        var card = Card();
        string DraftText() => editor.Document!.Graph!.Nodes.Single(node => node.Id == "dialogue").Text;

        input.Text = "Новая";
        input.Text = "Новая реплика";
        Assert.Same(card, Card());
        Assert.EndsWith("Новая реплика", Assert.IsType<StackPanel>(card.Child).Children.OfType<TextBlock>().Single().Text);
        Assert.Equal("Новая реплика", DraftText());
        Assert.True(editor.HasUnsavedChanges);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        WorkspaceTabsTests.Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Исходная реплика", DraftText());
        Assert.Equal("Исходная реплика", input.Text);
        Assert.False(editor.HasUnsavedChanges);
        WorkspaceTabsTests.Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Новая реплика", DraftText());
        Assert.Equal("Новая реплика", input.Text);
    });

    [Fact]
    public Task DetachedInspectorKeepsItsControlsAndTracksSelectionThenReturnsOnClose() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        string path = fixture.Write("game/scenes/inspector.knegraph", VisualScript.Create().Serialize());
        var editor = new ScriptEditor();
        var owner = new Window { Content = editor, ShowInTaskbar = false, ShowActivated = false, Left = -2000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            owner.Show();
            editor.Clear();
            await editor.OpenAsync(fixture.ProjectPath, path);
            editor.SelectGraphBlocks(["dialogue"]);
            var body = WorkspaceTabsTests.Find<ScrollViewer>(editor, "BlockPropertiesView");
            var input = WorkspaceTabsTests.Find<TextBox>(editor, "SceneTextInput");
            Assert.True(input.IsEnabled);
            input.Text = "Черновик";
            WorkspaceTabsTests.Find<Button>(editor, "DetachBlockPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var detached = Assert.Single(owner.OwnedWindows.Cast<Window>());
            Assert.Same(body, detached.Content);
            Assert.Equal("Черновик", input.Text);
            editor.SelectGraphBlocks(["end"]);
            Assert.Equal(editor.Document!.Graph!.Nodes.Single(node => node.Id == "end").Title, WorkspaceTabsTests.Find<TextBlock>(editor, "SelectedTitle").Text);
            editor.SelectGraphBlocks([]);
            Assert.Equal(Visibility.Visible, WorkspaceTabsTests.Find<TextBlock>(editor, "NoBlockSelected").Visibility);
            detached.Close();
            Assert.Same(body, WorkspaceTabsTests.Find<Border>(editor, "BlockPropertiesDock").Child);
            Assert.Equal("Черновик", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Text);

            var canvas = WorkspaceTabsTests.Find<Canvas>(editor, "GraphCanvas");
            var shadows = canvas.Children.OfType<Rectangle>().Where(item => item.Effect is DropShadowEffect).ToArray();
            Assert.Equal(3, shadows.Length);
            editor.ApplyBlockGlow(BlockGlowMode.Squares, smooth: false);
            Assert.All(shadows, shadow =>
            {
                Assert.Equal(11, shadow.RadiusX);
                Assert.Equal(EdgeMode.Aliased, RenderOptions.GetEdgeMode(shadow));
            });
            editor.ApplyBlockGlow(BlockGlowMode.Off, smooth: true);
            Assert.All(shadows, shadow => Assert.Null(shadow.Effect));
        }
        finally { owner.Close(); }
    });

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
    public Task ExtendedSceneLimit_AllowsGrowingAndSavingScenesWithoutLockingThemAfterDisabling() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        while (graph.Nodes.Count < 500)
            graph.Nodes.Add(new() { Id = "comment_" + graph.Nodes.Count, Kind = ScriptNodeKind.Comment });
        string path = fixture.Write("game/scenes/large.knegraph", graph.Serialize());
        var editor = new ScriptEditor();
        await editor.OpenAsync(fixture.ProjectPath, path);
        Assert.Contains("500", editor.GetBlockUnavailableReason(ScriptNodeKind.Comment));
        editor.ExtendedSceneBlockLimit = true;
        editor.AddBlock(ScriptNodeKind.Comment);
        Assert.Equal(501, editor.Document!.Graph!.Nodes.Count);
        editor.ExtendedSceneBlockLimit = false;
        Assert.False(editor.CanAddBlock(ScriptNodeKind.Comment));
        await editor.SaveAsync();
        var saved = VisualScript.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(501, saved.Nodes.Count);
        Assert.Contains("function main()", saved.Compile());
    });

    [Fact]
    public void DraftLimits_AllowBoundariesAndRejectExcess()
    {
        var graph = new VisualScript
        {
            Nodes = Enumerable.Range(0, 1000).Select(i => new ScriptNode
                { Id = "n" + i, Kind = ScriptNodeKind.End, X = 10000, Y = 10000 }).ToList()
        };
        Assert.Equal(1000, VisualScript.Parse(graph.Serialize()).Nodes.Count);
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
