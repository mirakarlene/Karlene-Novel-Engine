using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class StartupScriptsTests
{
    [Theory]
    [InlineData("init(-10000)", -10000)]
    [InlineData("init(+10000)", 10000)]
    [InlineData("-- comment\rinit(-5)", -5)]
    [InlineData("\uFEFF -- init(99)\n--[=[ ignored init(5) ]=]\ninit(\n -20\n)", -20)]
    public void Declaration_LeadingCommentsAndBoundaries_PreserveLineNumbers(string prefix, int priority)
    {
        string source = prefix + "\nstate.ready = true";
        var result = StartupScripts.Declaration(source);
        Assert.Equal(priority, result.Priority);
        Assert.Equal(source.Length, result.Source.Length);
        Assert.Equal(source.Select((c, i) => (c, i)).Where(p => p.c is '\n' or '\r'),
            result.Source.Select((c, i) => (c, i)).Where(p => p.c is '\n' or '\r'));
        Assert.EndsWith("\nstate.ready = true", result.Source);
        int start = prefix.LastIndexOf("init(", StringComparison.Ordinal);
        Assert.All(result.Source.Substring(start, prefix.Length - start), c => Assert.True(c is ' ' or '\r' or '\n'));
    }

    [Theory]
    [InlineData("init(10001)")]
    [InlineData("init(-10001)")]
    [InlineData("init(1.5)")]
    [InlineData("init('early')")]
    [InlineData("init()")]
    public void Declaration_InvalidPriority_ExplainsRequiredSyntax(string source)
    {
        Assert.Contains("-10000 до 10000", Assert.Throws<InvalidDataException>(() => StartupScripts.Declaration(source)).Message);
    }

    [Theory]
    [InlineData("initialize = true")]
    [InlineData("init_value = 7")]
    [InlineData("-- init(-10)\nfunction main() end")]
    [InlineData("state.x = 1; init(-10)")]
    public void Declaration_NonLeadingCallsAndSimilarIdentifiers_DoNotOptIn(string source)
    {
        var result = StartupScripts.Declaration(source);
        Assert.Null(result.Priority);
        Assert.Equal(source, result.Source);
    }

    [Fact]
    public async Task Plan_PrioritiesAndOrdinalTies_IncludeLegacyButExcludeOrdinaryModules()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "state.order = state.order .. 'legacy,'");
        fixture.Write("startup/early.krl", "init(-10) state.order = 'early,'");
        fixture.Write("startup/Z.lua", "init(10) state.order = state.order .. 'Z,'");
        fixture.Write("startup/a.lua", "init(10) state.order = state.order .. 'a,'");
        fixture.Write("startup/module.lua", "error('ordinary modules must not execute')");
        fixture.Write("game/scenes/main.krl", "function main() d(nil, state.order) end");
        var project = await fixture.LoadAsync();
        var plan = StartupScripts.Plan(project);
        Assert.Equal(new[] { -10, 0, 10, 10 }, plan.Select(s => s.Priority));
        Assert.Equal(new[] { "early.krl", "gamelogic.lua", "Z.lua", "a.lua" }, plan.Select(s => Path.GetFileName(s.Path)));
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("early,legacy,Z,a,", runtime.Story.Text);
        var checkpoint = await runtime.GetCheckpointAsync();
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(checkpoint);
        Assert.Equal(runtime.Story.Text, restored.Story.Text);
        Assert.Equal(runtime.Story.Kind, restored.Story.Kind);
        Assert.Equal("early,legacy,Z,a,", (await restored.GetCheckpointAsync()).State.GetProperty("order").GetString());
    }

    [Theory]
    [InlineData("function main() d(nil, state.loads) end")]
    [InlineData("return function() d(nil, state.loads) end")]
    public async Task InitScene_CachesEntryAndExecutesTopLevelOnlyOnce(string entry)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/child.krl", "init(-10) state.loads = (state.loads or 0) + 1; " + entry);
        fixture.Write("game/scenes/main.krl", "function main() call_scene('game/scenes/child.krl'); call_scene('GAME/SCENES/CHILD.KRL'); d(nil, state.loads) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("1", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("1", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("1", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task LateInit_IsRejectedDuringGameplay()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() init(-10) end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("первым оператором", error.Message);
    }

    [Fact]
    public async Task InitModuleWithoutEntry_IsNotExecutedAgain_WhenCalledAsScene()
    {
        using var fixture = new TempProject();
        fixture.Write("startup.lua", "init(-10) state.loads = (state.loads or 0) + 1");
        fixture.Write("game/scenes/main.krl", "function main() call_scene('startup.lua') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        Assert.Contains("main()", (await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync())).Message);
    }

    [Fact]
    public async Task InitScene_MainBindingRemainsLocal_AfterOtherFilesLoad()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/child.krl", "init(-10) function main(n) if n > 0 then main(n - 1) else d(nil, 'Своя main') end end");
        fixture.Write("game/scenes/main.krl", "function main() call_scene('game/scenes/child.krl', 1); d(nil, 'Возврат') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Своя main", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("Возврат", runtime.Story.Text);
    }
}
