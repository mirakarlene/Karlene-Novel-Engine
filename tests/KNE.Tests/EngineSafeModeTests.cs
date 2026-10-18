using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class EngineSafeModeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutPlugins_SkipsLuaCSharpAndScriptBlocksIncludingMissingFiles_AndWorkerInheritsMode(bool worker)
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/untrusted.lua", "init(-20)\nstate.plugin_started=true\n");
        fixture.Write("game/scenes/main.krl", """
            function main()
                include_lua('game/plugins/untrusted.lua')
                include_cs('missing-plugin.cs')
                run_script('missing-script.krl')
                d(nil, state.plugin_started and 'bad' or 'no plugins')
            end
            """);
        var project = await fixture.LoadAsync();
        await using IGameSession session = worker ? new ProcessGameSession(project, _ => { }, pluginsEnabled: false) : new LuaRuntime(project, _ => { }, interactive: true, pluginsEnabled: false);
        await session.StartAsync(); Assert.Equal("no plugins", session.Story.Text);
        var checkpoint = await session.GetCheckpointAsync(); Assert.False(checkpoint.State.TryGetProperty("plugin_started", out _));
        await session.AdvanceAsync(); Assert.True(session.Story.Completed);
        await session.RestoreAsync(checkpoint); Assert.Equal("no plugins", session.Story.Text);
    }

    [Theory]
    [InlineData("require('external')")]
    [InlineData("dofile('external.lua')")]
    [InlineData("loadfile('external.lua')")]
    [InlineData("package.loadlib('external.dll','init')")]
    public async Task WithoutPlugins_BlocksAlternativeModuleLoaders_ButMainAndStateRemainUsable(string loader)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() local ok,reason=pcall(function() " + loader + " end); state.allowed=ok; d(nil,reason) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, pluginsEnabled: false);
        await runtime.StartAsync();
        Assert.Contains("отключены", runtime.Story.Text);
        Assert.False((await runtime.GetCheckpointAsync()).State.GetProperty("allowed").GetBoolean());
        Assert.Contains("отключены", runtime.ExecuteCSharpPlugin("broken.cs", "this must not compile"));
        Assert.Contains("отключены", runtime.ReadPluginForLua("missing.lua", "lua")[2]);
    }

    [Fact]
    public async Task NormalMode_StillRunsPlugins_AndCheckpointCannotCrossPluginModes()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/value.lua", "state.from_plugin='ok'");
        fixture.Write("game/scenes/main.krl", "function main() include_lua('game/plugins/value.lua'); d(nil,state.from_plugin or 'safe') end");
        var project = await fixture.LoadAsync();
        await using var normal = new LuaRuntime(project, _ => { }, interactive: true, pluginsEnabled: true);
        await normal.StartAsync(); Assert.Equal("ok", normal.Story.Text);
        var checkpoint = await normal.GetCheckpointAsync();
        await using var safe = new LuaRuntime(project, _ => { }, interactive: true, pluginsEnabled: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => safe.RestoreAsync(checkpoint));
        await using var safeNew = new LuaRuntime(project, _ => { }, interactive: true, pluginsEnabled: false);
        await safeNew.StartAsync(); Assert.Equal("safe", safeNew.Story.Text);
    }
}
