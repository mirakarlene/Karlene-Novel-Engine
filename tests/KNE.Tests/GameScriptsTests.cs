using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class GameScriptsTests
{
    [Theory]
    [InlineData("function main() end\nfunction main() end", "game/scenes/main.krl:2")]
    [InlineData("function main() end\nmain = function() end", "game/scenes/main.krl:2")]
    public async Task DuplicateMainWithinFile_ReportsBothLocations(string source, string second)
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", source);
        var project = await fixture.LoadAsync();
        string error = Assert.Throws<InvalidDataException>(() => GameScripts.Load(project)).Message;
        Assert.Contains("должна быть одна", error); Assert.Contains("main.krl:1", error); Assert.Contains(second, error);
    }

    [Fact]
    public async Task DuplicateMainInUnusedFile_IsRejectedBeforeInitializerExecutes()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scripts/gamelogic.lua", "error('initializer must not run')");
        var project = await fixture.LoadAsync();
        fixture.Write("unused.lua", "\nfunction main() end"); // Added after the UI snapshot; launch must rescan disk.
        await using var runtime = new LuaRuntime(project, _ => { });
        string error = (await Assert.ThrowsAsync<InvalidDataException>(() => runtime.StartAsync())).Message;
        Assert.Contains("unused.lua:2", error); Assert.Contains("main.krl:2", error);
        Assert.DoesNotContain("initializer must not run", error);
    }

    [Fact]
    public async Task MainInAnyProjectFile_IsUniqueEntry_AndInitOrderIsPreserved()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function other() d(nil, 'not entry') end");
        fixture.Write("game/scripts/gamelogic.lua", "init(5) state.order = state.order .. 'late' ");
        fixture.Write("first.lua", "init(-5) state.order = 'early,'");
        fixture.Write("entry.lua", "function main() d(nil, state.order) end");
        fixture.Write("module.lua", "error('ordinary module is lazy')");
        var project = await fixture.LoadAsync(); var catalog = GameScripts.Load(project);
        Assert.Equal("entry.lua", catalog.Main.Path);
        Assert.Equal(new[] { "first.lua", "game/scripts/gamelogic.lua" }, catalog.Initialization.Select(f => f.Path));
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("early,late", runtime.Story.Text);
    }

    [Fact]
    public async Task AnonymousReturnedFunction_DoesNotSubstituteForMain()
    {
        using var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "return function() end");
        var project = await fixture.LoadAsync();
        Assert.Contains("Ни одного объявления", Assert.Throws<InvalidDataException>(() => GameScripts.Load(project)).Message);
    }

    [Fact]
    public async Task GraphFunctionName_IsPartOfMainValidation_AndUnusedDraftIsNotCompiled()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create(); fixture.Write("helper.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync();
        Assert.Contains("helper.knegraph:1", Assert.Throws<InvalidDataException>(() => GameScripts.Load(project)).Message);
        graph.FunctionName = "helper"; graph.Nodes.Clear(); fixture.Write("helper.knegraph", graph.Serialize());
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Добро пожаловать! Это базовая сцена.", runtime.Story.Text);
    }
}
