using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class FunctionTransitionTests
{
    [Theory]
    [InlineData("lmao")]
    [InlineData("'lmao'")]
    public async Task CallAndGoto_FunctionTargetsAcrossFiles_PreserveReturnsAndDiscardJumpedBranch(string target)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                local saved = 40
                local result, hole, flower = call({{target}}, 2)
                assert(hole == nil)
                d(nil, saved + result .. flower)
                goto(finale)
                error('discarded root')
            end
            """);
        fixture.Write("game/scenes/дочерняя.krl", """
            state.loads = (state.loads or 0) + 1
            function lmao(n)
                local answer = call(nested)
                d(nil, 'Дочерняя: ' .. answer)
                return n + answer, nil, '🌸'
            end
            function nested() return choice('Путь', {'Один', 'Два'}) end
            function finale() d(nil, 'Финал: ' .. state.loads) end
            """);
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync(); Assert.Equal("choice", runtime.Story.Kind);
        await runtime.ChooseAsync(2); Assert.Equal("Дочерняя: 2", runtime.Story.Text);
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(await runtime.GetCheckpointAsync()))!);
        await restored.AdvanceAsync(); Assert.Equal("44🌸", restored.Story.Text);
        await restored.AdvanceAsync(); Assert.Equal("Финал: 1", restored.Story.Text);
        await restored.AdvanceAsync(); Assert.True(restored.Story.Completed);
    }

    [Fact]
    public async Task GotoInsideCall_PreservesCaller_AndNativeLuaLabelsStillWork()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main() local n = call(branch); d(nil, 'Возврат: ' .. n) end
            function branch() goto(target, 7); error('discarded') end
            function target(n)
                goto continue
                error('native goto failed')
                ::continue::
                d(nil, 'Цель')
                return n
            end
            """);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync(); Assert.Equal("Цель", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("Возврат: 7", runtime.Story.Text);
    }

    [Theory]
    [InlineData("'missing'", "Не найдена функция: missing")]
    [InlineData("missing", "существующей функцией")]
    [InlineData("'game/scenes/child.lua'", "вместо пути")]
    [InlineData("42", "существующей функцией")]
    public async Task InvalidFunctionTarget_HasActionableError(string target, string message)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main() call(" + target + ") end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        Assert.Contains(message, (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task Preview_UsesSelectedFunction_AndCannotRestoreAsNormalGame()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Одинаковая реплика'); d(nil, 'main') end");
        fixture.Write("helper.lua", "function helper() d(nil, 'Одинаковая реплика'); d(nil, 'helper') end");
        var project = await fixture.LoadAsync(); project.Configuration.EntryPoints!.MainScene = "helper.lua";
        await using var preview = new ProcessGameSession(project, _ => { }, preview: true);
        await preview.StartAsync(); var checkpoint = await preview.GetCheckpointAsync();
        Assert.Equal("Одинаковая реплика", preview.Story.Text);
        await preview.AdvanceAsync(); Assert.Equal("helper", preview.Story.Text);
        await using var game = new ProcessGameSession(project, _ => { });
        await game.StartAsync(); Assert.Equal("Одинаковая реплика", game.Story.Text);
        Assert.Contains("сценарии изменились", (await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => game.RestoreAsync(checkpoint))).Message);
        await game.AdvanceAsync(); Assert.Equal("main", game.Story.Text);
    }
}
