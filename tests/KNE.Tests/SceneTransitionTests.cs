using System.Text.Json;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class SceneTransitionTests
{
    [Fact]
    public async Task NestedCall_ReturnsValuesAndLocals_AfterChoiceAndCheckpointRestore()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                local saved_local = 40
                local x, hole, y = call_scene(child, 2, nil, '🌸')
                assert(hole == nil)
                d(nil, saved_local + x .. y)
            end
            """);
        fixture.Write("game/scenes/child.lua", """
            function child(value, hole, flower)
                assert(hole == nil and flower == '🌸')
                local answer = call_scene(grandchild)
                d(nil, 'Дочерняя: ' .. answer)
                return value + answer, nil, flower
            end
            """);
        fixture.Write("game/scenes/grandchild.krl", "function grandchild() return choice('Путь', {'Один', 'Два'}) end");
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal(new[] { "Один", "Два" }, runtime.Story.Options);
        var choiceSave = await runtime.GetCheckpointAsync();
        await using var fromChoice = new ProcessGameSession(project, _ => { });
        await fromChoice.RestoreAsync(JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(choiceSave))!);
        await fromChoice.ChooseAsync(2);
        Assert.Equal("Дочерняя: 2", fromChoice.Story.Text);
        var childSave = await fromChoice.GetCheckpointAsync();
        await using var fromChild = new ProcessGameSession(project, _ => { });
        await fromChild.RestoreAsync(childSave);
        Assert.Equal("Дочерняя: 2", fromChild.Story.Text);
        await fromChild.AdvanceAsync();
        Assert.Equal("44🌸", fromChild.Story.Text);
        await fromChild.AdvanceAsync();
        Assert.True(fromChild.Story.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Goto_DiscardsCurrentContinuation_AndPreservesEarlierCaller(bool insideCall)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", insideCall
            ? "function main() local result = call_scene(branch); d(nil, 'Возврат: ' .. result) end"
            : "function main() goto_scene(branch); error('discarded root resumed') end");
        fixture.Write("game/scenes/branch.krl", "function branch() jump(target, 'Аргумент'); error('discarded branch resumed') end");
        fixture.Write("game/scenes/target.lua", "function target(arg) d(nil, arg); return 7 end");
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync();
        Assert.Equal("Аргумент", runtime.Story.Text);
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(await runtime.GetCheckpointAsync());
        await restored.AdvanceAsync();
        if (insideCall)
        {
            Assert.Equal("Возврат: 7", restored.Story.Text);
            await restored.AdvanceAsync();
        }
        Assert.True(restored.Story.Completed);
    }

    [Fact]
    public async Task NamedScenes_CanUseCallAlias_WithUniqueMain()
    {
        using var fixture = new TempProject();
        fixture.Write("startup.lua", "init(-5) scene('Глава 🌸', function(x) d(nil, 'Глава'); return x + 1 end)");
        fixture.Write("game/scenes/main.krl", "function main() local n = call('Глава 🌸', 6); d(nil, n) end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync(); Assert.Equal("Глава", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("7", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData("game/scenes/missing.krl", "вместо пути")]
    [InlineData("../outside.lua", "вместо пути")]
    [InlineData("noentry", "Не найдена функция")]
    public async Task InvalidDestination_ReportsTargetProblem(string target, string message)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/noentry.lua", "value = 1");
        fixture.Write("game/scenes/data.txt", "data");
        fixture.Write("game/scenes/main.krl", "function main() call_scene('" + target + "') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("scene('same', function() end); scene('same', function() end)", "уже объявлена")]
    [InlineData("call_scene('anything')", "только внутри")]
    public async Task InvalidStartupSceneUse_ReportsClearError(string code, string message)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", code);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task InvalidGraphDestination_ReportsCompilationProblemAcrossCoroutineBoundary()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/broken.knegraph", """{"Version":1,"FunctionName":"broken","Nodes":[]}""");
        fixture.Write("game/scenes/main.krl", "function main() call_scene(broken) end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        Assert.Contains("ровно один", (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task NonInteractiveCallAndGoto_PreserveLegacyPrintOrder()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "state.order = ''");
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Начало'); call_scene(child); d(nil, 'Возврат'); goto_scene(finale); error('discarded') end");
        fixture.Write("game/scenes/child.lua", "function child() d(nil, 'Вызов') end");
        fixture.Write("game/scenes/end.krl", "function finale() d(nil, 'Конец') end");
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Enqueue);
        await runtime.StartAsync();
        Assert.Equal(new[] { "Начало", "Вызов", "Возврат", "Конец" }, output.ToArray());
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public async Task RecursiveCall_HasBoundedDepth(int nestedCalls, bool allowed)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "scene('recursive', function(n) if n > 0 then call_scene('recursive', n - 1) else d(nil, 'Предел') end end)");
        fixture.Write("game/scenes/main.krl", $"function main() call_scene('recursive', {nestedCalls - 1}) end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        if (allowed)
        {
            await runtime.StartAsync(); Assert.Equal("Предел", runtime.Story.Text);
            await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
        }
        else Assert.Contains("больше 64", (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task GotoCycle_StopsAtTransitionBudget()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "scene('loop', function() goto_scene('loop') end)");
        fixture.Write("game/scenes/main.krl", "function main() goto_scene('loop') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        Assert.Contains("10000 переходов", (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task WorkerCancellation_InCalledScene_RecoversStackAndCallerLocals()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() local x = 42; call_scene(child); d(nil, x) end");
        fixture.Write("game/scenes/child.lua", "function child() d(nil, 'Дочерняя'); print('entered child loop'); debug.sethook(); while state.finish ~= true do end end");
        fixture.Write("game/scripts/gamelogic.lua", "function process_command(command) state.finish = true end");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), line => { if (line == "entered child loop") entered.TrySetResult(); });
        await runtime.StartAsync(); int worker = runtime.WorkerProcessId!.Value;
        using var cancellation = new CancellationTokenSource();
        var blocked = runtime.AdvanceAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Дочерняя", runtime.Story.Text);
        await runtime.ProcessCommandAsync("finish");
        Assert.NotEqual(worker, runtime.WorkerProcessId);
        await runtime.AdvanceAsync(); Assert.Equal("42", runtime.Story.Text);
    }
}
