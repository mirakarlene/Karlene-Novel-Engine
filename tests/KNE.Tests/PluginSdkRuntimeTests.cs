using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class PluginSdkRuntimeTests
{
    [Fact]
    public async Task ManifestStartup_RegistersCSharpBlock_AndGeneratedGraphFollowsReturnedPortInWorker()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            using System;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.RegisterBlock(new PluginBlock("demo.branch", "Проверка", "logic", Outputs: new[] { "Первый", "Второй" }, Parameters: new[] { new PluginParameter("message", "Сообщение") }), (running, values) => {
                        running.Set("callback_count", Convert.ToDouble(running.Get("callback_count") ?? 0d) + 1);
                        running.Set("payload", values["message"]!.GetValue<string>());
                        return 2;
                    });
                }
            }
            """);
        var custom = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", "Проверка", "", "#112233",
            new("demo.branch", "Проверка", "logic", Outputs: ["Первый", "Второй"])));
        custom.Id = "plugin-block"; custom.Plugin!.Parameters["message"] = "из C#";
        custom.SetOutput(0, "first"); custom.SetOutput(1, "second");
        var graph = new VisualScript { Nodes = [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "plugin-block" }, custom,
            new() { Id = "first", Kind = ScriptNodeKind.Dialogue, Text = "неверная ветка", Next = "end" },
            new() { Id = "second", Kind = ScriptNodeKind.Dialogue, Text = "вторая ветка", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        fixture.Write("game/scenes/main.krl", graph.Compile());
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("вторая ветка", runtime.Story.Text);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(1, state.GetProperty("callback_count").GetDouble());
        Assert.Equal("из C#", state.GetProperty("payload").GetString());
        Assert.IsType<int>(runtime.WorkerProcessId);
    }

    [Fact]
    public async Task LuaBackedBlock_CallsNamedFunctionWithJsonParameters_AndUsesOneBasedOutput()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context) => context.Api.RegisterLuaBlock(
                    new PluginBlock("demo.lua", "Lua", "logic", Outputs: new[] { "Да", "Нет" }, Parameters: new[] { new PluginParameter("text", "Текст"), new PluginParameter("amount", "Количество", "slider", "0", 0, 10) }), "plugin_branch");
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function plugin_branch(parameters)
                state.received_json = parameters
                return 2
            end
            function main()
                state.output = kne_execute_plugin_block('demo.lua', '{"text":"hello","amount":2}')
                d(nil, 'output ' .. state.output)
            end
            """);
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal("output 2", runtime.Story.Text);
        var state = (await runtime.GetCheckpointAsync()).State;
        using var parameters = JsonDocument.Parse(state.GetProperty("received_json").GetString()!);
        Assert.Equal("hello", parameters.RootElement.GetProperty("text").GetString());
        Assert.Equal(2, parameters.RootElement.GetProperty("amount").GetInt32());
    }

    [Fact]
    public async Task LuaManifest_RegistersAndExecutesGraphBlock_AndHooksDialogueAndRuntimeEventsInWorker()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/extension.lua", """
            function plugin_branch(parameters)
                state.plugin_parameters = parameters
                state.lua_calls = (state.lua_calls or 0) + 1
                return 2
            end
            function translate(text, speaker) return 'Lua: ' .. text, 'Переводчик' end
            function displayed(parameters) state.text_event = parameters end
            function started(parameters) state.scene_event = parameters end
            kne.register_group([[{"Id":"lua-group","Title":"Lua расширение","Color":"#8A4499"}]])
            kne.register_block([[{"Id":"lua.branch","Title":"Lua ветка","Group":"lua-group","Outputs":["Первая","Вторая"],"Parameters":[{"Id":"message","Label":"Сообщение"}]}]], 'plugin_branch')
            kne.before_text('translate')
            kne.on('on_text_displayed', 'displayed')
            kne.on('on_scene_start', 'started')
            """);
        fixture.Write("plugins/lua.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("lua-extension", "game/plugins/extension.lua")));
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "lua-group", "Lua ветка", "", "#8A4499",
            new("lua.branch", "Lua ветка", "lua-group", Outputs: ["Первая", "Вторая"])));
        node.Id = "lua-block"; node.Plugin!.Parameters["message"] = "привет & <text>";
        node.SetOutput(0, "first"); node.SetOutput(1, "second");
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = node.Id }, node,
            new() { Id = "first", Kind = ScriptNodeKind.Dialogue, Text = "wrong", Next = "end" },
            new() { Id = "second", Kind = ScriptNodeKind.Dialogue, Text = "правильная ветка", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }] };
        fixture.Write("game/scenes/main.krl", graph.Compile());
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("Lua: правильная ветка", runtime.Story.Text); Assert.Equal("Переводчик", runtime.Story.Speaker);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(1, state.GetProperty("lua_calls").GetInt32());
        using var parameters = JsonDocument.Parse(state.GetProperty("plugin_parameters").GetString()!);
        Assert.Equal("привет & <text>", parameters.RootElement.GetProperty("message").GetString());
        using var textEvent = JsonDocument.Parse(state.GetProperty("text_event").GetString()!);
        Assert.Equal("Lua: правильная ветка", textEvent.RootElement.GetProperty("Text").GetString());
        using var sceneEvent = JsonDocument.Parse(state.GetProperty("scene_event").GetString()!);
        Assert.Equal("main", sceneEvent.RootElement.GetProperty("Scene").GetString());
    }

    [Fact]
    public async Task DialogueHook_ReplacesTextAndSpeaker_BeforeTextDisplayedEvent()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.BeforeExecute("Dialogue", value => { value.Text = "translation: " + value.Text; value.Speaker = "Translator"; });
                    context.Api.On("on_text_displayed", value => value.Context.Set("displayed", value.Data.GetProperty("Text").GetString()));
                    context.Api.On("on_scene_start", value => value.Context.Set("scene", value.Data.GetProperty("Scene").GetString()));
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", "function main() d('Original', 'hello') end");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal("translation: hello", runtime.Story.Text);
        Assert.Equal("Translator", runtime.Story.Speaker);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal("translation: hello", state.GetProperty("displayed").GetString());
        Assert.Equal("main", state.GetProperty("scene").GetString());
    }

    [Fact]
    public async Task StructuredState_UsesDetachedJsonAndTypedStructures_AndPersistsSaveLoadEventData()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            using System;
            using System.Text.Json;
            public sealed class Registration : IKnePlugin
            {
                public record Quest(string Name, double Reward);
                public void Execute(IPluginContext context)
                {
                    context.Api.RegisterStructure<Quest>("quest");
                    context.Api.RegisterBlock(new PluginBlock("demo.state", "Состояние", "logic"), (running, _) => {
                        running.SetJson("inventory", JsonSerializer.SerializeToElement(new { items = new[] { "key", "map" }, hp = 17, active = true }));
                        running.SetJson("quest", running.Api.CreateStructure("quest", "{\"Name\":\"Путь\",\"Reward\":2.5}"));
                        var detached = running.GetJson("inventory");
                        running.Set("read_hp", detached.GetProperty("hp").GetInt32());
                        return 1;
                    });
                    context.Api.On("on_save", value => value.Context.Set("plugin_save_count", Convert.ToDouble(value.Context.Get("plugin_save_count") ?? 0d) + 1));
                    context.Api.On("on_load", value => value.Context.Set("plugin_load_count", Convert.ToDouble(value.Context.Get("plugin_load_count") ?? 0d) + 1));
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                kne_execute_plugin_block('demo.state', '{}')
                d(nil, 'first')
                state.inventory.hp = 1
                d(nil, 'second')
            end
            """);
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal(17, checkpoint.State.GetProperty("inventory").GetProperty("hp").GetInt32());
        Assert.Equal(new[] { "key", "map" }, checkpoint.State.GetProperty("inventory").GetProperty("items").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(17, checkpoint.State.GetProperty("read_hp").GetInt32());
        Assert.Equal(2.5, checkpoint.State.GetProperty("quest").GetProperty("Reward").GetDouble());
        Assert.Equal(1, checkpoint.State.GetProperty("plugin_save_count").GetDouble());
        await runtime.AdvanceAsync(); Assert.Equal("second", runtime.Story.Text);
        await runtime.RestoreAsync(checkpoint);
        Assert.Equal("first", runtime.Story.Text);
        var restored = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(17, restored.GetProperty("inventory").GetProperty("hp").GetInt32());
        Assert.Equal(1, restored.GetProperty("plugin_load_count").GetDouble());
        Assert.Equal(2, restored.GetProperty("plugin_save_count").GetDouble());
    }

    [Fact]
    public async Task RegistrationContext_ExpiresBeforeLaterExecutionHook_WhileCallbackContextWorks()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            using System;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.BeforeExecute("Dialogue", value => {
                        try { context.Set("stale_context_accepted", true); }
                        catch (ObjectDisposedException) { value.Context.Set("stale_context_rejected", true); }
                        value.Context.Set("live_context", true);
                    });
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'ready') end");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.True(state.GetProperty("live_context").GetBoolean());
        Assert.True(state.GetProperty("stale_context_rejected").GetBoolean());
        Assert.False(state.TryGetProperty("stale_context_accepted", out _));
    }

    [Fact]
    public async Task PluginScreensAndNamespacedSaveData_RestoreVisibilityAndStructuredPayload()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.RegisterScreen(new PluginGameScreen("quest", _ => null!));
                    context.Api.RegisterBlock(new PluginBlock("demo.show", "Показать", "visual"), (running, _) => {
                        running.Api.ShowScreen(running, "quest");
                        running.Api.SetSaveData(running, "quest-system", new { completed = new[] { "first", "second" }, reward = 2.5 });
                        return 1;
                    });
                    context.Api.RegisterBlock(new PluginBlock("demo.hide", "Скрыть", "visual"), (running, _) => {
                        running.Api.ShowScreen(running, "quest", false);
                        running.Api.SetSaveData(running, "quest-system", new { completed = new[] { "changed" }, reward = 0 });
                        return 1;
                    });
                    context.Api.On("on_load", value => value.Context.Set("loaded_reward", value.Context.Api.GetSaveData(value.Context, "quest-system").GetProperty("reward").GetDouble()));
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                kne_execute_plugin_block('demo.show', '{}')
                d(nil, 'overlay visible')
                kne_execute_plugin_block('demo.hide', '{}')
                d(nil, 'overlay hidden')
            end
            """);
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal(new[] { "quest" }, runtime.PluginScreens);
        var checkpoint = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync();
        Assert.Equal("overlay hidden", runtime.Story.Text); Assert.Empty(runtime.PluginScreens);
        await runtime.RestoreAsync(checkpoint);
        Assert.Equal("overlay visible", runtime.Story.Text); Assert.Equal(new[] { "quest" }, runtime.PluginScreens);
        var restored = (await runtime.CapturePluginCheckpointAsync(false)).State;
        Assert.Equal(2.5, restored.GetProperty("loaded_reward").GetDouble());
        Assert.Equal(new[] { "first", "second" }, restored.GetProperty("__plugin_quest-system").GetProperty("completed").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task RecoveryCheckpoints_DoNotEmitOnSave_WhileExplicitSaveEmitsOnce()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            using System;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context) => context.Api.On("on_save", value =>
                    value.Context.Set("saved", Convert.ToDouble(value.Context.Get("saved") ?? 0d) + 1));
            }
            """);
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'first') d(nil, 'second') end");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.False((await runtime.CapturePluginCheckpointAsync(false)).State.TryGetProperty("saved", out _));
        await runtime.AdvanceAsync();
        Assert.False((await runtime.CapturePluginCheckpointAsync(false)).State.TryGetProperty("saved", out _));
        Assert.Equal(1, (await runtime.GetCheckpointAsync()).State.GetProperty("saved").GetDouble());
        Assert.Equal(1, (await runtime.CapturePluginCheckpointAsync(false)).State.GetProperty("saved").GetDouble());
        Assert.Equal("second", runtime.Story.Text);
    }

    [Fact]
    public async Task KeyEventAndInputInterception_RunBeforeNextFrame_AndReplayDeterministicallyInWorker()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.On("on_key_press", value => {
                        value.Context.Set("pressed", value.Data.GetProperty("Key").GetString());
                        value.Context.Set("input_replayed", value.Context.Api.IsReplaying);
                    });
                    context.Api.InterceptInput(value => { if (value.Key == "F2") value.Handled = true; });
                }
            }
            """);
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, 'waiting')
                if state.pressed == 'F2' then d(nil, 'key received') else d(nil, 'key missing') end
                d(nil, 'last')
            end
            """);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.True(await runtime.InterceptInputAsync(new("key", "F2")));
        Assert.Equal("waiting", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.Equal("key received", runtime.Story.Text);
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal("F2", checkpoint.State.GetProperty("pressed").GetString());
        Assert.False(checkpoint.State.GetProperty("input_replayed").GetBoolean());
        await runtime.AdvanceAsync(); Assert.Equal("last", runtime.Story.Text);
        await runtime.RestoreAsync(checkpoint);
        Assert.Equal("key received", runtime.Story.Text);
        Assert.Equal("F2", (await runtime.GetCheckpointAsync()).State.GetProperty("pressed").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task CustomBlock_InvalidReturnedOutputBecomesActionableGameError(int output)
    {
        using var fixture = new TempProject();
        Install(fixture, $$"""
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context) => context.Api.RegisterBlock(
                    new PluginBlock("demo.output", "Выход", "logic", Outputs: new[] { "Первый", "Второй" }), (_, _) => {{output}});
            }
            """);
        fixture.Write("game/scenes/main.krl", "function main() kne_execute_plugin_block('demo.output', '{}') d(nil, 'unreachable') end");
        await using var runtime = Runtime(await fixture.LoadAsync());
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("demo.output", error.Message);
        Assert.Contains("недопустимый номер выхода: " + output, error.Message);
        Assert.DoesNotContain("unreachable", runtime.Story.Text);
    }

    [Fact]
    public async Task ManifestCallbackError_PreservesCSharpSourceLineAndCallingGraphBlockIdentityInWorker()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/callback.cs", """
            using KNE;
            using System;
            public sealed class Failure : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Api.RegisterBlock(new PluginBlock("demo.failure", "Ошибка", "logic"), (running, values) =>
                    {
                        running.Set("before_error", true);
                        throw new InvalidOperationException("manifest callback boom");
                    });
                }
            }
            """);
        fixture.Write("plugins/callback.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("callback", "game/plugins/callback.cs")));
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", "Ошибка", "", "#112233", new("demo.failure", "Ошибка", "logic")));
        node.Id = "failing-plugin"; node.SetOutput(0, "dialogue");
        var graph = VisualScript.Create(); graph.Nodes[0].Next = node.Id; graph.Nodes.Add(node);
        fixture.Write("game/scenes/main.krl", "-- main is exported by main.knegraph");
        fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        fixture.Write("project.json", JsonSerializer.Serialize(project.Configuration, ProjectConfiguration.JsonOptions));
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/plugins/callback.cs", report.File);
        Assert.Equal(10, report.Line);
        Assert.Equal("failing-plugin", Assert.IsType<FrameIdentity>(report.Frame).BlockId);
        Assert.Contains("manifest callback boom", report.Message);
        Assert.DoesNotContain("Добро пожаловать!", runtime.Story.Text);
    }

    [Fact]
    public async Task TerminalPluginBlock_AllowsZeroOnlyWhenItDeclaresNoOutputs()
    {
        using var fixture = new TempProject();
        Install(fixture, """
            using KNE;
            public sealed class Registration : IKnePlugin
            {
                public void Execute(IPluginContext context) => context.Api.RegisterBlock(
                    new PluginBlock("demo.terminal", "Терминал", "logic", Outputs: new string[0]), (running, _) => { running.Set("ended", true); return 0; });
            }
            """);
        fixture.Write("game/scenes/main.krl", "function main() local result = kne_execute_plugin_block('demo.terminal', '{}'); d(nil, 'ended ' .. result) end");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        Assert.Equal("ended 0", runtime.Story.Text);
        Assert.True((await runtime.GetCheckpointAsync()).State.GetProperty("ended").GetBoolean());
    }

    [Fact]
    public async Task SafeMode_SkipsManifestWithoutReadingSource_AndReportsDisabledCustomBlock()
    {
        using var fixture = new TempProject();
        fixture.Write("plugins/disabled.kneplugin.json", "{not valid json or source}");
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'without plugins') kne_execute_plugin_block('missing.block', '{}') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true, pluginsEnabled: false);
        await runtime.StartAsync(); Assert.Equal("without plugins", runtime.Story.Text);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        Assert.Contains("Плагины отключены: missing.block", error.Message);
    }

    [Fact]
    public async Task MissingCustomBlock_ExplainsHowToRecover_AndKeepsPreviousFrame()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'last valid') kne_execute_plugin_block('removed.translation', '{}') end");
        await using var runtime = Runtime(await fixture.LoadAsync()); await runtime.StartAsync();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        Assert.Contains("removed.translation", error.Message);
        Assert.Contains("Установите его расширение", error.Message);
        Assert.Equal("last valid", runtime.Story.Text);
    }

    [Theory]
    [InlineData(2, "game/plugins/registration.cs", "версия API")]
    [InlineData(1, "../escape.cs", "проект")]
    [InlineData(1, "game/plugins/registration.txt", ".cs")]
    public void PluginHost_RejectsUnsupportedOrEscapingManifestBeforeExecutingSource(int version, string source, string message)
    {
        using var fixture = new TempProject();
        fixture.Write("plugins/example.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("example", source, version)));
        fixture.Write("game/plugins/registration.txt", "should never run");
        using var host = new PluginHost(fixture.ProjectPath, true, new object());
        var error = Assert.ThrowsAny<Exception>(() => host.Load(() => new EditorPluginContext(host.Api)));
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.Api.Blocks);
    }

    private static LuaRuntime Runtime(ProjectInfo project) => new(project, _ => { }, interactive: true, executionLimit: TimeSpan.FromSeconds(30));
    private static void Install(TempProject fixture, string source)
    {
        fixture.Write("game/plugins/registration.cs", source);
        fixture.Write("plugins/example.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("example", "game/plugins/registration.cs")));
    }
}
