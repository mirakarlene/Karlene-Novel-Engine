using System.Collections.Concurrent;
using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class LuaRuntimeTests
{
    [Fact]
    public async Task Command_MissingHandler_ReportsSpecificError()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "score = 0");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ProcessCommandAsync("hello"));
        Assert.Contains("process_command", error.Message);
    }

    [Fact]
    public async Task Start_UniqueMainInInitialization_IsTheGameEntry()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "function main() print('only main') end");
        fixture.Write("game/scenes/main.krl", "score = 0");
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        Assert.Equal(new[] { "only main" }, output.ToArray());
    }

    [Fact]
    public async Task WindowSettings_InvalidTitleAndColor_PreservePreviousValues()
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        Assert.Throws<ArgumentNullException>(() => runtime.SetWindowTitle(null!));
        Assert.Throws<FormatException>(() => runtime.SetBgColor("invalid-color"));
        Assert.Equal("Новелла с пробелами", runtime.WindowSettings.Title);
        Assert.Equal("#FF201134", runtime.WindowSettings.Background);
    }

    [Theory]
    [InlineData(320, 240)]
    [InlineData(7680, 4320)]
    public async Task WindowSettings_SizeBoundaries_AreAccepted(int width, int height)
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        runtime.SetWindowSize(width, height);
        Assert.Equal(width, runtime.WindowSettings.Width);
        Assert.Equal(height, runtime.WindowSettings.Height);
    }

    [Fact]
    public async Task Commands_RunSeriallyWithoutOverlappingLuaStateAccess()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(command) print(command) end");
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => runtime.ProcessCommandAsync("command-" + i)));
        Assert.Equal(10, output.Count(text => text.StartsWith("command-", StringComparison.Ordinal)));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => "command-" + i).OrderBy(s => s),
            output.Where(s => s.StartsWith("command-", StringComparison.Ordinal)).OrderBy(s => s));
    }

    [Fact]
    public async Task Start_NewProject_PrintsInitializationAndMainDialogueInOrder()
    {
        using var fixture = new TempProject();
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        Assert.Equal(new[]
        {
            "Игра инициализирована!",
            "Добро пожаловать! Это базовая сцена.",
            "Игрок: Нажмите «Далее» или пробел, чтобы продолжить."
        }, output.ToArray());
        await runtime.ProcessCommandAsync("Привет мир");
        Assert.Equal("Игрок: Привет мир", output.Last());
    }

    [Fact]
    public async Task Start_UsesDeclaredScriptsAndPublishesWindowSettings()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", """
            {"entry_points":{"main_scene":"alternate/scene.krl","init_script":"alternate/init.lua"}}
            """);
        fixture.Write("alternate/init.lua", "value = 41");
        fixture.Write("game/scenes/main.krl", "function prologue() end");
        fixture.Write("alternate/scene.krl", """
            function main()
                print(value + 1, nil, true)
                set_window_title("Заголовок")
                set_window_size(1024, 768)
                set_bg_color("#FF123456")
            end
            """);
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        Assert.Equal(new[] { "42\tnil\ttrue" }, output.ToArray());
        Assert.Equal(new RuntimeWindowSettings("Заголовок", 1024, 768, "#FF123456"), runtime.WindowSettings);
    }

    [Fact]
    public async Task Start_MissingMain_ReportsError()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "value = 42");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.StartAsync());
    }

    [Fact]
    public async Task Command_Error_DoesNotPreventNextCommand()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            function process_command(command)
                if command == "bad" then error("bad command") end
                print(command)
            end
            """);
        var output = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => runtime.ProcessCommandAsync("bad"));
        Assert.Contains("bad command", exception.Message);
        await runtime.ProcessCommandAsync("good");
        Assert.Equal("good", output.Last());
    }

    [Fact]
    public async Task Command_BeforeStart_ReportsError()
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ProcessCommandAsync("hello"));
    }

    [Theory]
    [InlineData(319, 240)]
    [InlineData(320, 239)]
    [InlineData(7681, 600)]
    [InlineData(800, 4321)]
    public async Task InvalidWindowSize_IsRejectedWithoutChangingSettings(int width, int height)
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => runtime.SetWindowSize(width, height));
        Assert.Equal(1280, runtime.WindowSettings.Width);
        Assert.Equal(720, runtime.WindowSettings.Height);
    }

    [Fact]
    public async Task Command_LongLoop_IsCancelledAndSessionRemainsUsable()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            function process_command(command)
                if command == "loop" then
                    print("running")
                    while true do end
                end
                print(command)
            end
            """);
        var output = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), text =>
        {
            output.Enqueue(text);
            if (text == "running") started.TrySetResult();
        });
        await runtime.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var work = runtime.ProcessCommandAsync("loop", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(10)));
        await runtime.ProcessCommandAsync("recovered");
        Assert.Equal("recovered", output.Last());
    }

    [Fact]
    public async Task Start_LongLoop_ExceedsExecutionLimit()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() while true do end end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAsync<TimeoutException>(() => runtime.StartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Dispose_RunningLoop_CancelsAndRejectsFurtherCommands()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            function process_command(command)
                print("running")
                while true do end
            end
            """);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new LuaRuntime(await fixture.LoadAsync(), text => { if (text == "running") started.TrySetResult(); });
        await runtime.StartAsync();
        var work = runtime.ProcessCommandAsync("loop");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ProcessCommandAsync("again"));
    }
}
