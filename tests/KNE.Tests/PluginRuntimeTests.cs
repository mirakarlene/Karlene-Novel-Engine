using System.Collections.Concurrent;
using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class PluginRuntimeTests
{
    private const string IncrementPlugin = """
        using System;
        using KNE;
        public sealed class Increment : IKnePlugin
        {
            private int visits;
            public void Execute(IPluginContext context)
            {
                visits++;
                context.Set("count", Convert.ToDouble(context.Get("count") ?? 0d) + 1);
                context.Set("visits", visits);
                context.Set("flag", true);
                context.Set("temporary", null);
                context.Log("C# visit " + visits);
            }
        }
        """;

    [Fact]
    public async Task LuaInclude_ExecutesOnceWhileScriptRepeats_AndCanYieldDialogueInCurrentScene()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/helper.lua", """
            state.count = (state.count or 0) + 1
            function helper_text() return 'helper ' .. state.count end
            d(nil, 'plugin dialogue')
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                include_lua('game/plugins/helper.lua')
                include_lua('GAME/plugins/HELPER.lua')
                run_script('game/plugins/helper.lua')
                run_script('game/plugins/helper.lua')
                d(nil, helper_text())
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("plugin dialogue", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("plugin dialogue", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("plugin dialogue", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("helper 3", runtime.Story.Text);
        Assert.Equal(3, (await runtime.GetCheckpointAsync()).State.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CSharpInclude_CompilesWithoutExternalSdk_UsesScalarStateAndLog_AndScriptReusesInstance()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/counter.cs", IncrementPlugin);
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.temporary = 'remove'
                include_cs('game/plugins/counter.cs')
                include_cs('game/plugins/counter.cs')
                run_script('game/plugins/counter.cs')
                d(nil, state.count .. ':' .. state.visits .. ':' .. tostring(state.flag))
            end
            """);
        var log = new ConcurrentQueue<string>();
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), log.Enqueue, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("2.0:2.0:true", runtime.Story.Text);
        Assert.Contains("C# visit 1", log);
        Assert.Contains("C# visit 2", log);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.False(state.TryGetProperty("temporary", out _));
        Assert.True(state.GetProperty("flag").GetBoolean());
        Assert.Equal(2, state.GetProperty("count").GetDouble());
    }

    [Fact]
    public async Task Restore_ReinitializesPluginInstancesAndIncludeCache_AndRejectsChangedCSharpSource()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/counter.cs", IncrementPlugin);
        fixture.Write("game/scenes/main.krl", """
            function main()
                include_cs('game/plugins/counter.cs')
                d(nil, 'first')
                run_script('game/plugins/counter.cs')
                d(nil, 'second ' .. state.visits)
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, executionLimit: TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        var first = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync();
        Assert.Equal("second 2.0", runtime.Story.Text);
        await runtime.RestoreAsync(first);
        Assert.Equal("first", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("second 2.0", runtime.Story.Text);
        fixture.Write("game/plugins/counter.cs", IncrementPlugin + "\n// source changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.RestoreAsync(first));
    }

    [Theory]
    [InlineData("return", "ровно один public класс")]
    [InlineData("using KNE; public class Bad : IKnePlugin { public Bad(int x) {} public void Execute(IPluginContext c) {} }", "конструктор без параметров")]
    [InlineData("using KNE; public class A : IKnePlugin { public void Execute(IPluginContext c) {} } public class B : IKnePlugin { public void Execute(IPluginContext c) {} }", "ровно один public класс")]
    public async Task CSharpPlugin_InvalidContractAndSyntax_AreVisibleGameErrors(string source, string expected)
    {
        using var fixture = new TempProject();
        if (source == "return") source = "public class NoContract {}";
        fixture.Write("game/plugins/bad.cs", source);
        fixture.Write("game/scenes/main.krl", "function main() include_cs('game/plugins/bad.cs') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, executionLimit: TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(expected, error.Message);
        Assert.Equal("game/plugins/bad.cs", GameErrorReport.TryGet(error)!.File);
    }

    [Theory]
    [InlineData("using KNE;\npublic class Bad : IKnePlugin {\n public void Execute(IPluginContext context) { invalid syntax; }\n}", 3, "CS")]
    [InlineData("using KNE;\npublic class Bad : IKnePlugin {\n public void Execute(IPluginContext context) { throw new System.Exception(\"plugin boom\"); }\n}", 3, "plugin boom")]
    public async Task CSharpFailure_PreservesOriginalFileLineAndCallingBlockIdentity(string source, int line, string message)
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/bad.cs", source);
        fixture.Write("game/scenes/main.krl", "function main()\n include_cs('game/plugins/bad.cs')\nend");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/plugins/bad.cs", report.File);
        Assert.Equal(line, report.Line);
        Assert.Contains(message, report.Message);
        Assert.NotNull(report.Frame);
        Assert.EndsWith("::main", report.Frame.Scene);
        Assert.NotEmpty(report.Frame.BlockId);
    }

    [Fact]
    public async Task LuaFailure_PreservesPluginSourceAndLine()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/bad.lua", "state.ok = true\nerror('lua plugin boom')");
        fixture.Write("game/scenes/main.krl", "function main() include_lua('game/plugins/bad.lua') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/plugins/bad.lua", report.File);
        Assert.Equal(2, report.Line);
        Assert.Contains("lua plugin boom", report.Message);
    }

    [Theory]
    [InlineData("../outside.lua", "lua")]
    [InlineData("C:/outside.lua", "lua")]
    [InlineData("game/plugins/helper.cs", "lua")]
    [InlineData("game/plugins/helper.lua", "cs")]
    [InlineData("game/plugins/helper.txt", "script")]
    [InlineData("game/plugins/missing.lua", "lua")]
    public async Task PluginReader_RejectsEscapingMissingAndWrongExtension(string path, string kind)
    {
        using var fixture = new TempProject();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        var result = runtime.ReadPluginForLua(path, kind);
        Assert.Equal("", result[0]);
        Assert.Equal("", result[1]);
        Assert.NotEmpty(result[2]);
    }

    [Fact]
    public async Task PluginReader_RejectsOversizeAndDoesNotRewriteSource()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("game/plugins/large.lua", new string(' ', 8 * 1024 * 1024 + 1));
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        Assert.Contains("8 МБ", runtime.ReadPluginForLua("game/plugins/large.lua", "lua")[2]);
        Assert.Equal(8 * 1024 * 1024 + 1, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8 * 1024 * 1024)]
    public async Task PluginReader_AcceptsEmptyAndExactSizeBoundary(int size)
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/boundary.lua", new string(' ', size));
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { });
        var result = runtime.ReadPluginForLua("game/plugins/boundary.lua", "lua");
        Assert.Equal("", result[2]);
        Assert.Equal(size, result[0].Length);
        Assert.Equal("game/plugins/boundary.lua", result[1]);
    }

    [Fact]
    public async Task Script_CompilesEditedCSharpSourceIntoFreshInstance_WhileIncludeRemainsOncePerSession()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/counter.cs", IncrementPlugin);
        fixture.Write("game/scenes/main.krl", """
            function main()
                include_cs('game/plugins/counter.cs')
                d(nil, 'first')
                include_cs('game/plugins/counter.cs')
                run_script('game/plugins/counter.cs')
                d(nil, state.count .. ':' .. state.visits)
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, executionLimit: TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("first", runtime.Story.Text);
        fixture.Write("game/plugins/counter.cs", IncrementPlugin.Replace("+ 1);", "+ 10);", StringComparison.Ordinal));
        await runtime.AdvanceAsync();
        Assert.Equal("11.0:1.0", runtime.Story.Text);
    }

    [Fact]
    public async Task LuaInclude_RejectsRecursiveIncludesBeforeStackOverflow()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/recursive.lua", "include_lua('game/plugins/recursive.lua')");
        fixture.Write("game/scenes/main.krl", "function main() include_lua('game/plugins/recursive.lua') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("Циклическое", error.Message);
    }

    [Fact]
    public async Task CSharpContext_RejectsRetainedAndCrossThreadUse_WithoutLeakingLuaObjects()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/context.cs", """
            using System;
            using System.Threading.Tasks;
            using KNE;
            public class Check : IKnePlugin
            {
                private IPluginContext? old;
                public void Execute(IPluginContext context)
                {
                    if (old != null)
                    {
                        try { old.Get("x"); throw new Exception("retained context accepted"); }
                        catch (ObjectDisposedException) { context.Set("retained_rejected", true); }
                    }
                    Task.Factory.StartNew(() => {
                        try { context.Set("unsafe", true); throw new Exception("cross-thread accepted"); }
                        catch (InvalidOperationException) { }
                    }, System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
                    try { context.Set("bad", double.NaN); throw new Exception("NaN accepted"); }
                    catch (ArgumentException) { context.Set("nan_rejected", true); }
                    try { context.Get("nested"); throw new Exception("table accepted"); }
                    catch (ArgumentException) { context.Set("table_rejected", true); }
                    old = context;
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.nested = {answer = 42}
                run_script('game/plugins/context.cs')
                run_script('game/plugins/context.cs')
                d(nil, 'safe')
            end
            """);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal("safe", runtime.Story.Text);
        Assert.True(state.GetProperty("retained_rejected").GetBoolean());
        Assert.True(state.GetProperty("nan_rejected").GetBoolean());
        Assert.True(state.GetProperty("table_rejected").GetBoolean());
        Assert.False(state.TryGetProperty("unsafe", out _));
        Assert.False(state.TryGetProperty("bad", out _));
        Assert.Equal(42, state.GetProperty("nested").GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task WorkerCancellation_KillsUncooperativeCSharpPluginWithoutBlockingHost()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/hang.cs", """
            using KNE;
            public class Hang : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    if (context.Get("hang") is true) while (true) { }
                    context.Set("hang", true);
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                run_script('game/plugins/hang.cs')
                d(nil, 'compiled')
                run_script('game/plugins/hang.cs')
                d(nil, 'unreachable')
            end
            """);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("compiled", runtime.Story.Text);
        int processId = Assert.IsType<int>(runtime.WorkerProcessId);
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.AdvanceAsync(cancellation.Token));
        Assert.Null(runtime.WorkerProcessId);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
        Assert.DoesNotContain("unreachable", runtime.Story.Text);
    }
}
