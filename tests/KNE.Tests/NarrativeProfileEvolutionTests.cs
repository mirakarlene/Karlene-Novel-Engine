using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class NarrativeProfileEvolutionTests
{
    [Fact]
    public async Task InvalidCharacterReplacement_IsAtomic_AndDoesNotRegisterANewIdentity()
    {
        using var fixture = Scene("d(nil,'ready')");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("", runtime.RegisterCharacterForLua("{\"id\":\"alice\",\"name\":\"Original\"}"));
        Assert.Contains("Размер шрифта", runtime.UpdateCharacterForLua("{\"id\":\"alice\",\"name\":\"Broken\",\"font_size\":0}"));
        Assert.Equal("Original", runtime.PrepareDialogueForLua("", "alice", null)[0]);
        Assert.Contains("не зарегистрирован", runtime.UpdateCharacterForLua("{\"id\":\"bob\"}"));
        Assert.Contains("не зарегистрирован", runtime.CheckCharacterForLua("bob"));
        Assert.Equal("", runtime.CheckCharacterForLua("alice"));
    }

    [Fact]
    public async Task UpdateCharacter_ReplacesRegisteredPresentation_AndRetainsPriorFrameSnapshotAcrossWorkerReplay()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса"}')
            set_speaker("alice")
            d(nil,"before")
            update_character('{"id":"alice","name":"Алиса II","name_color":"#FF112233","text_alignment":"right","font_family":"Arial","font_size":37}')
            d(nil,"after")
            """);
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync(); var previous = runtime.Story;
        await runtime.AdvanceAsync();
        Assert.Equal("Алиса", previous.Speaker); Assert.Equal(24, previous.Character!.FontSize);
        var changed = new CharacterProfile { Id = "alice", Name = "Алиса II", NameColor = "#FF112233", TextAlignment = "right", FontFamily = "Arial", FontSize = 37 };
        Assert.Equal(changed, runtime.Story.Character); Assert.Equal("after", runtime.Story.Text);
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(await runtime.GetCheckpointAsync());
        Assert.Equal(changed, restored.Story.Character); Assert.Equal("after", restored.Story.Text);
        await using var restarted = new LuaRuntime(project, _ => { }, interactive: true);
        await restarted.StartAsync(); Assert.Equal("Алиса", restarted.Story.Speaker);
    }

    [Theory]
    [InlineData("update_character('{}')", "не зарегистрирован")]
    [InlineData("register_character('{}'); update_character('{\"id\":\"other\"}')", "не зарегистрирован")]
    [InlineData("register_character('{}'); update_character('{\"font_size\":0}')", "Размер шрифта")]
    [InlineData("register_character('{}'); update_character('{\"extra\":true}')", "extra")]
    [InlineData("set_speaker('missing')", "не зарегистрирован")]
    [InlineData("set_speaker(nil)", "ID персонажа")]
    [InlineData("update_character(nil)", "JSON")]
    public async Task InvalidProfileChangesAndSpeakerReferences_ReportSourceLocation(string command, string expected)
    {
        using var fixture = Scene(command);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(2, report.Line);
    }

    [Fact]
    public async Task DefaultSpeaker_AppliesToAllDialogueKinds_RespectsExplicitOverridesAndNarrator_AndReset()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса"}')
            register_character('{"id":"bob","name":"Боб"}')
            set_speaker("alice")
            d(nil,"inherited")
            kne_say(nil,"auto",nil,nil,0.5)
            kne_say(nil,"effect",nil,'{"kind":"wave"}')
            d("Manual","manual")
            kne_say(nil,"explicit","bob")
            kne_say(nil,"narrator",nil,nil,nil,true,true)
            d(nil,"still inherited")
            reset_speakers()
            d(nil,"reset")
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("alice", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); Assert.Equal("alice", runtime.Story.Character!.Id); Assert.Equal(.5, runtime.Story.RemainingSeconds);
        await runtime.TickAsync(.5); Assert.Equal("alice", runtime.Story.Character!.Id); Assert.Equal("wave", runtime.Story.TextEffect!.Kind);
        await runtime.AdvanceAsync(); Assert.Equal("Manual", runtime.Story.Speaker); Assert.Null(runtime.Story.Character);
        await runtime.AdvanceAsync(); Assert.Equal("bob", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); Assert.Equal("narrator", runtime.Story.Text); Assert.Equal("", runtime.Story.Speaker); Assert.Null(runtime.Story.Character);
        await runtime.AdvanceAsync(); Assert.Equal("alice", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); Assert.Equal("reset", runtime.Story.Text); Assert.Null(runtime.Story.Character); Assert.Equal("", runtime.Story.Speaker);
    }

    [Fact]
    public async Task SpeakerScope_IsInheritedByCallsJumpsAndParallelBranches_AndIsolatedFromCallerAndSiblings()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса"}')
            register_character('{"id":"bob","name":"Боб"}')
            set_speaker("alice")
            call(function()
                d(nil,"child inherits")
                set_speaker("bob")
                jump(function() d(nil,"jump inherits") end)
            end)
            d(nil,"parent restored")
            kne_parallel({function() set_speaker("bob"); kne_pause(0.5) end,
                function() kne_pause(0.25); d(nil,"sibling inherits") end})
            d(nil,"parent after parallel")
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("child inherits", runtime.Story.Text); Assert.Equal("alice", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); Assert.Equal("jump inherits", runtime.Story.Text); Assert.Equal("bob", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); Assert.Equal("parent restored", runtime.Story.Text); Assert.Equal("alice", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); await runtime.TickAsync(.25);
        Assert.Equal("sibling inherits", runtime.Story.Text); Assert.Equal("alice", runtime.Story.Character!.Id);
        await runtime.AdvanceAsync(); await runtime.TickAsync(.25);
        Assert.Equal("parent after parallel", runtime.Story.Text); Assert.Equal("alice", runtime.Story.Character!.Id);
    }

    [Theory]
    [InlineData(ScriptNodeKind.Branching, true)]
    [InlineData(ScriptNodeKind.Branching, false)]
    [InlineData(ScriptNodeKind.CompareVariables, true)]
    [InlineData(ScriptNodeKind.CompareVariables, false)]
    public async Task BranchingAndLogicCondition_CompareDistinctOperandKinds_AndExecuteWithoutDialogueStops(ScriptNodeKind kind, bool satisfied)
    {
        using var fixture = new TempProject();
        var comparison = new ScriptNode { Id = "check", Kind = kind, Variable = "left", CompareVariable = "right", Value = "7", Comparison = ScriptComparison.Equal, True = "yes", False = "no" };
        var graph = Chain(new() { Id = "left", Kind = ScriptNodeKind.SetVariable, Variable = "left", Value = satisfied ? "7" : "8" }, new() { Id = "right", Kind = ScriptNodeKind.SetVariable, Variable = "right", Value = "7" }, comparison);
        comparison.Next = null;
        graph.Nodes.Add(new() { Id = "yes", Kind = ScriptNodeKind.Dialogue, Text = "yes", Next = "end" });
        graph.Nodes.Add(new() { Id = "no", Kind = ScriptNodeKind.Dialogue, Text = "no", Next = "end" });
        string code = graph.Compile();
        Assert.Contains(kind == ScriptNodeKind.Branching ? "state[\"left\"] == 7" : "state[\"left\"] == state[\"right\"]", code);
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal(satisfied ? "yes" : "no", runtime.Story.Text); Assert.Equal("dialogue", runtime.Story.Kind);
    }

    [Fact]
    public void LegacyConditions_MigrateNarrativeNameButRetainLiteralLogicSemantics_AndNewOperandsRejectInvalidKeys()
    {
        var legacy = new ScriptNode { Id = "condition", Kind = ScriptNodeKind.Condition, Category = "narrative", Variable = "score", Value = "17", True = "end", False = "end" };
        var graph = Chain(legacy); legacy.Next = null;
        var narrative = VisualScript.Parse(graph.Serialize()); Assert.Equal(ScriptNodeKind.Branching, narrative.Nodes.Single(node => node.Id == legacy.Id).Kind);
        Assert.Contains("state[\"score\"] == 17", narrative.Compile());
        legacy.Category = "logic"; var logic = VisualScript.Parse(graph.Serialize()); Assert.Equal(ScriptNodeKind.Branching, logic.Nodes.Single(node => node.Id == legacy.Id).Kind);
        Assert.Contains("state[\"score\"] == 17", logic.Compile());
        legacy.Kind = ScriptNodeKind.CompareVariables; legacy.CompareVariable = "not a key";
        Assert.Contains("имя переменной", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        legacy.CompareVariable = "target"; Assert.Equal(new[] { "Да", "Нет" }, legacy.Outputs().Select(output => output.Label));
        legacy.SetOutput(1, "yes"); Assert.Equal("yes", legacy.False);
    }

    [Theory]
    [InlineData(ScriptComparison.NotEqual, 7, 7, false)]
    [InlineData(ScriptComparison.NotEqual, 7, 8, true)]
    [InlineData(ScriptComparison.Greater, 7, 7, false)]
    [InlineData(ScriptComparison.Greater, 8, 7, true)]
    [InlineData(ScriptComparison.Less, 7, 7, false)]
    [InlineData(ScriptComparison.Less, 6, 7, true)]
    [InlineData(ScriptComparison.GreaterOrEqual, 7, 7, true)]
    [InlineData(ScriptComparison.GreaterOrEqual, 6, 7, false)]
    [InlineData(ScriptComparison.LessOrEqual, 7, 7, true)]
    [InlineData(ScriptComparison.LessOrEqual, 8, 7, false)]
    public async Task VariableCondition_UsesCurrentSecondVariableForEachOperatorAndBoundary(ScriptComparison comparison, int left, int right, bool expected)
    {
        using var fixture = new TempProject();
        var condition = new ScriptNode { Id = "check", Kind = ScriptNodeKind.CompareVariables, Variable = "left", CompareVariable = "right", Comparison = comparison, True = "yes", False = "no" };
        var graph = Chain(new() { Id = "left", Kind = ScriptNodeKind.SetVariable, Variable = "left", Value = left.ToString() }, new() { Id = "right", Kind = ScriptNodeKind.SetVariable, Variable = "right", Value = right.ToString() }, condition);
        condition.Next = null;
        graph.Nodes.Add(new() { Id = "yes", Kind = ScriptNodeKind.Dialogue, Text = "yes", Next = "end" });
        graph.Nodes.Add(new() { Id = "no", Kind = ScriptNodeKind.Dialogue, Text = "no", Next = "end" });
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal(expected ? "yes" : "no", runtime.Story.Text);
    }

    [Fact]
    public async Task NewNarrativeEditors_ApplyPersistAndUndoProfileSpeakerNarratorAndDistinctConditionProperties()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/authoring.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            editor.AddBlock(ScriptNodeKind.UpdateCharacter); string id = editor.Document!.Graph!.Nodes.Last().Id;
            Extended<TextBox>(editor, "CharacterIdInput").Text = "alice";
            Extended<TextBox>(editor, "CharacterNameInput").Text = "Changed";
            Extended<TextBox>(editor, "CharacterFontSizeInput").Text = "39";
            Click(editor, "ApplyPropertiesButton"); Assert.Equal("alice", editor.Document.Graph.Nodes.Last().Character!.Id); Assert.Equal(39, editor.Document.Graph.Nodes.Last().Character!.FontSize);
            Click(editor, "UndoButton"); Assert.Equal("character", editor.Document.Graph.Nodes.Last().Character!.Id);
            Click(editor, "RedoButton"); Assert.Equal("Changed", editor.Document.Graph.Nodes.Last().Character!.Name);
            editor.AddBlock(ScriptNodeKind.SetSpeaker); Extended<TextBox>(editor, "DefaultSpeakerIdInput").Text = "alice"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal("alice", editor.Document.Graph.Nodes.Last().SpeakerId);
            editor.AddBlock(ScriptNodeKind.CompareVariables); Extended<TextBox>(editor, "BranchVariableInput").Text = "left"; Extended<TextBox>(editor, "ComparedVariableInput").Text = "right";
            Extended<ComboBox>(editor, "BranchComparisonInput").SelectedItem = nameof(ScriptComparison.Greater); Click(editor, "ApplyPropertiesButton");
            Assert.Equal("right", editor.Document.Graph.Nodes.Last().CompareVariable); Assert.Equal(ScriptComparison.Greater, editor.Document.Graph.Nodes.Last().Comparison);
            editor.AddBlock(ScriptNodeKind.Branching); Extended<TextBox>(editor, "BranchVariableInput").Text = "flag"; Extended<TextBox>(editor, "BranchValueInput").Text = "true";
            Extended<ComboBox>(editor, "BranchValueTypeInput").SelectedItem = nameof(ScriptValueKind.Boolean); Click(editor, "ApplyPropertiesButton");
            Assert.Equal(ScriptValueKind.Boolean, editor.Document.Graph.Nodes.Last().ValueKind);
            editor.AddBlock(ScriptNodeKind.Dialogue); Click(editor, "NarratorButton");
            Assert.True(editor.Document.Graph.Nodes.Last().Narration);
            var narratorGraph = Chain(new ScriptNode { Id = "narrator", Kind = ScriptNodeKind.Dialogue, Narration = editor.Document.Graph.Nodes.Last().Narration, Text = "Narration" });
            Assert.Contains("kne_say(nil, \"Narration\", nil, nil, nil, true, true)", narratorGraph.Compile());
            ((TextBox)editor.FindName("SpeakerInput")).Text = "Manual"; Click(editor, "ApplyPropertiesButton");
            Assert.False(editor.Document.Graph.Nodes.Last().Narration); Assert.Equal("Manual", editor.Document.Graph.Nodes.Last().Speaker);
            Click(editor, "NarratorButton");
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal("Changed", editor.Document.Graph!.Nodes.Single(node => node.Id == id).Character!.Name);
            Assert.True(editor.Document.Graph.Nodes.Last().Narration); editor.Clear();
        });
    }

    [Fact]
    public async Task ProfilePicker_SetSpeakerStoresIdentity_AndUpdateCharacterCopiesAllRegisteredParameters()
    {
        using var fixture = new TempProject();
        var profile = new CharacterProfile { Id = "alice", Name = "Алиса", NameColor = "#FF554433", TextAlignment = "right", FontFamily = "Arial", FontSize = 36 };
        var graph = Chain(new ScriptNode { Id = "registered", Kind = ScriptNodeKind.RegisterCharacter, Character = profile });
        string path = fixture.Write("game/scenes/picker.knegraph", graph.Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            foreach (var kind in new[] { ScriptNodeKind.SetSpeaker, ScriptNodeKind.UpdateCharacter })
            {
                editor.AddBlock(kind);
                Extended<Button>(editor, "SelectNarrativeCharacterButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var list = (ListBox)editor.FindName("CharacterProfiles");
                while (list.Items.Count == 0) await Task.Delay(10, timeout.Token);
                Assert.Single(list.Items.Cast<object>()); list.SelectedIndex = 0; Click(editor, "ChooseCharacterButton");
                if (kind == ScriptNodeKind.SetSpeaker) Assert.Equal("alice", editor.Document!.Graph!.Nodes.Last().SpeakerId);
                else Assert.Equal(profile, editor.Document!.Graph!.Nodes.Last().Character);
            }
            editor.Clear();
        });
    }

    private static VisualScript Chain(params ScriptNode[] nodes)
    {
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = nodes.FirstOrDefault()?.Id ?? "end" }, .. nodes, new() { Id = "end", Kind = ScriptNodeKind.End }] };
        for (int i = 0; i < nodes.Length; i++) nodes[i].Next = i + 1 < nodes.Length ? nodes[i + 1].Id : "end";
        return graph;
    }
    private static TempProject Scene(string source)
    {
        var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main()\n" + source + "\nend"); return fixture;
    }
    private static T Extended<T>(ScriptEditor editor, string name) where T : FrameworkElement => ((StackPanel)editor.FindName("ExtendedProperties")).Children.OfType<T>().Single(item => item.Name == name);
    private static void Click(ScriptEditor editor, string name) => ((Button)editor.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
