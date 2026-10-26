using System.IO;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class FlowAnchorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anchors_RepeatOnlyOwnSceneFlow_AndReplayPreservesState(bool worker)
    {
        using var fixture = new TempProject();
        var increment = BlockLibrary.Create(ScriptNodeKind.Increment); increment.Id = "increment"; increment.Variable = "count"; increment.Calculation = new() { A = "count" }; increment.Next = "condition";
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "init" },
            new() { Id = "init", Kind = ScriptNodeKind.SetVariable, Variable = "count", Value = "0", ValueKind = ScriptValueKind.Number, Next = "anchor" },
            new() { Id = "anchor", Kind = ScriptNodeKind.Anchor, Text = "Повтор", Next = "dialogue" },
            new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "loop", Next = "increment" },
            increment,
            new() { Id = "condition", Kind = ScriptNodeKind.Branching, Variable = "count", Comparison = ScriptComparison.Less, ValueKind = ScriptValueKind.Number, Value = "2", True = "send", False = "finished" },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = "anchor" },
            new() { Id = "finished", Kind = ScriptNodeKind.Dialogue, Text = "done", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        await using IGameSession runtime = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("loop", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("loop", runtime.Story.Text);
        var checkpoint = await runtime.GetCheckpointAsync(); Assert.Equal(1, checkpoint.State.GetProperty("count").GetDouble());
        await runtime.RestoreAsync(checkpoint); await runtime.AdvanceAsync(); Assert.Equal("done", runtime.Story.Text);
        Assert.Equal(2, (await runtime.GetCheckpointAsync()).State.GetProperty("count").GetDouble());
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task ForwardAnchor_IsReachableThroughSendWithoutAnOrdinaryOutput()
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "send" },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = "anchor" },
            new() { Id = "anchor", Kind = ScriptNodeKind.Anchor, Text = "Вперёд", Next = "dialogue" },
            new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "reached", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        Assert.Empty(graph.Nodes[1].Outputs());
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("reached", runtime.Story.Text);
    }

    [Theory]
    [InlineData(ScriptNodeKind.Parallel)]
    [InlineData(ScriptNodeKind.Async)]
    public async Task OwnBranchAnchor_RepeatsInsideParallelOrAsyncWithoutRedirectingParent(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "init" },
            new() { Id = "init", Kind = ScriptNodeKind.SetVariable, Variable = "count", Value = "0", Next = "fork" },
            new() { Id = "fork", Kind = kind, Variable = "jobs", Next = kind == ScriptNodeKind.Async ? "join" : "done", Options = [new() { Text = "left", Target = "anchor" }, new() { Text = "right", Target = "right-end" }] },
            new() { Id = "anchor", Kind = ScriptNodeKind.Anchor, Text = "Своя ветка", Next = "increment" },
            new() { Id = "increment", Kind = ScriptNodeKind.Increment, Variable = "count", Calculation = new() { A = "count" }, Next = "condition" },
            new() { Id = "condition", Kind = ScriptNodeKind.Branching, Variable = "count", Comparison = ScriptComparison.Less, Value = "2", True = "send", False = "left-end" },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = "anchor" },
            new() { Id = "left-end", Kind = ScriptNodeKind.End }, new() { Id = "right-end", Kind = ScriptNodeKind.End },
            new() { Id = "done", Kind = ScriptNodeKind.Dialogue, Text = "parent", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        if (kind == ScriptNodeKind.Async) graph.Nodes.Add(new() { Id = "join", Kind = ScriptNodeKind.Join, Variable = "jobs", Next = "done" });
        await using var runtime = new ProcessGameSession(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { });
        await runtime.StartAsync(); Assert.Equal("parent", runtime.Story.Text);
        Assert.Equal(2, (await runtime.GetCheckpointAsync()).State.GetProperty("count").GetDouble());
    }

    [Theory]
    [InlineData("left-anchor", "другому потоку")]
    [InlineData("other-scene-anchor", "существующий якорь")]
    public void Send_RejectsAnchorOfAnotherParallelFlowOrScene(string target, string message)
    {
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "fork" },
            new() { Id = "fork", Kind = ScriptNodeKind.Parallel, Next = "end", Options = [new() { Text = "left", Target = "left-anchor" }, new() { Text = "right", Target = "send" }] },
            new() { Id = "left-anchor", Kind = ScriptNodeKind.Anchor, Text = "Левый поток", Next = "branch-end" },
            new() { Id = "branch-end", Kind = ScriptNodeKind.End },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = target },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        Assert.Contains(message, Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalJump_ChangesFunctionWhenTrue_AndContinuesWhenFalse(bool enabled)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/destination.krl", "function destination() d(nil, 'destination') end");
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "set" },
            new() { Id = "set", Kind = ScriptNodeKind.SetVariable, Variable = "enabled", ValueKind = ScriptValueKind.Boolean, Value = enabled ? "true" : "false", Next = "jump" },
            new() { Id = "jump", Kind = ScriptNodeKind.ConditionalGoto, Variable = "enabled", ValueKind = ScriptValueKind.Boolean, Value = "true", Path = "destination", Next = "dialogue" },
            new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "continued", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        Assert.Single(graph.Nodes[2].Outputs());
        await using var runtime = new ProcessGameSession(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { });
        await runtime.StartAsync(); Assert.Equal(enabled ? "destination" : "continued", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData(ScriptNodeKind.While)]
    [InlineData(ScriptNodeKind.TryCatch)]
    public async Task SendFromStructuredBody_UnwindsToOwnAnchorWithoutCatchingItOrDuplicatingBody(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "init" },
            new() { Id = "init", Kind = ScriptNodeKind.SetVariable, Variable = "visits", Value = "0", ValueKind = ScriptValueKind.Number, Next = "flag" },
            new() { Id = "flag", Kind = ScriptNodeKind.SetFlag, Variable = "always", Value = "true", Next = "anchor" },
            new() { Id = "anchor", Kind = ScriptNodeKind.Anchor, Text = "Снаружи тела", Next = "increment" },
            new() { Id = "increment", Kind = ScriptNodeKind.Increment, Variable = "visits", Calculation = new() { A = "visits" }, Next = "condition" },
            new() { Id = "condition", Kind = ScriptNodeKind.Branching, Variable = "visits", Comparison = ScriptComparison.Less, ValueKind = ScriptValueKind.Number, Value = "2", True = "body", False = "done" },
            new() { Id = "body", Kind = kind, Variable = kind == ScriptNodeKind.While ? "always" : "error", ValueKind = ScriptValueKind.Boolean, Value = "true", True = "send", False = kind == ScriptNodeKind.TryCatch ? "unexpected" : null, Next = "unexpected" },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = "anchor" },
            new() { Id = "unexpected", Kind = ScriptNodeKind.Dialogue, Text = "wrong continuation", Next = "end" },
            new() { Id = "done", Kind = ScriptNodeKind.Dialogue, Text = "done", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        await using var runtime = new ProcessGameSession(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { });
        await runtime.StartAsync(); Assert.Equal("done", runtime.Story.Text);
        var saved = await runtime.GetCheckpointAsync(); Assert.Equal(2, saved.State.GetProperty("visits").GetDouble());
        Assert.False(saved.State.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Editor_AnchorSelectionAndConditionalFields_AreEditableUndoableAndPersisted()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("map.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path);
                editor.AddBlock(ScriptNodeKind.Anchor);
                string anchor = editor.Document!.Graph!.Nodes.Last().Id;
                Extended<TextBox>("AnchorNameInput").Text = "Повтор"; Click("ApplyPropertiesButton");
                Assert.Equal("Повтор", Node(anchor).Text);
                editor.AddBlock(ScriptNodeKind.SendFlow);
                string send = editor.Document.Graph.Nodes.Last().Id;
                Extended<ComboBox>("FlowAnchorInput").SelectedValue = anchor; Click("ApplyPropertiesButton");
                Assert.Equal(anchor, Node(send).Target); Assert.Empty(Node(send).Outputs());
                Click("UndoButton"); Assert.Equal("", Node(send).Target);
                Click("RedoButton"); Assert.Equal(anchor, Node(send).Target);
                editor.AddBlock(ScriptNodeKind.ConditionalGoto);
                string jump = editor.Document.Graph.Nodes.Last().Id;
                Assert.Equal(Visibility.Visible, Find<ComboBox>("ValueTypeInput").Visibility);
                Assert.Equal(Visibility.Visible, Find<TextBox>("ValueInput").Visibility);
                Find<TextBox>("VariableInput").Text = "count";
                Find<TextBox>("ValueInput").Text = "5";
                Find<ComboBox>("ComparisonInput").SelectedValue = ScriptComparison.Greater;
                Find<TextBox>("PathInput").Text = "destination"; Click("ApplyPropertiesButton");
                Assert.Equal("count", Node(jump).Variable); Assert.Equal("5", Node(jump).Value);
                Assert.Equal(ScriptComparison.Greater, Node(jump).Comparison); Assert.Equal("destination", Node(jump).Path);
                await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
                Assert.Equal("Повтор", Node(anchor).Text); Assert.Equal(anchor, Node(send).Target);
                Assert.Equal("count", Node(jump).Variable); Assert.Equal("destination", Node(jump).Path);
                Assert.Equal("5", Node(jump).Value); Assert.Equal(ScriptComparison.Greater, Node(jump).Comparison);
                editor.SelectGraphBlocks([anchor, send]); editor.DuplicateGraphSelection();
                var copies = editor.Document.Graph.Nodes.Where(node => editor.SelectedBlockIds.Contains(node.Id)).ToArray();
                var anchorCopy = Assert.Single(copies, node => node.Kind == ScriptNodeKind.Anchor);
                Assert.Equal(anchorCopy.Id, Assert.Single(copies, node => node.Kind == ScriptNodeKind.SendFlow).Target);
                Assert.NotEqual(anchor, anchorCopy.Id);
                Click("UndoButton");
                editor.SelectGraphBlocks([send]); editor.DuplicateGraphSelection();
                Assert.Equal("", Assert.Single(editor.Document.Graph.Nodes, node => editor.SelectedBlockIds.Contains(node.Id)).Target);
                editor.ConfirmSave = _ => MessageBoxResult.No;
                T Find<T>(string name) where T : class => WorkspaceTabsTests.Find<T>(editor, name);
                T Extended<T>(string name) where T : FrameworkElement => Find<StackPanel>("ExtendedProperties").Children.OfType<T>().Single(input => input.Name == name);
                void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ScriptNode Node(string id) => editor.Document!.Graph!.Nodes.Single(node => node.Id == id);
            }
            finally { editor.Clear(); }
        });
    }

    [Fact]
    public async Task Analyzer_RecognizesAnchorReachability_AndReportsMissingConditionalTargetWithItsBlock()
    {
        using var fixture = new TempProject();
        var graph = new VisualScript { Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "send" },
            new() { Id = "send", Kind = ScriptNodeKind.SendFlow, Target = "anchor" },
            new() { Id = "anchor", Kind = ScriptNodeKind.Anchor, Text = "Вперёд", Next = "jump" },
            new() { Id = "jump", Kind = ScriptNodeKind.ConditionalGoto, Variable = "flag", ValueKind = ScriptValueKind.Boolean, Value = "true", Path = "missing", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        var result = await ProjectAnalyzer.AnalyzeAsync(project.Path);
        Assert.DoesNotContain(result.Findings, item => item.Code is "KNE101" or "KNE304");
        var missing = Assert.Single(result.Findings, item => item.Code == "KNE203");
        Assert.Equal("jump", missing.BlockId); Assert.EndsWith(".knegraph", missing.File);
        Assert.Contains("missing()", missing.Message);
    }
}
