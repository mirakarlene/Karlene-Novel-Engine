using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ExtendedFlowTests
{
    [Fact]
    public async Task Parallel_PresentsEachBranchChoiceAndDialogue_AndWaitsForAllBranches()
    {
        using var fixture = Scene("""
            kne_parallel({function()
                state.answer = choice("Branch A", {"one", "two"})
                d(nil, "A:" .. state.answer)
            end, function() d(nil, "B") end})
            d(nil, "after join")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("Branch A", runtime.Story.Text);
        await runtime.ChooseAsync(2);
        Assert.Equal("A:2", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("B", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("after join", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task Async_TicksWhileForegroundIsVisible_AndJoinWaitsWithoutReplacingThatFrame()
    {
        using var fixture = Scene("""
            state.task = kne_async({function() kne_pause(1); state.finished = true; d(nil, "background log") end})
            d(nil, "foreground")
            kne_join(state.task)
            d(nil, "joined")
            """);
        var log = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), log.Enqueue, interactive: true);
        await runtime.StartAsync();
        var foreground = runtime.Story;
        Assert.True(foreground.HasBackgroundTasks);
        await runtime.TickAsync(.5);
        Assert.Equal("foreground", runtime.Story.Text);
        Assert.False((await runtime.GetCheckpointAsync()).State.TryGetProperty("finished", out _));
        await runtime.TickAsync(.5);
        Assert.Equal("foreground", runtime.Story.Text);
        Assert.False(runtime.Story.HasBackgroundTasks);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("finished").GetBoolean());
        Assert.Contains("background log", log);
        Assert.Equal(1, log.Count(item => item == "foreground"));
        await runtime.AdvanceAsync();
        Assert.Equal("joined", runtime.Story.Text);
    }

    [Fact]
    public async Task PauseAndTimer_AreLogicalTimeDriven_WithExactBoundary_AndDoNotBusyWait()
    {
        using var fixture = Scene("kne_pause(1); d(nil, 'after pause'); kne_timer(.5); d(nil, 'after timer')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.TickAsync(.999);
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.TickAsync(.001);
        Assert.Equal("after pause", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.TickAsync(.5);
        Assert.Equal("after timer", runtime.Story.Text);
        Assert.False(runtime.Story.NeedsTick);
    }

    [Fact]
    public async Task WaitCondition_ObservesConcurrentState_AndTimeoutIsCatchableAcrossYield()
    {
        using var fixture = Scene("""
            state.task = kne_async({function() kne_pause(.5); state.ready = true end})
            kne_wait_condition(function() return state.ready == true end, 1)
            local ok, problem = pcall(function() kne_wait_condition(function() return false end, .2) end)
            state.timeout = not ok and problem:find("Истекло") ~= nil
            d(nil, "ready")
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.TickAsync(.5);
        await runtime.TickAsync(.01);
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.TickAsync(.2);
        Assert.Equal("ready", runtime.Story.Text);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("timeout").GetBoolean());
    }

    [Fact]
    public async Task AsyncChoice_RejectsInputRatherThanChoosingSilently_AndReportsItsScriptLocation()
    {
        using var fixture = Scene("kne_async({function() choice('forbidden', {'a','b'}) end}); d(nil, 'foreground')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Выбор недоступен", error.Message);
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File);
        Assert.Equal(2, report.Line);
    }

    [Fact]
    public async Task Checkpoint_ReplaysLogicalTicksAndSeed_AndRestoresStillRunningTasks()
    {
        using var fixture = Scene("""
            state.task = kne_async({function() kne_pause(1); state.roll = math.random(1, 9999) end})
            d(nil, "foreground")
            kne_join(state.task)
            d(nil, "roll:" .. state.roll)
            """);
        var project = await fixture.LoadAsync();
        await using var first = Runtime(project);
        await first.StartAsync();
        await first.TickAsync(.4);
        GameCheckpoint checkpoint = await first.GetCheckpointAsync();
        Assert.Equal("tick", Assert.Single(checkpoint.History).Kind);
        await using var restored = Runtime(project);
        await restored.RestoreAsync(checkpoint);
        await first.AdvanceAsync();
        await restored.AdvanceAsync();
        Assert.Equal("wait", restored.Story.Kind);
        await first.TickAsync(.6);
        await restored.TickAsync(.6);
        Assert.Equal(first.Story.Text, restored.Story.Text);
        Assert.StartsWith("roll:", restored.Story.Text);
        Assert.Equal((await first.GetCheckpointAsync()).State.GetProperty("roll").GetInt64(),
            (await restored.GetCheckpointAsync()).State.GetProperty("roll").GetInt64());
    }

    [Theory]
    [InlineData("menu", null)]
    [InlineData("screen", "main_menu")]
    [InlineData("exit", null)]
    public async Task End_ProducesExplicitHostAction_CancelsBackgroundTasks_AndDoesNotRunFollowingCode(string action, string? screen)
    {
        using var fixture = Scene($"kne_async({{function() kne_pause(.1); error('must be cancelled') end}}); kne_end('{action}', {(screen == null ? "nil" : "'" + screen + "'")}); error('after end')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.True(runtime.Story.Completed);
        Assert.Equal(action, runtime.Story.EndAction);
        Assert.Equal(screen, runtime.Story.EndScreen);
        Assert.False(runtime.Story.NeedsTick);
        await runtime.TickAsync(1);
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task RootGoto_CancelsDetachedTasks_FromPreviousScene()
    {
        using var fixture = Scene("kne_async({function() kne_pause(.1); error('old scene') end}); goto_scene(next_scene)",
            "function next_scene() kne_pause(.2); d(nil, 'next scene') end");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await runtime.TickAsync(.2);
        Assert.Equal("next scene", runtime.Story.Text);
    }

    [Fact]
    public async Task Worker_TransportsTicksAndCompletionAction_WithoutConsumingForegroundDialogue()
    {
        using var fixture = Scene("state.task=kne_async({function() kne_pause(.2); state.done=true end}); d(nil,'worker'); kne_join(state.task); kne_end('menu')");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.True(runtime.Story.HasBackgroundTasks);
        await runtime.TickAsync(.2);
        Assert.Equal("worker", runtime.Story.Text);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("done").GetBoolean());
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
        Assert.Equal("menu", runtime.Story.EndAction);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(60.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Tick_RejectsInvalidIntervalsWithoutChangingState(double seconds)
    {
        using var fixture = Scene("kne_pause(1); d(nil,'after')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.TickAsync(seconds));
        Assert.Equal("wait", runtime.Story.Kind);
    }

    [Fact]
    public async Task BoundedTasks_AndUnknownJoin_ReportErrorsRatherThanHanging()
    {
        using var fixture = Scene("for i=1,129 do kne_async({function() kne_pause(10) end}) end");
        await using (var runtime = Runtime(await fixture.LoadAsync()))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
            Assert.Contains("128", error.Message);
        }
        fixture.Write("game/scenes/main.krl", "function main() kne_join(999) end");
        await using var other = Runtime(await fixture.LoadAsync());
        var joinError = await Assert.ThrowsAnyAsync<Exception>(() => other.StartAsync());
        Assert.Contains("Не найдена асинхронная ветка", joinError.Message);
    }

    [Fact]
    public async Task WaitingScene_DisposesImmediately_AndCancelledTicksDoNotAdvanceLogicalTime()
    {
        using var fixture = Scene("kne_pause(3600); d(nil,'never')");
        var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.TickAsync(1, cancelled.Token));
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("kne_pause(-.1)", "Время")]
    [InlineData("kne_pause(3600.01)", "Время")]
    [InlineData("kne_parallel({})", "64")]
    [InlineData("kne_async({123})", "функцией")]
    [InlineData("kne_end('screen','missing-screen')", "Экран не найден")]
    [InlineData("kne_end('screen','')", "Укажите экран")]
    [InlineData("kne_end('invalid')", "действие")]
    public async Task Flow_InvalidArguments_ReportActionableErrors(string code, string expected)
    {
        using var fixture = Scene(code);
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Join_RejectsWaitingForOwnGroup()
    {
        using var fixture = Scene("state.task=kne_async({function() kne_pause(.1); kne_join(state.task) end}); d(nil,'foreground')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.TickAsync(.1));
        Assert.Contains("собственной группы", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryCatch_CatchesJoinedBranchErrorsAfterPause_AndCancelsItsSiblings(bool detached)
    {
        string branches = "{function() kne_pause(.1); error('caught branch') end, function() kne_pause(.2); state.sibling=true end}";
        string operation = detached ? "local handle=kne_async(" + branches + "); kne_join(handle)" : "kne_parallel(" + branches + ")";
        using var fixture = Scene("local ok, problem=pcall(function() " + operation + " end); state.caught=not ok and tostring(problem):find('caught branch')~=nil; d(nil,'caught')");
        await using var runtime = Runtime(await fixture.LoadAsync());
        await runtime.StartAsync();
        Assert.Equal("wait", runtime.Story.Kind);
        await runtime.TickAsync(.1);
        Assert.Equal("caught", runtime.Story.Text);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("caught").GetBoolean());
        await runtime.TickAsync(.2);
        Assert.False((await runtime.GetCheckpointAsync()).State.TryGetProperty("sibling", out _));
    }

    [Fact]
    public async Task CaughtBranchError_DoesNotBlameItsOldLocation_ForLaterIdenticalUncaughtError()
    {
        using var fixture = Scene("""
            local ok = pcall(function()
                kne_parallel({function() error('same error', 0) end})
            end)
            if ok then error('expected catch') end
            error('same error', 0)
            """);
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal(6, report.Line);
        Assert.Equal("game/scenes/main.krl", report.File);
        Assert.Contains("same error", error.Message);
    }

    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true);
    private static TempProject Scene(string body, string extra = "")
    {
        var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n" + body + "\nend\n" + extra);
        return fixture;
    }
}
