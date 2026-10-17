using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class TextInputSettingsTests
{
    [Theory]
    [InlineData("player_name")]
    [InlineData("_answer2")]
    public void Settings_AcceptLuaIdentifiers_AndPreserveDefaults(string variable)
    {
        var settings = new TextInputSettings { Variable = variable };
        settings.Validate();
        Assert.Equal(32, settings.MaximumLength);
        Assert.True(settings.AutoFocus);
        Assert.False(settings.IsPassword);
        Assert.Equal(TextInputValidationMode.None, settings.ValidationMode);
        Assert.True(settings.ValidateAnswer(""));
        Assert.True(settings.ValidateAnswer("имя 'quoted' \" ; --"));
    }

    [Theory]
    [InlineData("name space")]
    [InlineData("1name")]
    [InlineData("имя")]
    [InlineData("end")]
    [InlineData("")]
    public void Settings_RejectInvalidStateKeys(string variable) => Assert.Throws<ArgumentException>(() => new TextInputSettings { Variable = variable }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Settings_RejectLengthOutsideOneTo256(int maximum) => Assert.Throws<ArgumentException>(() => new TextInputSettings { MaximumLength = maximum }.Validate());

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(256)]
    public void Answer_LengthUsesWholeUnicodeGraphemes_AtAndAboveBoundary(int maximum)
    {
        var settings = new TextInputSettings { MaximumLength = maximum };
        string answer = string.Concat(Enumerable.Repeat("👩‍🚀", maximum));
        settings.Validate();
        Assert.Equal(maximum, TextInputSettings.CountCharacters(answer));
        Assert.True(settings.ValidateAnswer(answer));
        Assert.False(settings.ValidateAnswer(answer + "é"));
    }

    [Fact]
    public void Answer_ExactUsesOrdinalCaseRule_AndRegexMatchesWholeAnswer()
    {
        var exact = new TextInputSettings { ValidationMode = TextInputValidationMode.Exact, ExpectedValue = "Secret" };
        Assert.True(exact.ValidateAnswer("Secret"));
        Assert.False(exact.ValidateAnswer("secret"));
        Assert.True((exact with { IgnoreCase = true }).ValidateAnswer("secret"));
        var regex = new TextInputSettings { ValidationMode = TextInputValidationMode.Regex, Pattern = "[0-9]{3}" };
        regex.Validate();
        Assert.True(regex.ValidateAnswer("123"));
        Assert.False(regex.ValidateAnswer("x123"));
        Assert.False(regex.ValidateAnswer("123\n"));
        Assert.Throws<ArgumentException>(() => (regex with { Pattern = "[" }).Validate());
        Assert.False((regex with { Pattern = "(a+)+", MaximumLength = 256 }).ValidateAnswer(new string('a', 255) + "!"));
    }

    [Theory]
    [InlineData("000-000", "123-456", "123456")]
    [InlineData("LL_AA", "Ab_9z", "Яb_9z")]
    [InlineData("\\0&", "0👩‍🚀", "1👩‍🚀")]
    public void Answer_MaskHonorsTokensEscapesAndLiterals(string mask, string valid, string invalid)
    {
        var settings = new TextInputSettings { InputMask = mask };
        settings.Validate();
        Assert.True(settings.ValidateAnswer(valid));
        Assert.False(settings.ValidateAnswer(invalid));
        Assert.False(settings.ValidateAnswer(valid + "x"));
        Assert.False(settings.ValidateAnswer(""));
    }

    [Fact]
    public void Settings_RejectMalformedMasksUnknownModesAndOrphanedBranches()
    {
        Assert.Throws<ArgumentException>(() => new TextInputSettings { InputMask = "\\" }.Validate());
        Assert.Throws<ArgumentException>(() => new TextInputSettings { InputMask = "000", MaximumLength = 2 }.Validate());
        Assert.Throws<ArgumentException>(() => new TextInputSettings { ValidationMode = (TextInputValidationMode)100 }.Validate());
        Assert.Throws<ArgumentException>(() => new TextInputSettings { HasCancelOutput = true }.Validate());
        Assert.Throws<ArgumentException>(() => new TextInputSettings { HasErrorOutput = true }.Validate());
    }
}

public sealed class TextInputRuntimeTests
{
    [Fact]
    public async Task Input_WorkerTransportsPasswordMaskAndCancelFlag_AndReplaysWithoutWritingAnswer()
    {
        var settings = new TextInputSettings { Variable = "password", InputMask = "000-000", IsPassword = true, AutoFocus = false, BranchOnResult = true, HasCancelOutput = true };
        using var fixture = Scene(settings, "d(nil,result)");
        fixture.Write("game/scenes/main.krl", "function main()\nlocal result = " + Command(settings) + "\nd(nil,result)\nend");
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal(settings, worker.Story.Input);
        await worker.SubmitInputAsync("123-456", cancel: true);
        Assert.Equal("cancel", worker.Story.Text);
        var checkpoint = await worker.GetCheckpointAsync();
        Assert.True(Assert.Single(checkpoint.History).Cancel);
        Assert.False(checkpoint.State.TryGetProperty("password", out _));
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("cancel", restored.Story.Text);
        Assert.False((await restored.GetCheckpointAsync()).State.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Input_WorkerStoresRawAnswerAsData_AndCheckpointReplaysSubmission()
    {
        using var fixture = Scene(new TextInputSettings { Prompt = "Ваше имя?", Placeholder = "Имя", MaximumLength = 256 }, "d(nil,state.player_answer)");
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal("input", worker.Story.Kind);
        Assert.Equal("Ваше имя?", worker.Story.Text);
        Assert.Equal("Имя", worker.Story.Input!.Placeholder);
        Assert.False(worker.Story.CanAdvance);
        Assert.NotNull(worker.Story.Identity);
        string answer = "name\"; state.hacked=true; -- 👩‍🚀";
        await worker.SubmitInputAsync(answer);
        Assert.Equal(answer, worker.Story.Text);
        var checkpoint = await worker.GetCheckpointAsync();
        Assert.Equal(answer, checkpoint.State.GetProperty("player_answer").GetString());
        Assert.False(checkpoint.State.TryGetProperty("hacked", out _));
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(answer, restored.Story.Text);
        Assert.Equal(answer, (await restored.GetCheckpointAsync()).State.GetProperty("player_answer").GetString());
    }

    [Fact]
    public async Task Input_InvalidRetryPreservesPromptFeedbackIdentityAndStateAcrossTicksAndRestore()
    {
        var settings = new TextInputSettings { ValidationMode = TextInputValidationMode.Exact, ExpectedValue = "123", FailureMessage = "Неверный код" };
        using var fixture = Scene(settings, "d(nil,state.player_answer)", "state.player_answer='old'; kne_async({function() kne_pause(1); state.background=true end})");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        long presentation = runtime.Story.PresentationId;
        await runtime.SubmitInputAsync("bad");
        Assert.Equal("input", runtime.Story.Kind);
        Assert.Equal("Неверный код", runtime.Story.InputFeedback);
        await runtime.TickAsync(1);
        Assert.Equal(presentation, runtime.Story.PresentationId);
        Assert.Equal("Неверный код", runtime.Story.InputFeedback);
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal("old", checkpoint.State.GetProperty("player_answer").GetString());
        Assert.True(checkpoint.State.GetProperty("background").GetBoolean());
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("Неверный код", restored.Story.InputFeedback);
        Assert.Equal(settings, restored.Story.Input);
        await restored.SubmitInputAsync("123");
        Assert.Equal("123", restored.Story.Text);
        Assert.Null(restored.Story.Input);
    }

    [Theory]
    [InlineData("123", false, "success")]
    [InlineData("bad", false, "error")]
    [InlineData("", true, "cancel")]
    public async Task Input_ExplicitOutputsRouteOutcome_AndOnlySuccessChangesVariable(string answer, bool cancel, string outcome)
    {
        using var fixture = new TempProject();
        var graph = InputGraph(new TextInputSettings { ValidationMode = TextInputValidationMode.Exact, ExpectedValue = "123", BranchOnResult = true, HasErrorOutput = true, HasCancelOutput = true });
        fixture.Write("game/scenes/main.krl", graph.Compile());
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.SubmitInputAsync(answer, cancel);
        Assert.Equal(outcome, runtime.Story.Text);
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal(outcome == "success", checkpoint.State.TryGetProperty("player_answer", out var stored));
        if (outcome == "success") Assert.Equal(answer, stored.GetString());
        Assert.Equal(new[] { "Успех", "Ошибка", "Отмена" }, graph.Nodes.Single(n => n.Kind == ScriptNodeKind.TextInput).Outputs().Select(p => p.Label));
    }

    [Fact]
    public async Task Input_AbsentCancelRejectsCancellationAndAdvance_AndOptionalCancelPortMapsToFalse()
    {
        using var fixture = Scene(new TextInputSettings(), "d(nil,'next')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SubmitInputAsync("", cancel: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AdvanceAsync());
        Assert.Equal("input", runtime.Story.Kind);
        Assert.Empty((await runtime.GetCheckpointAsync()).History);
        var node = new ScriptNode { Kind = ScriptNodeKind.TextInput, Input = new() { BranchOnResult = true, HasCancelOutput = true } };
        node.SetOutput(0, "success"); node.SetOutput(1, "cancel");
        Assert.Equal(new[] { ("Успех", (string?)"success"), ("Отмена", (string?)"cancel") }, node.Outputs());
        Assert.Null(node.True);
        Assert.Equal("cancel", node.False);
    }

    [Fact]
    public async Task Input_ForegroundParallelWaitsForAnswer_WhileDetachedInputReportsSourceError()
    {
        string input = Command(new TextInputSettings());
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", $"function main() kne_parallel({{function() {input} end,function() kne_pause(1); state.timer=true end}}); d(nil,'done') end");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.TickAsync(1);
        Assert.Equal("input", runtime.Story.Kind);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("timer").GetBoolean());
        await runtime.SubmitInputAsync("ok");
        Assert.Equal("done", runtime.Story.Text);
        fixture.Write("game/scenes/main.krl", $"function main()\nkne_async({{function() {input} end}})\nd(nil,'waiting')\nend");
        await using var detached = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => detached.StartAsync());
        Assert.Contains("фоновой", error.Message);
        Assert.Equal("game/scenes/main.krl", GameErrorReport.TryGet(error)!.File);
        Assert.Equal(2, GameErrorReport.TryGet(error)!.Line);
    }

    [Fact]
    public async Task Input_InvalidRegexIsRejectedAtOriginalFileAndBlock_AndPendingCheckpointDetectsTampering()
    {
        using var fixture = Scene(new TextInputSettings(), "d(nil,'after')");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("input", restored.Story.Kind);
        await using var tampered = Runtime(project);
        await Assert.ThrowsAsync<InvalidDataException>(() => tampered.RestoreAsync(checkpoint with { Frame = checkpoint.Frame with { Input = new() { Variable = "different" } } }));
        var graph = InputGraph(new TextInputSettings { ValidationMode = TextInputValidationMode.Regex, Pattern = "[" });
        var graphError = Assert.Throws<InvalidDataException>(() => graph.Compile());
        Assert.Contains("input", graphError.Message);
        Assert.Contains("регулярное", graphError.Message);
        fixture.Write("game/scenes/main.krl", "function main()\ntext_input('{\"pattern\":\"[\",\"validation_mode\":\"Regex\"}')\nend");
        await using var invalid = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => invalid.StartAsync());
        Assert.Equal(2, GameErrorReport.TryGet(error)!.Line);
        Assert.Contains("регулярное", error.Message);
    }

    [Fact]
    public async Task Input_CalledFunctionReturnsToCaller_AndRejectsSubmissionOutsideInputWithoutRecordingHistory()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\ncall(ask)\nd(nil,'returned:'..state.player_answer)\nend\nfunction ask()\n" + Command(new TextInputSettings()) + "\nend");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SubmitInputAsync("early"));
        await runtime.StartAsync();
        Assert.Equal("input", runtime.Story.Kind);
        await runtime.SubmitInputAsync("answer");
        Assert.Equal("returned:answer", runtime.Story.Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SubmitInputAsync("late"));
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Single(checkpoint.History);
        Assert.Equal("answer", checkpoint.State.GetProperty("player_answer").GetString());
    }

    private static VisualScript InputGraph(TextInputSettings settings) => new()
    {
        Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "input" },
            new() { Id = "input", Kind = ScriptNodeKind.TextInput, Input = settings, Next = "success", True = "error", False = "cancel" },
            new() { Id = "success", Kind = ScriptNodeKind.Dialogue, Text = "success", Next = "end" },
            new() { Id = "error", Kind = ScriptNodeKind.Dialogue, Text = "error", Next = "end" },
            new() { Id = "cancel", Kind = ScriptNodeKind.Dialogue, Text = "cancel", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ]
    };

    private static string Command(TextInputSettings settings) => "text_input([=[" + JsonSerializer.Serialize(settings, EngineSettings.JsonOptions) + "]=])";
    private static TempProject Scene(TextInputSettings settings, string after, string before = "")
    {
        var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n" + before + "\n" + Command(settings) + "\n" + after + "\nend");
        return fixture;
    }
    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true);
}
