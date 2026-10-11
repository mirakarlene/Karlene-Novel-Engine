using System.Collections.Concurrent;
using Xunit;

namespace KNE.Tests;

public sealed class StoryRuntimeTests
{
    [Fact]
    public async Task Dialogue_PausesAndChoiceReturnsBranchIndex()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                d("Алиса", "Первая")
                local answer = choice("Куда?", {"Домой", "В парк"})
                if answer == 2 then d(nil, "Парк") else d(nil, "Дом") end
                d(nil, "Последняя")
            end
            """);
        var log = new ConcurrentQueue<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), log.Enqueue, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Первая", runtime.Story.Text);
        Assert.DoesNotContain("Парк", log);
        await runtime.AdvanceAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal(new[] { "Домой", "В парк" }, runtime.Story.Options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.ChooseAsync(0));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AdvanceAsync());
        await runtime.ChooseAsync(2);
        Assert.Equal("Парк", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("Последняя", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AdvanceAsync());
    }
}
