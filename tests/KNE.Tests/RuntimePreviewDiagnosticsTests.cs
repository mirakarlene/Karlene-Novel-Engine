using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class RuntimePreviewDiagnosticsTests
{
    [Fact]
    public async Task Variables_IncludeUpdatedClosureUpvalues_WithoutCopyingGlobalEnvironment()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            local captured_count = 5
            function main()
                local current = captured_count
                d(nil, tostring(current))
                captured_count = captured_count + 1
                d(nil, tostring(captured_count))
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Contains(runtime.Variables.GetProperty("Frames").EnumerateObject(), frame =>
            frame.Value.TryGetProperty("captured_count (замыкание)", out var value) && value.GetInt32() == 5);
        await runtime.AdvanceAsync();
        Assert.Equal("6", runtime.Story.Text);
        Assert.Contains(runtime.Variables.GetProperty("Frames").EnumerateObject(), frame =>
            frame.Value.TryGetProperty("captured_count (замыкание)", out var value) && value.GetInt32() == 6);
        Assert.DoesNotContain("_ENV", runtime.Variables.GetRawText());
        Assert.DoesNotContain("coroutine", runtime.Variables.GetRawText());
    }

    [Fact]
    public async Task Catalog_UsesAbsoluteUnsavedGraphOverride_AndRejectsUndeclaredPreviewEntry()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("game/scenes/main.knegraph", new VisualScript().Serialize());
        var source = new EditorPreviewSource(path, "function drafted() d(nil, 'draft') end", "missing");
        var catalog = GameScripts.Load(await fixture.LoadAsync(), source);
        Assert.Equal("function drafted() d(nil, 'draft') end", catalog.FindFile("game/scenes/main.knegraph").ExecutableSource);
        Assert.Contains(catalog.Functions, function => function.Name == "drafted" && function.Path == "game/scenes/main.knegraph");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, preview: true, editorSource: source);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => runtime.StartAsync());
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public async Task UnsavedCode_PausesAfterVisualCommandWithoutChangingFile_ThenShowsDialogue()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("game/scenes/main.krl", "function main() d(nil, 'disk') end");
        fixture.Write("game/scripts/gamelogic.lua", "set_bg_color('#000000')");
        var source = new EditorPreviewSource("game/scenes/main.krl", """
            function main()
                set_bg_color('#112233')
                d('Алиса', 'unsaved')
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, preview: true, editorSource: source);
        await runtime.StartAsync();
        Assert.True(runtime.PreviewPaused);
        Assert.Equal("#112233", runtime.WindowSettings.Background);
        Assert.Equal(new PreviewLocation("game/scenes/main.krl", 2, null, 1), runtime.Location);
        await runtime.AdvanceAsync();
        Assert.Equal("unsaved", runtime.Story.Text);
        Assert.Equal("Алиса", runtime.Story.Speaker);
        Assert.Equal(3, runtime.Location!.Line);
        Assert.Equal("function main() d(nil, 'disk') end", File.ReadAllText(path));
    }

    [Fact]
    public async Task SelectedFunction_AndCoroutineNodesRemainScoped_AcrossCallReturnAndChoice()
    {
        using var fixture = new TempProject();
        var source = new EditorPreviewSource("game/scenes/main.krl", """
            function main() d(nil, 'wrong entry') end
            function chapter()
                kne_editor_node('caller')
                call(child)
                kne_editor_node('choice')
                local answer = choice('Choose', {'one', 'two'})
                d(nil, tostring(answer))
            end
            """, "chapter");
        fixture.Write("game/scenes/child.lua", "function child() kne_editor_node('child'); set_bg_color('#334455'); d(nil, 'child'); end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, preview: true, editorSource: source);
        await runtime.StartAsync();
        Assert.Equal("caller", runtime.Location!.NodeId);
        await runtime.AdvanceAsync();
        Assert.Equal("child", runtime.Location!.NodeId);
        Assert.Equal("#334455", runtime.WindowSettings.Background);
        await runtime.AdvanceAsync();
        Assert.Equal("child", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal("choice", runtime.Location!.NodeId);
        await runtime.ChooseAsync(2);
        Assert.Equal("2", runtime.Story.Text);
    }

    [Fact]
    public async Task Worker_StreamsGlobalsAndCallerLocals_AndReturnsAfterFailedTransition()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                local caller_value = 42
                global_score = 3
                d(nil, 'safe')
                call(child)
                d(nil, 'after')
            end
            """);
        fixture.Write("game/scenes/child.lua", "function child() local child_value = '🌸'; state.score = 9; error('child failure') end");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        Assert.Equal(3, session.Variables.GetProperty("Globals").GetProperty("global_score").GetInt32());
        Assert.Contains(session.Variables.GetProperty("Frames").EnumerateObject(), frame =>
            frame.Value.TryGetProperty("caller_value", out var value) && value.GetInt32() == 42);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        Assert.True(GameErrorReport.TryGet(error)!.TransitionFailure);
        Assert.True(session.CanReturnAfterError);
        Assert.Equal(9, session.Variables.GetProperty("Globals").GetProperty("state").GetProperty("score").GetInt32());
        Assert.Contains(session.Variables.GetProperty("Frames").EnumerateObject(), frame =>
            frame.Value.TryGetProperty("child_value", out var value) && value.GetString() == "🌸");
        await session.ReturnAsync();
        Assert.Equal("safe", session.Story.Text);
        Assert.False(session.CanReturnAfterError);
        Assert.Equal(0, session.Variables.GetProperty("Globals").GetProperty("state").GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Return_RestoresChildSegment_WhenCallerFailsImmediatelyAfterCoroutineReturn()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call(child); error('return failure') end\nfunction child() d(nil, 'safe child') end");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        Assert.Equal("safe child", session.Story.Text);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        Assert.True(GameErrorReport.TryGet(error)!.TransitionFailure);
        Assert.True(session.CanReturnAfterError);
        await session.ReturnAsync();
        Assert.Equal("safe child", session.Story.Text);
    }

    [Fact]
    public async Task TimeoutDuringTransfer_CanReturnToSafeFrame_EvenIfChildDisablesItsHook()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'safe'); call(child) end\nfunction child() debug.sethook(); while true do end end");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(1));
        await session.StartAsync();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => session.AdvanceAsync());
        Assert.True(GameErrorReport.TryGet(error)!.TransitionFailure);
        Assert.True(session.CanReturnAfterError);
        await session.ReturnAsync();
        Assert.Equal("safe", session.Story.Text);
        Assert.False(session.CanReturnAfterError);
    }

    [Theory]
    [InlineData("function main() error('before frame') end")]
    [InlineData("function main() d(nil, 'safe'); error('ordinary error') end")]
    [InlineData("function main() d(nil, 'safe'); call(child) end\nfunction child() d(nil, 'child safe'); error('after transition') end")]
    public async Task Return_IsUnavailableWithoutSafeTransferFailure(string code)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", code);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await session.StartAsync();
            for (int step = 0; step < 3; step++) await session.AdvanceAsync();
        });
        Assert.False(session.CanReturnAfterError);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReturnAsync());
    }

    [Fact]
    public async Task CyclicAndLargeVariables_AreBounded_WithoutBreakingStory()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                inventory = {name = 'bag'}
                inventory.self = inventory
                inventory.many = {}
                for i = 1, 1000 do inventory.many[i] = i end
                math = 17
                d(nil, 'safe')
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("safe", runtime.Story.Text);
        Assert.Equal("<cycle>", runtime.Variables.GetProperty("Globals").GetProperty("inventory").GetProperty("self").GetString());
        Assert.Equal(17, runtime.Variables.GetProperty("Globals").GetProperty("math").GetInt32());
        Assert.True(runtime.Variables.GetRawText().Length < 30000);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task Worker_UpdatesVariablesWhileCommandIsStillRunning()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            counter = 0
            function process_command(command)
                print('loop entered')
                while true do counter = counter + 1 end
            end
            """);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), line =>
        {
            if (line == "loop entered") entered.TrySetResult();
        });
        await session.StartAsync();
        using var cancellation = new CancellationTokenSource();
        Task running = session.ProcessCommandAsync("loop", cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (session.Variables.GetProperty("Globals").GetProperty("counter").GetInt64() == 0)
                await Task.Delay(10, deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.True(session.Variables.GetProperty("Globals").GetProperty("counter").GetInt64() > 0);
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
    }
}
