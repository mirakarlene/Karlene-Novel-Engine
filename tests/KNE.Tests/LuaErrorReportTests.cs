using System.Runtime.InteropServices;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class LuaErrorReportTests
{
    [Fact]
    public async Task HandledLazyLoadError_DoesNotReplaceLocationOfLaterUncaughtFailure()
    {
        using var fixture = new TempProject();
        fixture.Write("helper.lua", "error('HANDLED_MARKER')\nfunction helper() end");
        fixture.Write("game/scenes/main.krl", "function main()\n local ok = pcall(helper)\n assert(ok == false)\n local missing = nil\n missing.field = 7\nend");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(5, report.Line);
        Assert.Contains("nil", report.Message); Assert.DoesNotContain("HANDLED_MARKER", report.Details);
    }
    [Fact]
    public async Task ChildFailure_ReportsOriginalLongUnicodePathAndLine_WithWorkerMetadata()
    {
        using var fixture = new TempProject();
        string childPath = "game/scenes/очень длинное имя дочерней функции с пробелами и символами 🌸.krl";
        fixture.Write(childPath, """
            function child()
                d(nil, 'Дочерняя')
                local missing = nil
                missing.field = 7
            end
            """);
        fixture.Write("game/scenes/main.krl", "function main() call(child) end");
        var project = await fixture.LoadAsync();
        await using var session = new ProcessGameSession(project, _ => { });
        await session.StartAsync();
        var before = DateTimeOffset.UtcNow;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        var after = DateTimeOffset.UtcNow;
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal(childPath, report.File); Assert.Equal(4, report.Line);
        Assert.Contains("nil", report.Message);
        Assert.Contains("stack traceback", report.Details);
        Assert.Equal(project.Title, report.ProjectTitle); Assert.Equal(project.Name, report.ProjectName);
        Assert.Equal(project.Path, report.ProjectPath);
        Assert.Equal(RuntimeInformation.OSDescription, report.OperatingSystem);
        Assert.Equal(Environment.MachineName, report.DeviceName);
        Assert.Equal(RuntimeInformation.OSArchitecture.ToString(), report.Architecture);
        Assert.Equal("Продолжение сценария", report.Operation);
        Assert.InRange(report.OccurredAt.ToUniversalTime(), before, after);
        Assert.Same(report, GameErrorReport.Create(project, error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializerOrLazySyntaxFailure_ReportsTheCompiledFile_NotScheduler(bool lazy)
    {
        using var fixture = new TempProject();
        string path = lazy ? "game/scenes/child.lua" : "game/scripts/gamelogic.lua";
        fixture.Write(path, lazy ? "function child()\n local broken = )\nend" : "local broken = )");
        fixture.Write("game/scenes/main.krl", "function main() call(child) end");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal(path, report.File); Assert.Equal(lazy ? 2 : 1, report.Line);
        Assert.Contains("unexpected", report.Message);
        Assert.Equal("Запуск игры", report.Operation);
    }

    [Fact]
    public async Task DebugCommandFailure_CapturesCommandFunctionSource_AndCanRecoverWithNextCommand()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(command)\n if command == 'bad' then error('COMMAND_MARKER') end\n state.score = 7\nend");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.ProcessCommandAsync("bad"));
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scripts/gamelogic.lua", report.File); Assert.Equal(2, report.Line);
        Assert.Contains("COMMAND_MARKER", report.Message); Assert.Equal("Команда отладки", report.Operation);
        await session.ProcessCommandAsync("good");
        Assert.Equal(7, (await session.GetCheckpointAsync()).State.GetProperty("score").GetInt32());
    }

    [Theory]
    [InlineData("background('game/backgrounds/missing.png')", "не найден")]
    [InlineData("show_character('x.png', 'invalid')", "Позиция")]
    [InlineData("set_window_size(1, 1)", "Размер окна")]
    [InlineData("set_bg_color('bad-color')", "set_bg_color")]
    public async Task ManagedApiFailure_IsRaisedInScriptFrame_InsteadOfBeingSilentlyIgnored(string command, string expected)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n d(nil, 'Ready')\n " + command + "\n d(nil, 'must not run')\nend");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(3, report.Line);
        Assert.Contains(expected, report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ready", session.Story.Text);
    }

    [Fact]
    public async Task InvalidTransition_ReportsCallingLine_AndCancellationIsNotScriptFailure()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n d(nil, 'Ready')\n call('missing')\nend");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(3, report.Line);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.AdvanceAsync(cancellation.Token));
        Assert.Null(GameErrorReport.TryGet(canceled));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerTimeout_HasUnknownSourceReport_WhileActiveUserCancellationHasNoReport(bool cancel)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Ready'); print('entered loop'); debug.sethook(); while true do end end");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), line => { if (line == "entered loop") entered.TrySetResult(); }, TimeSpan.FromSeconds(3));
        await session.StartAsync();
        using var cancellation = new CancellationTokenSource();
        var blocked = session.AdvanceAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cancel)
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(GameErrorReport.TryGet(error));
        }
        else
        {
            var error = await Assert.ThrowsAsync<TimeoutException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
            var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
            Assert.Null(report.File); Assert.Null(report.Line);
            Assert.Equal(nameof(TimeoutException), report.ErrorType);
            Assert.Equal("Продолжение сценария", report.Operation);
            Assert.Equal(RuntimeInformation.OSDescription, report.OperatingSystem);
        }
        Assert.Equal("Ready", session.Story.Text);
    }
}
