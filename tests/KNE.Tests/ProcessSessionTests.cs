using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class ProcessSessionTests
{
    [Fact]
    public async Task Worker_IsSeparateProcess_TransportsUnicodeBranchesAndSaves()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.score = 0
                d("Алиса", "Привет 🌸")
                local answer = choice("Куда?", {"Домой", "В парк"})
                state.score = answer
                d(nil, "Выбор: " .. answer)
                d(nil, "Очки: " .. state.score)
            end
            """);
        var project = await fixture.LoadAsync();
        var session = new ProcessGameSession(project, _ => { });
        int worker;
        try
        {
            await session.StartAsync();
            worker = session.WorkerProcessId!.Value;
            Assert.NotEqual(Environment.ProcessId, worker);
            Assert.Equal("Привет 🌸", session.Story.Text);
            await session.AdvanceAsync();
            Assert.Equal(new[] { "Домой", "В парк" }, session.Story.Options);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ChooseAsync(0));
            await session.ChooseAsync(2);
            Assert.Equal("Выбор: 2", session.Story.Text);
            var saved = await session.GetCheckpointAsync();
            Assert.Equal(2, saved.State.GetProperty("score").GetInt32());
            await using var restored = new ProcessGameSession(project, _ => { });
            await restored.RestoreAsync(JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(saved))!);
            await restored.AdvanceAsync();
            Assert.Equal("Очки: 2", restored.Story.Text);
        }
        finally { await session.DisposeAsync(); }
        Assert.Null(session.WorkerProcessId);
        Assert.False(IsAlive(worker));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync());
    }

    [Fact]
    public async Task Cancellation_KillsHookBypassingLoop_AndRecoversLastCompletedAction()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            function process_command(command)
                if command == "hang" then
                    print("entered loop")
                    debug.sethook()
                    while true do end
                end
                state.score = state.score + 1
            end
            """);
        fixture.Write("game/scenes/main.krl", "function main() state.score = 5; d(nil, 'Первая'); d(nil, 'Очки: ' .. state.score) end");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), line =>
        {
            if (line == "entered loop") entered.TrySetResult();
        });
        await session.StartAsync();
        await session.ProcessCommandAsync("increment");
        int worker = session.WorkerProcessId!.Value;
        using var cancellation = new CancellationTokenSource();
        var blocked = session.ProcessCommandAsync("hang", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(IsAlive(worker));
        Assert.Equal("Первая", session.Story.Text);
        await session.AdvanceAsync();
        Assert.Equal("Очки: 6", session.Story.Text);
        Assert.NotEqual(worker, session.WorkerProcessId);
    }

    [Fact]
    public async Task HardTimeout_KillsLoopEvenWhenLuaHookIsDisabled()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(command) debug.sethook(); while true do end end");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(3));
        await session.StartAsync();
        int worker = session.WorkerProcessId!.Value;
        await Assert.ThrowsAsync<TimeoutException>(() => session.ProcessCommandAsync("hang"));
        Assert.False(IsAlive(worker));
    }

    [Fact]
    public async Task NativeExit_IsContained_AndDisposeInterruptsBusyWorker()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", """
            function process_command(command)
                if command == "exit" then os.exit(17) end
                print("entered loop"); debug.sethook(); while true do end
            end
            """);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProcessGameSession(await fixture.LoadAsync(), line =>
        {
            if (line == "entered loop") entered.TrySetResult();
        });
        try
        {
            await session.StartAsync();
            await Assert.ThrowsAsync<System.IO.IOException>(() => session.ProcessCommandAsync("exit"));
            var blocked = session.ProcessCommandAsync("hang");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            int worker = session.WorkerProcessId!.Value;
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            Assert.False(IsAlive(worker));
        }
        finally { await session.DisposeAsync(); }
    }

    private static bool IsAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task ClosingParentPipe_ExitsWorkerEvenInsideUninterruptibleLoop()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(command) print('entered loop'); debug.sethook(); while true do end end");
        var info = new ProcessStartInfo(System.IO.Path.ChangeExtension(typeof(App).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("--lua-worker");
        info.ArgumentList.Add(fixture.ProjectPath);
        using var process = Process.Start(info)!;
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Id = 1, Command = "start" }));
            await ReadUntilAsync(process, "result");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Id = 2, Command = "command", Text = "hang" }));
            await ReadUntilAsync(process, "log", "entered loop");
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static async Task ReadUntilAsync(Process process, string type, string? text = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("Type").GetString() == type &&
                (text == null || json.RootElement.GetProperty("Text").GetString() == text)) return;
        }
        throw new InvalidOperationException("Worker exited before expected response.");
    }
}
