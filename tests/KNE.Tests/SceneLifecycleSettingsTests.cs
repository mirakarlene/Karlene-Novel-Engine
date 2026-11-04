using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class SceneLifecycleSettingsTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SameFileScenesMergeInitialRootStateOnEachCallAndGotoButRestoreCallerPolicyWithoutReinitializingOnReturn(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("engine/state.json", """{"keep":9,"replace":{"old":true}}""");
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, 'main:' .. state.score)
                state.score = 41
                call_scene(child)
                d(nil, 'back:' .. state.score .. ':' .. state.keep)
                call_scene('child')
                d(nil, 'again:' .. state.score)
                goto_scene(finale)
                error('goto must discard the caller')
            end
            function child()
                d(nil, 'child:' .. state.score)
                state.score = state.score + 1
            end
            function finale() d(nil, 'finale:' .. state.score .. ':' .. state.keep) end
            """);
        await Save(fixture, new()
        {
            ["main"] = new() { Name = "Главная", WindowWidth = 1600, WindowHeight = 900, InitialState = Json("""{"score":1}""") },
            ["child"] = new() { Name = "Дочерняя", WindowWidth = 800, WindowHeight = 600, InitialState = Json("""{"score":7,"replace":{"new":true},"items":[1,"два"]}"""), EffectsQuality = "low", PostProcessing = false },
            ["finale"] = new() { InitialState = Json("""{"score":20}""") }
        });
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync();
        Assert.Equal("main:1", runtime.Story.Text); var mainVisit = AssertScene(runtime, "main", 1600, 900).Visit;
        await runtime.AdvanceAsync(); Assert.Equal("child:7", runtime.Story.Text); var child = AssertScene(runtime, "child", 800, 600);
        Assert.Equal("low", child.Settings.EffectsQuality); Assert.False(child.Settings.PostProcessing); Assert.NotEqual(mainVisit, child.Visit);
        var checkpoint = await runtime.GetCheckpointAsync(); Assert.Equal(9, checkpoint.State.GetProperty("keep").GetInt32());
        Assert.False(checkpoint.State.GetProperty("replace").TryGetProperty("old", out _)); Assert.True(checkpoint.State.GetProperty("replace").GetProperty("new").GetBoolean());
        Assert.Equal(1, checkpoint.State.GetProperty("items")[0].GetInt32()); Assert.Equal("два", checkpoint.State.GetProperty("items")[1].GetString());
        await using var replay = Runtime(project, worker); await replay.RestoreAsync(JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(checkpoint))!);
        Assert.Equal("child:7", replay.Story.Text); Assert.Equal(child.Visit, AssertScene(replay, "child", 800, 600).Visit);
        await replay.AdvanceAsync(); Assert.Equal("back:8:9", replay.Story.Text); Assert.Equal(mainVisit, AssertScene(replay, "main", 1600, 900).Visit);
        Assert.Equal("high", replay.Visuals.Scene!.Settings.EffectsQuality); Assert.True(replay.Visuals.Scene.Settings.PostProcessing);
        await replay.AdvanceAsync(); Assert.Equal("child:7", replay.Story.Text); Assert.NotEqual(child.Visit, AssertScene(replay, "child", 800, 600).Visit);
        await replay.AdvanceAsync(); Assert.Equal("again:8", replay.Story.Text); Assert.Equal(mainVisit, AssertScene(replay, "main", 1600, 900).Visit);
        await replay.AdvanceAsync(); Assert.Equal("finale:20:9", replay.Story.Text); AssertScene(replay, "finale", 1280, 720);
        await replay.AdvanceAsync(); Assert.True(replay.Story.Completed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NoSaveScenesRejectUserSavesButAllowInternalRecoveryCaptureLoadingAndReturnToASavableCaller(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'before'); call_scene(locked); d(nil,'after') end\nfunction locked() d(nil,'locked'); state.finished=true end");
        await Save(fixture, new() { ["locked"] = new() { AllowSaves = false, AllowAutosave = false, CheckpointOnEntry = true } });
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync();
        var before = await runtime.GetCheckpointAsync(); await runtime.AdvanceAsync(); Assert.Equal("locked", runtime.Story.Text);
        Assert.False(runtime.Visuals.Scene!.Settings.AllowSaves); Assert.False(runtime.Visuals.Scene.Settings.AllowAutosave);
        var denied = await Assert.ThrowsAnyAsync<Exception>(() => runtime.GetCheckpointAsync()); Assert.Contains("Сохранения в этой сцене запрещены", denied.Message);
        var recovery = await runtime.CaptureCheckpointAsync(); Assert.Equal("locked", recovery.Frame.Text); Assert.Single(recovery.History);
        await using var replay = Runtime(project, worker); await replay.RestoreAsync(recovery); Assert.Equal("locked", replay.Story.Text); Assert.False(replay.Visuals.Scene!.Settings.AllowSaves);
        await replay.AdvanceAsync(); Assert.Equal("after", replay.Story.Text); Assert.True(replay.Visuals.Scene!.Settings.AllowSaves);
        Assert.True((await replay.GetCheckpointAsync()).State.GetProperty("finished").GetBoolean());
        await replay.RestoreAsync(before); Assert.Equal("before", replay.Story.Text); Assert.True(replay.Visuals.Scene!.Settings.AllowSaves);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.ProjectPath, "saves"), "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task CallBudgetsAreRelativeToTheConfiguredSceneAndEnforcedForEveryEnclosingCaller(bool worker, bool exceeds)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene(outer) end\nfunction outer() call_scene(inner) end\nfunction inner() d(nil,'allowed'); " + (exceeds ? "call_scene(leaf)" : "d(nil,'returned')") + " end\nfunction leaf() d(nil,'unreachable') end");
        await Save(fixture, new() { ["outer"] = new() { Name = "Ограниченная", MaxCallDepth = 1 }, ["inner"] = new() { MaxCallDepth = 3 } });
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); Assert.Equal("allowed", runtime.Story.Text);
        Assert.Equal("inner", runtime.Visuals.Scene!.Function);
        if (exceeds)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
            Assert.Contains("Ограниченная", error.Message); Assert.Contains("глубина call", error.Message); Assert.DoesNotContain("unreachable", runtime.Story.Text);
        }
        else { await runtime.AdvanceAsync(); Assert.Equal("returned", runtime.Story.Text); await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DisabledSceneTriggersSuppressKeysButtonsAndMouseButKeepNarrativeTextClosedAndRestoreTheCallerPolicy(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                kne_on_event('{"kind":"key","keys":"E"}', function() state.blocked=true end)
                kne_on_event('{"kind":"button","button":"next"}', function() state.blocked=true end)
                kne_on_event('{"kind":"hotspot","x":0,"y":0,"width":100,"height":100}', function() state.blocked=true end)
                kne_on_event('{"kind":"text_closed","once":true}', function() state.closed=state.closed+1 end)
                clear_text(false,0)
                d(nil,'main:'..state.closed)
                call_scene(child)
                d(nil,'back')
            end
            function child()
                kne_on_event('{"kind":"key","keys":"E"}', function() state.enabled=true end)
                d(nil,'child')
            end
            """);
        await Save(fixture, new() { ["main"] = new() { AllowTriggers = false, InitialState = Json("""{"closed":0}""") } });
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); Assert.Equal("main:1", runtime.Story.Text); Assert.Empty(runtime.Story.Triggers);
        await runtime.TriggerAsync(new("key", "E")); await runtime.TriggerAsync(new("button", Button: "next")); await runtime.TriggerAsync(new("hotspot", X: 25, Y: 25));
        var untouched = await runtime.CaptureCheckpointAsync(); Assert.Empty(untouched.History); Assert.False(untouched.State.TryGetProperty("blocked", out _)); Assert.Equal(1, untouched.State.GetProperty("closed").GetInt32());
        await runtime.AdvanceAsync(); Assert.Equal("child", runtime.Story.Text); Assert.True(runtime.Visuals.Scene!.Settings.AllowTriggers); Assert.Equal("E", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.TriggerAsync(new("key", "E")); Assert.True((await runtime.CaptureCheckpointAsync()).State.GetProperty("enabled").GetBoolean());
        await runtime.AdvanceAsync(); Assert.Equal("back", runtime.Story.Text); Assert.False(runtime.Visuals.Scene!.Settings.AllowTriggers); Assert.Empty(runtime.Story.Triggers);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GotoReplacesOnlyTheCurrentSceneWhileRetainingTheEnclosingCallBudget(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene(outer) end\nfunction outer() call_scene(child) end\nfunction child() goto_scene(replacement) end\nfunction replacement() d(nil,'replacement'); call_scene(leaf) end\nfunction leaf() d(nil,'unreachable') end");
        await Save(fixture, new() { ["outer"] = new() { Name = "Ancestor", MaxCallDepth = 1 }, ["replacement"] = new() { WindowWidth = 800, WindowHeight = 600 } });
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); Assert.Equal("replacement", runtime.Story.Text); AssertScene(runtime, "replacement", 800, 600);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync()); Assert.Contains("Ancestor", error.Message); Assert.Contains("глубина call", error.Message); Assert.DoesNotContain("unreachable", runtime.Story.Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SceneMusicStartsWithItsLoopPreferenceAndCallerMusicRestoresWhileAnEmptySceneCannotStopInheritedMusic(bool worker)
    {
        using var fixture = new TempProject(); fixture.Write("music/main.wav", "resource fixture"); fixture.Write("music/child.wav", "resource fixture");
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'main'); call_scene(child); d(nil,'back'); goto_scene(empty) end\nfunction child() d(nil,'child') end\nfunction empty() d(nil,'empty'); goto_scene(finale) end\nfunction finale() d(nil,'finale') end");
        await Save(fixture, new()
        {
            ["main"] = new() { Music = "music/main.wav", LoopMusic = true, MusicVolume = 70 },
            ["child"] = new() { Music = "music/child.wav", LoopMusic = false, MusicVolume = 0, StopMusicOnExit = true },
            ["empty"] = new() { StopMusicOnExit = true }
        });
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync();
        string main = Path.GetFullPath(Path.Combine(fixture.ProjectPath, "music/main.wav")), child = Path.GetFullPath(Path.Combine(fixture.ProjectPath, "music/child.wav"));
        Assert.Equal(main, runtime.Visuals.Music); Assert.True(runtime.Visuals.LoopMusic); Assert.Equal(70, runtime.Visuals.Scene!.Settings.MusicVolume);
        await runtime.AdvanceAsync(); Assert.Equal("child", runtime.Story.Text); Assert.Equal(child, runtime.Visuals.Music); Assert.False(runtime.Visuals.LoopMusic); Assert.Equal(0, runtime.Visuals.Scene!.Settings.MusicVolume);
        await using var replay = Runtime(project, worker); await replay.RestoreAsync(await runtime.GetCheckpointAsync());
        Assert.Equal(child, replay.Visuals.Music); Assert.False(replay.Visuals.LoopMusic);
        await replay.AdvanceAsync(); Assert.Equal("back", replay.Story.Text); Assert.Equal(main, replay.Visuals.Music); Assert.True(replay.Visuals.LoopMusic); Assert.Equal(70, replay.Visuals.Scene!.Settings.MusicVolume);
        await replay.AdvanceAsync(); Assert.Equal("empty", replay.Story.Text); Assert.Equal(main, replay.Visuals.Music); Assert.Null(replay.Visuals.Scene!.Settings.Music);
        await replay.AdvanceAsync(); Assert.Equal("finale", replay.Story.Text); Assert.Equal(main, replay.Visuals.Music); Assert.True(replay.Visuals.LoopMusic);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AnonymousParallelFlowsInheritTheirScenePolicyWithoutCreatingANewVisitOrReapplyingInitialState(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.score=state.score+1
                kne_parallel({function()
                    kne_on_event('{"kind":"key","keys":"E"}',function() state.blocked=true end)
                    d(nil,'branch:'..state.score)
                    state.score=state.score+1
                end})
                d(nil,'main:'..state.score)
            end
            """);
        await Save(fixture, new() { ["main"] = new() { WindowWidth = 800, WindowHeight = 600, AllowSaves = false, AllowTriggers = false, InitialState = Json("""{"score":7}""") } });
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync(); Assert.Equal("branch:8", runtime.Story.Text); var visit = AssertScene(runtime, "main", 800, 600).Visit;
        Assert.False(runtime.Visuals.Scene!.Settings.AllowSaves); Assert.Empty(runtime.Story.Triggers); var recovery = await runtime.CaptureCheckpointAsync(); Assert.Equal(8, recovery.State.GetProperty("score").GetInt32());
        await using var replay = Runtime(project, worker); await replay.RestoreAsync(recovery); await replay.AdvanceAsync(); Assert.Equal("main:9", replay.Story.Text); Assert.Equal(visit, AssertScene(replay, "main", 800, 600).Visit); Assert.False(replay.Visuals.Scene!.Settings.AllowTriggers);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NullInitialRootKeysClearExistingVariablesOnEachFreshCallAndGotoWithoutRestoringThemOnReturn(bool worker)
    {
        using var fixture = new TempProject(); fixture.Write("engine/state.json", """{"clear":"before","keep":9}""");
        fixture.Write("game/scenes/main.krl", "function main() d(nil,state.clear); call_scene(child); d(nil,'back:'..tostring(state.clear)); state.clear='again'; goto_scene(child) end\nfunction child() d(nil,'child:'..tostring(state.clear)..':'..state.keep) end");
        await Save(fixture, new() { ["child"] = new() { InitialState = Json("""{"clear":null}""") } });
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("child:nil:9", runtime.Story.Text); Assert.False((await runtime.CaptureCheckpointAsync()).State.TryGetProperty("clear", out _));
        await runtime.AdvanceAsync(); Assert.Equal("back:nil", runtime.Story.Text); await runtime.AdvanceAsync(); Assert.Equal("child:nil:9", runtime.Story.Text); Assert.False((await runtime.CaptureCheckpointAsync()).State.TryGetProperty("clear", out _));
    }

    [Theory]
    [InlineData(false, "initial")] [InlineData(true, "initial")] [InlineData(false, "depth")] [InlineData(true, "depth")] [InlineData(false, "triggers")] [InlineData(true, "triggers")]
    public async Task ChangedSceneExecutionPolicyInvalidatesExistingCheckpoints(bool worker, string field)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main() d(nil,'stable text') end");
        var project = await fixture.LoadAsync(); await using var original = Runtime(project, worker); await original.StartAsync(); var checkpoint = await original.GetCheckpointAsync();
        var settings = new ScenePerformanceSettings(); if (field == "initial") settings.InitialState = Json("""{"score":7}"""); else if (field == "depth") settings.MaxCallDepth = 2; else settings.AllowTriggers = false;
        await Save(fixture, new() { ["main"] = settings }); await using var changed = Runtime(await fixture.LoadAsync(), worker);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => changed.RestoreAsync(checkpoint)); Assert.Contains("сценарии изменились", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PresentationMusicAndSavePermissionChangesKeepExistingCheckpointsLoadableAndApplyTheNewSettings(bool worker)
    {
        using var fixture = new TempProject(); fixture.Write("theme.wav", "resource fixture"); fixture.Write("effect.kneshader", ShaderGraphCompiler.Create().Serialize()); fixture.Write("game/scenes/main.krl", "function main() d(nil,'stable text') end");
        var project = await fixture.LoadAsync(); await using var original = Runtime(project, worker); await original.StartAsync(); var checkpoint = await original.GetCheckpointAsync();
        await Save(fixture, new() { ["main"] = new() { Name = "Changed presentation", WindowWidth = 800, WindowHeight = 600, Music = "theme.wav", LoopMusic = false, MusicVolume = 17, StopMusicOnExit = true, EntryTransition = new() { Kind = "custom", Shader = "effect.kneshader", Duration = .5 }, ExitTransition = new() { Kind = "fade", Duration = 2 }, EffectsQuality = "low", PostProcessing = false, HideShaders = true, AllowSaves = false, AllowAutosave = false, CheckpointOnEntry = true } });
        await using var changed = Runtime(await fixture.LoadAsync(), worker); await changed.RestoreAsync(checkpoint); Assert.Equal("stable text", changed.Story.Text); var scene = AssertScene(changed, "main", 800, 600);
        Assert.Equal("Changed presentation", scene.Settings.Name); Assert.Equal(17, scene.Settings.MusicVolume); Assert.False(scene.Settings.AllowSaves); Assert.False(scene.Settings.AllowAutosave); Assert.True(scene.Settings.CheckpointOnEntry);
        Assert.Equal("custom", scene.Settings.EntryTransition.Kind); Assert.Equal("fade", scene.Settings.ExitTransition.Kind); Assert.Equal("low", scene.Settings.EffectsQuality); Assert.False(scene.Settings.PostProcessing); Assert.True(scene.Settings.HideShaders);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "theme.wav"), changed.Visuals.Music); Assert.False(changed.Visuals.LoopMusic); Assert.Equal(checkpoint.State.GetRawText(), (await changed.CaptureCheckpointAsync()).State.GetRawText());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ParallelAnonymousFlowsRetainTheirEnclosingSceneDepthWhenCheckingFurtherCalls(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene(outer) end\nfunction outer() kne_parallel({function() call_scene(middle) end}) end\nfunction middle() d(nil,'middle'); call_scene(leaf) end\nfunction leaf() d(nil,'unreachable') end");
        await Save(fixture, new() { ["outer"] = new() { Name = "Outer budget", MaxCallDepth = 1 } });
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); Assert.Equal("middle", runtime.Story.Text); Assert.Equal("middle", runtime.Visuals.Scene!.Function);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync()); Assert.Contains("Outer budget", error.Message); Assert.Contains("глубина call", error.Message); Assert.DoesNotContain("unreachable", runtime.Story.Text);
    }

    [Fact]
    public async Task DetachedFlowsKeepTheirReturnedScenePolicyAndAllowedBackgroundHandlersCannotBypassABlockedForegroundScene()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                call_scene(blocked_outer)
                call_scene(allowed_outer)
                d(nil,'blocked foreground')
                call_scene(unblocked)
                d(nil,'back')
            end
            function blocked_outer()
                kne_async({function()
                    kne_pause(.1)
                    kne_on_event('{"kind":"key","keys":"E"}',function() state.blocked=true end)
                    kne_pause(10)
                end})
            end
            function allowed_outer()
                kne_async({function()
                    kne_pause(.1)
                    kne_on_event('{"kind":"key","keys":"C"}',function() state.allowed=true end)
                    kne_pause(10)
                end})
            end
            function unblocked() d(nil,'allowed foreground') end
            """);
        await Save(fixture, new() { ["main"] = new() { AllowTriggers = false }, ["blocked_outer"] = new() { AllowTriggers = false } });
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }); await runtime.StartAsync(); Assert.Equal("blocked foreground", runtime.Story.Text); await runtime.TickAsync(.2);
        Assert.Empty(runtime.Story.Triggers); var checkpoint = await runtime.CaptureCheckpointAsync();
        await runtime.TriggerAsync(new("key", "E")); await runtime.TriggerAsync(new("key", "C")); var untouched = await runtime.CaptureCheckpointAsync(); Assert.Equal(checkpoint.History.Count, untouched.History.Count); Assert.False(untouched.State.TryGetProperty("blocked", out _)); Assert.False(untouched.State.TryGetProperty("allowed", out _));
        await runtime.AdvanceAsync(); Assert.Equal("allowed foreground", runtime.Story.Text); Assert.Equal("C", Assert.Single(runtime.Story.Triggers).Keys);
        await runtime.TriggerAsync(new("key", "C")); var state = (await runtime.CaptureCheckpointAsync()).State; Assert.True(state.GetProperty("allowed").GetBoolean()); Assert.False(state.TryGetProperty("blocked", out _));
    }

    [Fact]
    public async Task DetachedGotoReplacementsRetainTheReturnedAncestorsCallBudgetThroughTwoOwnedSceneReplacements()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene(outer); d(nil,'foreground') end\nfunction outer() kne_async({function() kne_pause(.1); goto_scene(first) end}) end\nfunction first() goto_scene(second) end\nfunction second() call_scene(middle) end\nfunction middle() state.reached=true; kne_pause(1); call_scene(leaf) end\nfunction leaf() state.leaked=true end");
        await Save(fixture, new() { ["outer"] = new() { Name = "Retained outer", MaxCallDepth = 1 } });
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }); await runtime.StartAsync(); Assert.Equal("foreground", runtime.Story.Text); await runtime.TickAsync(.2);
        var checkpoint = await runtime.CaptureCheckpointAsync(); Assert.True(checkpoint.State.GetProperty("reached").GetBoolean()); Assert.False(checkpoint.State.TryGetProperty("leaked", out _));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.TickAsync(1)); Assert.Contains("Retained outer", error.Message); Assert.Contains("глубина call", error.Message); Assert.Equal("foreground", runtime.Story.Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TerminalScenesWithoutAnInteractiveYieldStillPublishTheirOwnCompletedPolicy(bool worker)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main() d(nil,'before'); goto_scene(terminal) end\nfunction terminal() end");
        await Save(fixture, new() { ["terminal"] = new() { Name = "Terminal policy", AllowSaves = false, MusicVolume = 0 } });
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text); await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed); var scene = Assert.IsType<SceneRuntimeState>(runtime.Visuals.Scene); Assert.Equal("terminal", scene.Function); Assert.Equal("game/scenes/main.krl", scene.File); Assert.Equal("Terminal policy", scene.Settings.Name);
        Assert.False(scene.Settings.AllowSaves); Assert.Equal(0, scene.Settings.MusicVolume); Assert.True(scene.Exited); Assert.Equal(2, scene.Visit); Assert.Equal(2, scene.LastVisit);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.GetCheckpointAsync()); Assert.Contains("Сохранения в этой сцене запрещены", error.Message);
    }

    static SceneRuntimeState AssertScene(IGameSession runtime, string function, int width, int height)
    {
        var scene = Assert.IsType<SceneRuntimeState>(runtime.Visuals.Scene); Assert.Equal("game/scenes/main.krl", scene.File); Assert.Equal(function, scene.Function);
        Assert.Equal(width, runtime.WindowSettings.Width); Assert.Equal(height, runtime.WindowSettings.Height); Assert.True(scene.Visit > 0); return scene;
    }
    static IGameSession Runtime(ProjectInfo project, bool worker) => worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static Task Save(TempProject fixture, Dictionary<string, ScenePerformanceSettings> functions)
        => PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = functions } });
}
