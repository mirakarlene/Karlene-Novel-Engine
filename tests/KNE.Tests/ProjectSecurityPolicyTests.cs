using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectSecurityPolicyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProjectPluginDisableAppliesToWorkerAndDirectRuntimeBeforeAnyPluginCode(bool worker)
    {
        using var fixture = new TempProject();
        WritePolicy(fixture, new() { AllowPlugins = false });
        fixture.Write("game/plugins/automatic.lua", "init(-10)\nstate.plugin_ran=true");
        fixture.Write("plugins/unsafe.kneplugin.json", "{not valid; must never be read}");
        fixture.Write("game/plugins/included.lua", "state.plugin_ran=true");
        fixture.Write("game/scenes/main.krl", """
            function main()
                include_lua('game/plugins/included.lua')
                include_cs('missing.cs')
                run_script('missing.lua')
                d(nil, state.plugin_ran and 'unsafe' or 'policy applied')
            end
            """);
        var project = await fixture.LoadAsync();
        await using IGameSession session = worker ? new ProcessGameSession(project, _ => { }, pluginsEnabled: true) : new LuaRuntime(project, _ => { }, interactive: true, pluginsEnabled: true);
        await session.StartAsync(); Assert.Equal("policy applied", session.Story.Text);
        Assert.False((await session.GetCheckpointAsync()).State.TryGetProperty("plugin_ran", out _));
    }

    [Theory]
    [InlineData("luanet.import_type('System.IO.File')")]
    [InlineData("require('socket')")]
    [InlineData("dofile('external.lua')")]
    [InlineData("package.loaded.luanet")]
    [InlineData("debug.getregistry()")]
    [InlineData("debug.getupvalue(kne_scene_code_result, 1)")]
    [InlineData("(kne_scene_code_result('game/scenes/main.krl')):GetType()")]
    [InlineData("assert(load(string.char(27)..'Lua', 'binary', 'b'))()")]
    [InlineData("os.getenv('PATH')")]
    public async Task RestrictedLuaBlocksClrRegistryNativeModuleAndManagedArrayEscapeRoutes(string expression)
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { FileSystem = "project" });
        fixture.Write("game/scenes/main.krl", "function main() local ok,reason=pcall(function() return " + expression + " end); state.allowed=ok; d(nil,ok and 'escape' or 'blocked') end");
        await using var session = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await session.StartAsync(); Assert.Equal("blocked", session.Story.Text);
        Assert.False((await session.GetCheckpointAsync()).State.GetProperty("allowed").GetBoolean());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ManagedCallbackExceptionsAreStringsAndCannotExposeReflection(bool worker)
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { AllowNetwork = false });
        fixture.Write("game/scenes/main.krl", """
            function main()
                local ok, problem = pcall(kne_read_file, 'missing-no-such-file-policy-probe.txt')
                state.callback_succeeded = ok
                state.problem_type = type(problem)
                state.problem_text = tostring(problem)
                state.reflection_escape = pcall(function() return problem:GetType().Assembly end)
                local source = kne_scene_code_result('game/scenes/main.krl')
                state.array_type = type(source)
                state.array_escape = pcall(function() return source:GetType().Assembly end)
                d(nil, 'safe callback')
            end
            """);
        var project = await fixture.LoadAsync();
        await using IGameSession session = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await session.StartAsync(); var state = (await session.GetCheckpointAsync()).State;
        Assert.False(state.GetProperty("callback_succeeded").GetBoolean(), state.GetRawText()); Assert.Equal("string", state.GetProperty("problem_type").GetString());
        Assert.False(state.GetProperty("reflection_escape").GetBoolean()); Assert.Equal("table", state.GetProperty("array_type").GetString());
        Assert.False(state.GetProperty("array_escape").GetBoolean()); Assert.Equal("safe callback", session.Story.Text);
    }

    [Theory]
    [InlineData(false, "full")] [InlineData(true, "project")]
    public async Task RestrictedPoliciesRejectCSharpBeforeCompilingOrExecutingIt(bool network, string files)
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { AllowNetwork = network, FileSystem = files });
        fixture.Write("game/plugins/policy.cs", "using KNE; public class PolicyPlugin : IKnePlugin { public void Execute(IPluginContext context) { context.Set(\"unsafe\", true); } }");
        fixture.Write("game/scenes/main.krl", "function main() include_cs('game/plugins/policy.cs'); d(nil,'unsafe') end");
        await using var session = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.StartAsync());
        Assert.Contains("C#", error.Message); Assert.Contains("запрещены", error.Message); Assert.NotEqual("unsafe", session.Story.Text);
    }

    [Fact]
    public async Task NetworkDenyPreventsHttpAndDownloadBeforeTheHandlerAndCreatesNoCache()
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { AllowNetwork = false }); var handler = new RecordingHandler();
        using var assets = new PluginAssets(fixture.ProjectPath, handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/private");
        var send = await Assert.ThrowsAsync<InvalidOperationException>(() => assets.SendAsync(request)); Assert.Contains("запрещены", send.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => assets.DownloadAsync(new Uri("https://example.invalid/asset.png"), ".png"));
        Assert.Equal(0, handler.Calls); Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne/plugin-assets")));
    }

    [Theory]
    [InlineData("project", false)] [InlineData("full", true)]
    public async Task LuaFileApiReadsAndWritesProjectFilesAndEnforcesTheSelectedExternalAccess(string mode, bool externalAllowed)
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { FileSystem = mode });
        string outside = Path.Combine(fixture.Root, "outside.txt"); await File.WriteAllTextAsync(outside, "unchanged");
        string outsideLiteral = JsonSerializer.Serialize(outside.Replace('\\', '/'));
        fixture.Write("game/scenes/main.krl", "function main() kne.write_file('generated/inside.txt', 'inside value'); state.inside=kne.read_file('generated/inside.txt'); local ok,problem=pcall(kne.write_file," + outsideLiteral + ",'overwritten'); state.outside_allowed=ok; state.error_type=type(problem); state.problem_text=tostring(problem); d(nil,'files checked') end");
        await using var session = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await session.StartAsync(); var state = (await session.GetCheckpointAsync()).State;
        Assert.Equal("inside value", state.GetProperty("inside").GetString()); Assert.Equal("inside value", await File.ReadAllTextAsync(Path.Combine(fixture.ProjectPath, "generated/inside.txt")));
        Assert.Equal(externalAllowed ? "overwritten" : "unchanged", await File.ReadAllTextAsync(outside));
        Assert.True(externalAllowed == state.GetProperty("outside_allowed").GetBoolean(), state.GetRawText());
        if (!externalAllowed) Assert.Equal("string", state.GetProperty("error_type").GetString());
    }

    [Fact]
    public async Task NetworkAllowUsesTheConfiguredHandlerAndProjectOnlyAssetsRejectExternalFiles()
    {
        using var fixture = new TempProject(); WritePolicy(fixture, new() { FileSystem = "project" });
        string local = fixture.Write("inside.txt", "inside"), external = Path.Combine(fixture.Root, "outside.txt"); await File.WriteAllTextAsync(external, "outside");
        var handler = new RecordingHandler(); using var assets = new PluginAssets(fixture.ProjectPath, handler);
        Assert.Equal(local, assets.Local("inside.txt", ".txt")); Assert.Throws<ArgumentException>(() => assets.Local(external, ".txt"));
        Assert.Throws<ArgumentException>(() => assets.Local("../outside.txt", ".txt"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/allowed");
        Assert.Equal("handler response", Encoding.UTF8.GetString(await assets.SendAsync(request))); Assert.Equal(1, handler.Calls);
        Assert.Equal("outside", await File.ReadAllTextAsync(external));
    }

    private static void WritePolicy(TempProject fixture, ProjectSecuritySettings policy) => fixture.Write("engine/security.json", JsonSerializer.Serialize(policy, EngineSettings.JsonOptions));
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("handler response") }); }
    }
}
