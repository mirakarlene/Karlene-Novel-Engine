using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ChoiceSettingsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3600.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Timer_RejectsInvalidDurations(double seconds) => Assert.Throws<ArgumentException>(() => new ChoiceSettings { Seconds = seconds }.Validate());

    [Fact]
    public void Timer_AcceptsBoundaryAndBothDisplays_AndRejectsMissingDefaultOrUnknownDisplay()
    {
        new ChoiceSettings { Seconds = .001, TimerDisplay = "bar", DefaultOption = 1 }.Validate(1);
        new ChoiceSettings { Seconds = 3600, TimerDisplay = "numeric", DefaultOption = 20 }.Validate(20);
        Assert.Throws<ArgumentException>(() => new ChoiceSettings { DefaultOption = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new ChoiceSettings { DefaultOption = 2 }.Validate(1));
        Assert.Throws<ArgumentException>(() => new ChoiceSettings { TimerDisplay = "unknown" }.Validate());
        Assert.Equal(10, new ChoiceSettings().Seconds);
    }

    [Fact]
    public void Locks_ValidateIdentifiersLiteralsDiscriminatorsAndHintBounds()
    {
        new OptionLockSettings { Variable = "flag_1", Value = "false" }.Validate();
        new OptionLockSettings { Variable = "score", CompareVariable = "required", ValueType = "number" }.Validate();
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { Variable = "state.x;error()" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { CompareVariable = "bad name" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { ValueType = "number", Value = "NaN" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { Value = "yes" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { ValueType = "unknown" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { Effect = "unknown" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { HintPlacement = "unknown" }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { Operator = (ScriptComparison)99 }.Validate());
        Assert.Throws<ArgumentException>(() => new OptionLockSettings { Hint = new string('x', 16001) }.Validate());
        new OptionLockSettings { Hint = new string('x', 16000) }.Validate();
    }

    [Fact]
    public void ChoiceRequest_RejectsEmptyNullAndOversizedLists_AndKeepsOriginalOrdering()
    {
        Assert.Throws<ArgumentException>(() => new ChoiceRequest().Validate());
        Assert.Throws<ArgumentException>(() => new ChoiceRequest { Options = [new("")] }.Validate());
        Assert.Throws<ArgumentException>(() => new ChoiceRequest { Options = Enumerable.Repeat(new ChoiceRequestOption("x"), 21).ToArray() }.Validate());
        var request = new ChoiceRequest { Options = Enumerable.Range(1, 20).Select(i => new ChoiceRequestOption(i.ToString())).ToArray() };
        request.Validate();
        Assert.Equal("1", request.Options[0].Text);
        Assert.Equal("20", request.Options[^1].Text);
    }
}

public sealed class ChoiceRuntimeTests
{
    [Fact]
    public async Task Choice_SingleEnabledAnswerHasNoCountdown_AndReturnsOriginalIndexOne()
    {
        using var fixture = Scene(new ChoiceRequest { Prompt = "Only?", Options = [new("Only")] });
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Single(runtime.Story.Options);
        Assert.True(Assert.Single(runtime.Story.ChoiceOptions!).IsEnabled);
        Assert.False(runtime.Story.NeedsTick);
        Assert.Null(runtime.Story.ChoiceTimer);
        await runtime.ChooseAsync(1);
        Assert.Equal("1", runtime.Story.Text);
    }

    [Theory]
    [InlineData("state.value=true", "boolean", "true", ScriptComparison.Equal, "", true)]
    [InlineData("state.value=false", "boolean", "true", ScriptComparison.Equal, "", false)]
    [InlineData("state.value=5", "number", "4", ScriptComparison.Greater, "", true)]
    [InlineData("state.value=4", "number", "4", ScriptComparison.Greater, "", false)]
    [InlineData("state.value=4", "number", "4", ScriptComparison.GreaterOrEqual, "", true)]
    [InlineData("state.value=3", "number", "4", ScriptComparison.Less, "", true)]
    [InlineData("state.value=4", "number", "4", ScriptComparison.LessOrEqual, "", true)]
    [InlineData("state.value='A'", "string", "a", ScriptComparison.NotEqual, "", true)]
    [InlineData("state.value=5; state.other=5", "number", "0", ScriptComparison.Equal, "other", true)]
    [InlineData("state.value=5; state.other=7", "number", "0", ScriptComparison.Greater, "other", false)]
    [InlineData("", "boolean", "true", ScriptComparison.Equal, "", false)]
    public async Task Locks_ResolveTypedPredicatesAgainstState_AndRetainOriginalOptionIndices(string before, string type, string value, ScriptComparison comparison, string compareVariable, bool locked)
    {
        var request = Request(new OptionLockSettings { Variable = "value", ValueType = type, Value = value, Operator = comparison, CompareVariable = compareVariable });
        using var fixture = Scene(request, before);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal(new[] { "Locked", "Available" }, runtime.Story.Options);
        Assert.Equal(!locked, runtime.Story.ChoiceOptions![0].IsEnabled);
        Assert.True(runtime.Story.ChoiceOptions[1].IsEnabled);
        Assert.NotNull(runtime.Story.Identity);
        await runtime.ChooseAsync(2);
        Assert.Equal("2", runtime.Story.Text);
    }

    [Fact]
    public async Task Locks_DisabledSelectionCannotAdvanceOrRecordHistory_AndHintsEffectsAreFrozenAtPresentation()
    {
        var request = Request(new OptionLockSettings { ShowHint = true, Hint = "Need key", Effect = "glitch", HintPlacement = "overlay" });
        using var fixture = Scene(request, "state.locked=true; kne_async({function() kne_pause(1); state.locked=false end})");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        long presentation = runtime.Story.PresentationId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ChooseAsync(1));
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        await runtime.TickAsync(1);
        Assert.False(runtime.Story.ChoiceOptions![0].IsEnabled);
        Assert.Equal("Need key", runtime.Story.ChoiceOptions[0].Hint);
        Assert.Equal("overlay", runtime.Story.ChoiceOptions[0].HintPlacement);
        Assert.Equal("glitch", runtime.Story.ChoiceOptions[0].Effect);
        Assert.Equal(presentation, runtime.Story.PresentationId);
        Assert.False((await runtime.GetCheckpointAsync()).State.GetProperty("locked").GetBoolean());
        await runtime.ChooseAsync(2);
        Assert.Equal("2", runtime.Story.Text);
    }

    [Theory]
    [InlineData("bar")]
    [InlineData("numeric")]
    public async Task Timer_ExpiresExactlyAtBoundary_UsesEnabledOriginalDefault_AndKeepsPresentationAcrossTicks(string display)
    {
        using var fixture = Scene(Request(timer: new ChoiceSettings { Seconds = 2, DefaultOption = 2, TimerDisplay = display }));
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        long presentation = runtime.Story.PresentationId;
        Assert.True(runtime.Story.NeedsTick);
        Assert.Equal(2, runtime.Story.ChoiceTimer!.RemainingSeconds);
        Assert.Equal(display, runtime.Story.ChoiceTimer.Display);
        await runtime.TickAsync(1.999);
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal(presentation, runtime.Story.PresentationId);
        Assert.InRange(runtime.Story.ChoiceTimer!.RemainingSeconds, .00099, .00101);
        await runtime.TickAsync(.001);
        Assert.Equal("2", runtime.Story.Text);
        Assert.Null(runtime.Story.ChoiceTimer);
        Assert.False(runtime.Story.NeedsTick);
    }

    [Fact]
    public async Task Timer_BlockedDefaultFallsBackToFirstAvailable_AndManualChoiceCancelsCountdown()
    {
        var request = Request(new OptionLockSettings(), new ChoiceSettings { Seconds = 1 });
        request = request with { Options = request.Options.Append(new ChoiceRequestOption("Third available")).ToArray() };
        using var fixture = Scene(request, "state.locked=true");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        await runtime.TickAsync(1);
        Assert.Equal("2", runtime.Story.Text);
        await using var manual = Runtime(project);
        await manual.StartAsync();
        await manual.ChooseAsync(2);
        await manual.TickAsync(1);
        Assert.Equal("2", manual.Story.Text);
        Assert.Single((await manual.GetCheckpointAsync()).History);
    }

    [Fact]
    public async Task Timer_TickPastDeadlineSelectsDefaultWithoutWaitingForExactClockEquality()
    {
        using var fixture = Scene(Request(timer: new ChoiceSettings { Seconds = 2, DefaultOption = 2 }));
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.TickAsync(3);
        Assert.Equal("2", runtime.Story.Text);
        Assert.Null(runtime.Story.ChoiceTimer);
    }

    [Fact]
    public async Task Timer_PendingCheckpointReplaysRemainingTimeLocksAndOriginalIndex_AndDetectsTampering()
    {
        using var fixture = Scene(Request(new OptionLockSettings(), new ChoiceSettings { Seconds = 2, DefaultOption = 1 }), "state.locked=true");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        await runtime.TickAsync(.75);
        var checkpoint = await runtime.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(1.25, restored.Story.ChoiceTimer!.RemainingSeconds);
        Assert.False(restored.Story.ChoiceOptions![0].IsEnabled);
        await restored.TickAsync(1.25);
        Assert.Equal("2", restored.Story.Text);
        var after = await restored.GetCheckpointAsync();
        await using var replay = Runtime(project);
        await replay.RestoreAsync(after);
        Assert.Equal("2", replay.Story.Text);
        await using var tampered = Runtime(project);
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.RestoreAsync(checkpoint with { Frame = checkpoint.Frame with { ChoiceTimer = checkpoint.Frame.ChoiceTimer! with { RemainingSeconds = 2 } } }));
    }

    [Fact]
    public async Task Worker_TransportsLocksTimerAndSaveReplay_AndRejectsDisabledWithoutKillingSession()
    {
        using var fixture = Scene(Request(new OptionLockSettings(), new ChoiceSettings { Seconds = 2, TimerDisplay = "numeric" }), "state.locked=true");
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        int? process = worker.WorkerProcessId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ChooseAsync(1));
        Assert.Equal(process, worker.WorkerProcessId);
        Assert.Equal("choice", worker.Story.Kind);
        await worker.TickAsync(.5);
        Assert.Equal(1.5, worker.Story.ChoiceTimer!.RemainingSeconds);
        var checkpoint = await worker.GetCheckpointAsync();
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("numeric", restored.Story.ChoiceTimer!.Display);
        Assert.False(restored.Story.ChoiceOptions![0].IsEnabled);
        await restored.TickAsync(1.5);
        Assert.Equal("2", restored.Story.Text);
    }

    [Fact]
    public async Task Timer_CalledFunctionReturnsToCaller_AndDetachedChoiceReportsOriginalSource()
    {
        var request = Request(timer: new ChoiceSettings { Seconds = 1 });
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\ncall(ask)\nd(nil,'returned:'..state.answer)\nend\nfunction ask()\nstate.answer=" + Command(request) + "\nend");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.TickAsync(1);
        Assert.Equal("returned:1", runtime.Story.Text);
        fixture.Write("game/scenes/main.krl", "function main()\nkne_async({function() " + Command(request) + " end})\nd(nil,'waiting')\nend");
        await using var detached = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => detached.StartAsync());
        Assert.Contains("фоновой", error.Message);
        Assert.Equal(2, GameErrorReport.TryGet(error)!.Line);
    }

    [Fact]
    public async Task Choice_AllLockedAndMistypedRelationalPredicatesReportActionableSourceErrors()
    {
        var all = Request(new OptionLockSettings()) with { Options = [new("A", new()), new("B", new())] };
        using var fixture = Scene(all, "state.locked=true");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Все варианты", error.Message);
        Assert.Equal("game/scenes/main.krl", GameErrorReport.TryGet(error)!.File);
        Assert.Equal(3, GameErrorReport.TryGet(error)!.Line);
        fixture.Write("game/scenes/main.krl", "function main()\n" + Command(Request(new OptionLockSettings { Variable = "missing", ValueType = "number", Value = "1", Operator = ScriptComparison.Greater })) + "\nend");
        await using var invalid = Runtime(await fixture.LoadAsync());
        Assert.Contains("числовые", (await Assert.ThrowsAnyAsync<Exception>(() => invalid.StartAsync())).Message);
    }

    [Fact]
    public async Task Choice_TextAndHintsRemainData_AndLegacyChoiceRetainsCheckpointCompatibility()
    {
        string text = "'\"; state.hacked=true; -- <tag>";
        using var fixture = Scene(Request(new OptionLockSettings { ValueType = "string", Value = text, Variable = "value", ShowHint = false, Hint = "hidden" }) with { Prompt = text }, "state.value='different'");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        Assert.Equal(text, runtime.Story.Text);
        Assert.Equal("", runtime.Story.ChoiceOptions![0].Hint);
        Assert.False((await runtime.GetCheckpointAsync()).State.TryGetProperty("hacked", out _));
        fixture.Write("game/scenes/main.krl", "function main()\nlocal answer=choice('old',{'one','two'})\nd(nil,tostring(answer))\nend");
        project = await fixture.LoadAsync();
        await using var legacy = Runtime(project);
        await legacy.StartAsync();
        Assert.Null(legacy.Story.ChoiceOptions);
        Assert.Null(legacy.Story.ChoiceTimer);
        var checkpoint = await legacy.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        await restored.ChooseAsync(2);
        Assert.Equal("2", restored.Story.Text);
    }

    internal static ChoiceRequest Request(OptionLockSettings? settings = null, ChoiceSettings? timer = null) =>
        new() { Prompt = "Question?", Options = [new("Locked", settings), new("Available")], Timer = timer };
    internal static string Command(ChoiceRequest request) => "kne_choice([==[" + JsonSerializer.Serialize(request, EngineSettings.JsonOptions) + "]==])";
    private static TempProject Scene(ChoiceRequest request, string before = "")
    {
        var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n" + before + "\nlocal answer=" + Command(request) + "\nd(nil,tostring(answer))\nend");
        return fixture;
    }
    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true);
}

public sealed class ChoiceCompilerTests
{
    [Fact]
    public async Task TimedChoice_MissingConfigurationUsesTenSecondsAndFirstAnswer()
    {
        using var fixture = new TempProject();
        var graph = Graph();
        graph.Nodes.Single(n => n.Kind == ScriptNodeKind.TimedChoice).ChoiceSettings = null;
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(10, runtime.Story.ChoiceTimer!.Seconds);
        Assert.Equal("bar", runtime.Story.ChoiceTimer.Display);
        await runtime.TickAsync(10);
        Assert.Equal("first", runtime.Story.Text);
    }

    [Fact]
    public async Task TimedChoice_CompilesDistinctPortsAndBranchTargets_WithSourceIdentityAndPersistedLocks()
    {
        using var fixture = new TempProject();
        var graph = Graph();
        string json = graph.Serialize();
        var parsed = VisualScript.Parse(json);
        var node = parsed.Nodes.Single(n => n.Kind == ScriptNodeKind.TimedChoice);
        Assert.Equal(2, node.Outputs().Count());
        Assert.Equal("Выбор с таймером", node.Title);
        Assert.Equal("numeric", node.ChoiceSettings!.TimerDisplay);
        node.SetOutput(1, "second");
        string code = parsed.Compile();
        Assert.Contains("kne_choice", code);
        Assert.DoesNotContain("local answer = choice(", code);
        var project = await VisualScriptTests.LoadGraphAsync(fixture, parsed);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal("choice", runtime.Story.Identity!.BlockId);
        await runtime.TickAsync(1);
        Assert.Equal("second", runtime.Story.Text);
    }

    [Fact]
    public void Choice_CompilerPreservesLegacyCommand_RejectsBadDefaultWithBlockIdentity_AndRoundTripsOptionLocks()
    {
        var graph = Graph();
        var node = graph.Nodes.Single(n => n.Id == "choice");
        node.ChoiceSettings = new() { DefaultOption = 3 };
        var error = Assert.Throws<InvalidDataException>(() => graph.Compile());
        Assert.Contains("choice", error.Message);
        node.Kind = ScriptNodeKind.Choice;
        node.ChoiceSettings = null;
        Assert.Contains("local answer = choice(", graph.Compile());
        node.Options[0].Lock = new() { Effect = "scramble", ShowHint = true, Hint = "hint" };
        var parsed = VisualScript.Parse(graph.Serialize()).Nodes.Single(n => n.Id == "choice");
        Assert.Equal(node.Options[0].Lock, parsed.Options[0].Lock);
        Assert.Contains("kne_choice", graph.Compile());
        parsed.Options[0].Lock = new() { Effect = "bad" };
        graph.Nodes[1] = parsed;
        Assert.Contains("choice", Assert.Throws<InvalidDataException>(() => graph.Serialize()).Message);
    }

    private static VisualScript Graph() => new()
    {
        Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "choice" },
            new() { Id = "choice", Kind = ScriptNodeKind.TimedChoice, Text = "Where?", ChoiceSettings = new() { Seconds = 1, DefaultOption = 2, TimerDisplay = "numeric" },
                Options = [new() { Text = "first", Target = "first" }, new() { Text = "second", Target = "second" }] },
            new() { Id = "first", Kind = ScriptNodeKind.Dialogue, Text = "first", Next = "end" },
            new() { Id = "second", Kind = ScriptNodeKind.Dialogue, Text = "second", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ]
    };
}
