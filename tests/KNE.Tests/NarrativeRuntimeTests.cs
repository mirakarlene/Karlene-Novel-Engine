using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class NarrativeRuntimeTests
{
    [Fact]
    public async Task Character_RegistersWithoutVisuals_ResolvesExplicitProfile_AndKeepsManualSpeakerAndNarratorIndependent()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса","name_color":"#FF336699","text_alignment":"right","font_family":"Arial","font_size":31}')
            register_character('{"id":"alice","name":"Алиса","name_color":"#FF336699","text_alignment":"right","font_family":"Arial","font_size":31}')
            kne_say("ignored", "profile", "alice")
            kne_say("Manual", "manual")
            d(nil, "narrator")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("Алиса", runtime.Story.Speaker);
        Assert.Equal(new CharacterProfile { Id = "alice", Name = "Алиса", NameColor = "#FF336699", TextAlignment = "right", FontFamily = "Arial", FontSize = 31 }, runtime.Story.Character);
        Assert.Null(runtime.Visuals.Character);
        Assert.Null(runtime.Visuals.Background);
        await runtime.AdvanceAsync();
        Assert.Equal("Manual", runtime.Story.Speaker);
        Assert.Null(runtime.Story.Character);
        await runtime.AdvanceAsync();
        Assert.Equal("", runtime.Story.Speaker);
        Assert.Equal("narrator", runtime.Story.Text);
    }

    [Theory]
    [InlineData("register_character('{\"id\":\"bad id\"}')", "ID персонажа")]
    [InlineData("register_character('{\"id\":\"русский\"}')", "ID персонажа")]
    [InlineData("register_character('{\"id\":\"end\"}')", "ID персонажа")]
    [InlineData("register_character('{\"font_size\":0}')", "Размер шрифта")]
    [InlineData("register_character('{\"name_color\":\"invalid-color\"}')", "цвет текста")]
    [InlineData("register_character('{\"text_alignment\":\"up\"}')", "Позиция текста")]
    [InlineData("register_character('{\"font_family\":\"https://external/font\"}')", "локальное")]
    [InlineData("register_character('{\"unknown\":1}')", "unknown")]
    [InlineData("register_character('{}'); register_character('{\"name\":\"Different\"}')", "уже зарегистрирован")]
    [InlineData("kne_say(nil,'missing','unknown')", "не зарегистрирован")]
    public async Task Character_InvalidDeclarationsAndMissingExplicitProfiles_ReportOriginalFileLine(string command, string message)
    {
        using var fixture = Scene(command);
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File);
        Assert.Equal(2, report.Line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TextPause_RetainsPriorDialogueAndProfile_ExpiresAtExactLogicalBoundary_AndHonorsClickPermission(bool skippable)
    {
        using var fixture = Scene($$"""
            register_character('{"id":"alice","name":"Алиса"}')
            kne_say(nil,"remembered","alice")
            text_pause(2, {{(skippable ? "true" : "false")}})
            d(nil,"after")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.AdvanceAsync();
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.Equal("remembered", runtime.Story.Text);
        Assert.Equal("alice", runtime.Story.Character!.Id);
        Assert.Equal(skippable, runtime.Story.CanAdvance);
        Assert.True(runtime.Story.NeedsTick);
        await runtime.TickAsync(1.999);
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.InRange(runtime.Story.RemainingSeconds!.Value, .00099, .00101);
        if (skippable) await runtime.AdvanceAsync();
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AdvanceAsync());
            Assert.Equal("text_pause", runtime.Story.Kind);
            await runtime.TickAsync(.001);
        }
        Assert.Equal("after", runtime.Story.Text);
        Assert.False(runtime.Story.NeedsTick);
    }

    [Fact]
    public async Task ClearText_InstantlyClearsRememberedText_AndAnimatedEraseHasLogicalProgressWithoutLosingUnicode()
    {
        using var fixture = Scene("""
            d("A", "é 👩‍🚀")
            clear_text(true, 2)
            text_pause(1, false)
            d("B", "second")
            clear_text(false)
            text_pause(1, true)
            d(nil, "after")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.AdvanceAsync();
        Assert.Equal("clear_text", runtime.Story.Kind);
        Assert.Equal("é 👩‍🚀", runtime.Story.Text);
        Assert.Equal(0, runtime.Story.EraseProgress);
        Assert.False(runtime.Story.CanAdvance);
        await runtime.TickAsync(.5);
        Assert.Equal(.25, runtime.Story.EraseProgress, 8);
        Assert.Equal("é 👩‍🚀", runtime.Story.Text);
        await runtime.TickAsync(1.5);
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.Equal("", runtime.Story.Text);
        Assert.Equal("", runtime.Story.Speaker);
        await runtime.TickAsync(1);
        Assert.Equal("second", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.Equal("", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("after", runtime.Story.Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutoDialogue_UsesLogicalClockAndOptionalClick_AndCheckpointRestoresRemainingTime(bool skippable)
    {
        using var fixture = Scene($"kne_say(nil,'auto',nil,nil,2,{(skippable ? "true" : "false")}); d(nil,'next')");
        var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project);
        await runtime.StartAsync();
        Assert.Equal("dialogue", runtime.Story.Kind);
        Assert.Equal(skippable, runtime.Story.CanAdvance);
        await runtime.TickAsync(.75);
        Assert.Equal(1.25, runtime.Story.RemainingSeconds);
        var checkpoint = await runtime.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(runtime.Story.Text, restored.Story.Text);
        Assert.Equal(runtime.Story.RemainingSeconds, restored.Story.RemainingSeconds);
        Assert.Equal(runtime.Story.IsSkippable, restored.Story.IsSkippable);
        await restored.TickAsync(1.249);
        Assert.Equal("auto", restored.Story.Text);
        if (skippable) await restored.AdvanceAsync();
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => restored.AdvanceAsync());
            await restored.TickAsync(.001);
        }
        Assert.Equal("next", restored.Story.Text);
        Assert.Null(restored.Story.RemainingSeconds);
    }

    public static IEnumerable<object[]> EffectKinds => TextEffectSettings.Kinds.Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(EffectKinds))]
    public async Task EffectDialogue_PreservesKindSpeedLoopColors_AndWorkerAndCheckpointTransport(string kind)
    {
        var effect = new TextEffectSettings { Kind = kind, Speed = 2.5, Loop = false, Color1 = "#FF112233", Color2 = "#FF778899" };
        string json = JsonSerializer.Serialize(effect, EngineSettings.JsonOptions);
        using var fixture = Scene($"kne_say('A','effect',nil,[=[{json}]=]); d(nil,'plain')");
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal(effect, worker.Story.TextEffect);
        Assert.Equal("effect", worker.Story.Text);
        var checkpoint = await worker.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(effect, restored.Story.TextEffect);
        Assert.Equal(worker.Story.Identity, restored.Story.Identity);
        await worker.AdvanceAsync();
        Assert.Equal("plain", worker.Story.Text);
        Assert.Null(worker.Story.TextEffect);
    }

    [Fact]
    public async Task ConsoleWrite_UsesPrintOutputWithoutStopping_AndRandomBranchesReplayWeightedSelection()
    {
        using var fixture = Scene("""
            console_write("console value")
            state.zero = kne_random_branch({0, 100})
            state.roll = kne_random_branch({60, 40})
            d(nil, "branch:" .. state.roll)
            state.next_roll = kne_random_branch({60, 40})
            d(nil, "next:" .. state.next_roll)
            """);
        var output = new ConcurrentQueue<string>();
        var project = await fixture.LoadAsync();
        await using var runtime = new LuaRuntime(project, output.Enqueue, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(new[] { "console value", runtime.Story.Text }, output.Where(item => item == "console value" || item.StartsWith("branch:")).ToArray());
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal(2, checkpoint.State.GetProperty("zero").GetInt64());
        Assert.InRange(checkpoint.State.GetProperty("roll").GetInt64(), 1, 2);
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        await runtime.AdvanceAsync();
        await restored.AdvanceAsync();
        Assert.Equal(runtime.Story.Text, restored.Story.Text);
        Assert.Equal((await runtime.GetCheckpointAsync()).State.GetProperty("next_roll").GetInt64(), (await restored.GetCheckpointAsync()).State.GetProperty("next_roll").GetInt64());
    }

    [Fact]
    public async Task RandomBranch_UsesRelativeWeightsAndExactCumulativeBoundary_WithoutSelectingZeroWeightBranches()
    {
        using var fixture = Scene("""
            local original = math.random
            math.random = function() return 0 end
            state.first = kne_random_branch({0,60,40,0})
            math.random = function() return 0.599999 end
            state.before = kne_random_branch({60,40})
            math.random = function() return 0.6 end
            state.at = kne_random_branch({60,40})
            math.random = function() return 0.999999 end
            state.last = kne_random_branch({0,6,4,0})
            math.random = original
            d(nil,"weighted")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(2, state.GetProperty("first").GetInt64());
        Assert.Equal(1, state.GetProperty("before").GetInt64());
        Assert.Equal(2, state.GetProperty("at").GetInt64());
        Assert.Equal(3, state.GetProperty("last").GetInt64());
    }

    [Fact]
    public async Task Worker_TransportsProfilePauseAndEraseProgress_AndRestoreRejectsTamperedNarrativeMetadata()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса","text_alignment":"center"}')
            kne_say(nil,"Unicode 👩‍🚀","alice",'{"kind":"wave","speed":3,"loop":false}')
            clear_text(true,2)
            text_pause(1,false)
            d(nil,"after")
            """);
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal("alice", worker.Story.Character!.Id);
        Assert.Equal("center", worker.Story.Character.TextAlignment);
        await worker.AdvanceAsync();
        await worker.TickAsync(.5);
        Assert.Equal(.25, worker.Story.EraseProgress);
        Assert.Equal(1.5, worker.Story.RemainingSeconds);
        Assert.False(worker.Story.CanAdvance);
        var checkpoint = await worker.GetCheckpointAsync();
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(checkpoint.Frame.Character, restored.Story.Character);
        Assert.Equal(checkpoint.Frame.TextEffect, restored.Story.TextEffect);
        Assert.Equal(.25, restored.Story.EraseProgress);
        await restored.TickAsync(1.5);
        Assert.Equal("text_pause", restored.Story.Kind);
        Assert.Equal("", restored.Story.Text);
        await using var tampered = Runtime(project);
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => tampered.RestoreAsync(checkpoint with { Frame = checkpoint.Frame with { EraseProgress = .9 } }));
    }

    [Theory]
    [InlineData("text_pause(-1,true)")]
    [InlineData("text_pause(3601,true)")]
    [InlineData("text_pause(1,'yes')")]
    [InlineData("clear_text('yes',1)")]
    [InlineData("clear_text(true,-1)")]
    [InlineData("kne_say(nil,'x',nil,nil,1,'yes')")]
    [InlineData("kne_say(nil,'x',nil,nil,nil,false)")]
    [InlineData("kne_say(nil,'x',nil,'{\"kind\":\"unknown\"}')")]
    [InlineData("kne_say(nil,'x',nil,'{\"speed\":0}')")]
    [InlineData("kne_random_branch({0,0})")]
    [InlineData("kne_random_branch({-1,1})")]
    [InlineData("kne_random_branch({1})")]
    [InlineData("kne_random_branch({0/0,1})")]
    public async Task NarrativeCommands_RejectInvalidPartitionsBeforeChangingScene(string command)
    {
        using var fixture = Scene(command);
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Equal(2, GameErrorReport.TryGet(error)!.Line);
        Assert.Equal("idle", runtime.Story.Kind);
    }

    [Fact]
    public async Task ZeroPauseAndZeroDurationAuto_ContinueWithoutInput_AndLegacyFrameSignatureRemainsUnchanged()
    {
        using var fixture = Scene("text_pause(0,false); kne_say(nil,'zero',nil,nil,0,false); d(nil,'legacy'); d(nil,'after')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("legacy", runtime.Story.Text);
        await runtime.AdvanceAsync();
        string signature = Assert.Single((await runtime.GetCheckpointAsync()).History).ExpectedFrame;
        Assert.Equal("{\"Kind\":\"dialogue\",\"Speaker\":\"\",\"Text\":\"legacy\",\"Options\":[],\"Completed\":false}", signature);
    }

    [Fact]
    public async Task DetachedTextPause_WaitsOnLogicalClockWithoutReplacingForegroundOrLoggingDialogueTwice()
    {
        using var fixture = Scene("""
            kne_async({function() console_write("before"); text_pause(.5,false); console_write("after") end})
            d(nil,"foreground")
            """);
        var log = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), log.Enqueue, interactive: true);
        await runtime.StartAsync();
        Assert.Contains("before", log);
        Assert.DoesNotContain("after", log);
        await runtime.TickAsync(.499);
        Assert.DoesNotContain("after", log);
        Assert.Equal("foreground", runtime.Story.Text);
        await runtime.TickAsync(.001);
        Assert.Contains("after", log);
        Assert.Equal("foreground", runtime.Story.Text);
        Assert.Single(log, item => item == "foreground");
        Assert.False(runtime.Story.NeedsTick);
    }

    [Fact]
    public void NarrativeModels_ValidateFontAndSpeedExactBounds_AndRejectNonFiniteValues()
    {
        new CharacterProfile { FontSize = 1 }.Validate();
        new CharacterProfile { FontSize = 512 }.Validate();
        Assert.Throws<ArgumentException>(() => new CharacterProfile { FontSize = .999 }.Validate());
        Assert.Throws<ArgumentException>(() => new CharacterProfile { FontSize = 512.001 }.Validate());
        Assert.Throws<ArgumentException>(() => new CharacterProfile { FontSize = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new CharacterProfile { Name = "" }.Validate());
        Assert.Throws<ArgumentException>(() => new CharacterProfile { FontFamily = "" }.Validate());
        new TextEffectSettings { Speed = .001 }.Validate();
        new TextEffectSettings { Speed = 100 }.Validate();
        Assert.Throws<ArgumentException>(() => new TextEffectSettings { Speed = 100.001 }.Validate());
        Assert.Throws<ArgumentException>(() => new TextEffectSettings { Speed = double.PositiveInfinity }.Validate());
        Assert.Throws<ArgumentException>(() => new TextEffectSettings { Color1 = "bad" }.Validate());
        Assert.Throws<ArgumentException>(() => new TextEffectSettings { Color2 = "bad" }.Validate());
    }

    [Fact]
    public async Task ParallelAndBackgroundDialogue_DoNotReplaceTheTextRetainedByForegroundPause()
    {
        using var fixture = Scene("""
            kne_async({function() kne_pause(.2); d("Background","background log") end})
            kne_parallel({function()
                d("A","foreground A")
                text_pause(1,true)
                d("A","after A")
            end, function() d("B","queued B") end})
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("foreground A", runtime.Story.Text);
        await runtime.TickAsync(.2);
        Assert.Equal("foreground A", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.Equal("foreground A", runtime.Story.Text);
        Assert.Equal("A", runtime.Story.Speaker);
        await runtime.AdvanceAsync();
        Assert.Equal("after A", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("queued B", runtime.Story.Text);
    }

    [Fact]
    public async Task ParallelAutoDialogue_StartsItsTimerWhenPresented_AndCannotExpireWhileQueuedBehindAnotherFrame()
    {
        using var fixture = Scene("""
            kne_async({function() kne_pause(30); state.background_done = true end})
            kne_parallel({function() d(nil,"first") end, function()
                kne_say(nil,"queued auto",nil,nil,1,false)
                d(nil,"after auto")
            end})
            d(nil,"done")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("first", runtime.Story.Text);
        // A separate ticking task must not make an invisible foreground timer run.
        await runtime.TickAsync(20);
        await runtime.AdvanceAsync();
        Assert.Equal("queued auto", runtime.Story.Text);
        Assert.Equal(1, runtime.Story.RemainingSeconds);
        await runtime.TickAsync(.999);
        Assert.Equal("queued auto", runtime.Story.Text);
        await runtime.TickAsync(.001);
        Assert.Equal("after auto", runtime.Story.Text);
    }

    [Fact]
    public async Task ClearAndPauseAfterChoice_RetainTheActualChoicePrompt_WithoutStaleSpeakerProfileOrEffect()
    {
        using var fixture = Scene("""
            register_character('{"id":"alice","name":"Алиса"}')
            kne_say(nil,"old dialogue","alice",'{"kind":"wave"}')
            choice("First prompt", {"yes"})
            clear_text(true,1)
            choice("Second prompt", {"yes"})
            text_pause(1,true)
            d(nil,"after")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.AdvanceAsync();
        Assert.Equal("First prompt", runtime.Story.Text);
        await runtime.ChooseAsync(1);
        Assert.Equal("clear_text", runtime.Story.Kind);
        Assert.Equal("First prompt", runtime.Story.Text);
        Assert.Equal("", runtime.Story.Speaker);
        Assert.Null(runtime.Story.Character);
        Assert.Null(runtime.Story.TextEffect);
        await runtime.TickAsync(1);
        Assert.Equal("Second prompt", runtime.Story.Text);
        await runtime.ChooseAsync(1);
        Assert.Equal("text_pause", runtime.Story.Kind);
        Assert.Equal("Second prompt", runtime.Story.Text);
        Assert.Equal("", runtime.Story.Speaker);
        Assert.Null(runtime.Story.Character);
        Assert.Null(runtime.Story.TextEffect);
        await runtime.AdvanceAsync();
        Assert.Equal("after", runtime.Story.Text);
    }

    [Fact]
    public async Task RepeatedSameBlockDialogue_HasNewPresentationEachTime_ButTicksPauseEraseAndCheckpointKeepItsOccurrence()
    {
        using var fixture = Scene("""
            kne_async({function() kne_pause(30) end})
            for i = 1, 3 do
                kne_say(nil,"same text",nil,'{"kind":"wave","loop":false}')
                text_pause(1,true)
                clear_text(true,1)
            end
            d(nil,"after")
            """);
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync();
        var identity = runtime.Story.Identity;
        Assert.NotNull(identity);
        for (long occurrence = 1; occurrence <= 3; occurrence++)
        {
            Assert.Equal("same text", runtime.Story.Text);
            Assert.Equal(identity, runtime.Story.Identity);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            await runtime.TickAsync(.1);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            var checkpoint = await runtime.GetCheckpointAsync();
            await using var restored = Runtime(project);
            await restored.RestoreAsync(checkpoint);
            Assert.Equal(occurrence, restored.Story.PresentationId);
            Assert.Equal(identity, restored.Story.Identity);
            await runtime.AdvanceAsync();
            Assert.Equal("text_pause", runtime.Story.Kind);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            await runtime.TickAsync(.25);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            await runtime.AdvanceAsync();
            Assert.Equal("clear_text", runtime.Story.Kind);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            await runtime.TickAsync(.25);
            Assert.Equal(.25, runtime.Story.EraseProgress, 8);
            Assert.Equal(occurrence, runtime.Story.PresentationId);
            await runtime.TickAsync(.75);
        }
        Assert.Equal("after", runtime.Story.Text);
        Assert.Equal(4, runtime.Story.PresentationId);
    }

    private static TempProject Scene(string body)
    {
        var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n" + body + "\nend");
        return fixture;
    }

    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true);
}
