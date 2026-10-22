using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class PluginSdkExampleTests
{
    [Fact]
    public async Task ShippedExamples_RegisterInEditor_AndRunTogetherInWorker_WithTypedQuestAndLuaCounter()
    {
        using var fixture = new TempProject();
        var assembly = typeof(PluginSdkExampleTests).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("SdkExamples.", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            fixture.Write("plugins/" + name["SdkExamples.".Length..], reader.ReadToEnd());
        }
        using var editor = new PluginHost(fixture.ProjectPath, true, new object());
        editor.Load(() => new EditorPluginContext(editor.Api));
        Assert.Equal(new[] { "demo.quest", "lua_demo.counter" }, editor.Api.Blocks.Select(b => b.Id));
        Assert.Equal("Инвентарь", Assert.Single(editor.Api.tabs).Title);
        Assert.Equal(new[] { "toolbar", "context" }, editor.Api.commands.Select(c => c.Location));
        Assert.Equal("demo.neon", Assert.Single(editor.Api.Palettes).Id);
        ScriptNode Create(string id)
        {
            var block = editor.Api.Blocks.Single(b => b.Id == id);
            return BlockLibrary.Create(new(ScriptNodeKind.Plugin, block.Group, block.Title, block.Description, block.Color, block));
        }
        var graph = VisualScript.Create();
        var quest = Create("demo.quest"); var counter = Create("lua_demo.counter");
        quest.Options[0].Target = counter.Id; counter.Options[0].Target = "dialogue";
        graph.Nodes.Single(n => n.Id == "start").Next = quest.Id;
        graph.Nodes.Single(n => n.Id == "dialogue").Text = "Welcome! Hello";
        graph.Nodes.AddRange([quest, counter]); fixture.Write("game/scenes/main.krl", graph.Compile());
        var project = await fixture.LoadAsync();
        var scripts = GameScripts.Load(project);
        Assert.DoesNotContain(scripts.Functions, f => f.Path == "plugins/LuaDemo.lua");
        await using var runtime = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync();
        Assert.Equal("Добро пожаловать! Привет", runtime.Story.Text);
        Assert.Equal(new[] { "demo.quest" }, runtime.PluginScreens);
        var saved = await runtime.GetCheckpointAsync();
        Assert.Equal("Новый квест", saved.State.GetProperty("quest").GetProperty("Title").GetString());
        Assert.Equal(10, saved.State.GetProperty("quest").GetProperty("Reward").GetDouble());
        Assert.Equal(1, saved.State.GetProperty("lua_counter").GetDouble());
        Assert.Equal(1, saved.State.GetProperty("__plugin_demo").GetProperty("Version").GetInt32());
        await runtime.RestoreAsync(saved);
        Assert.Equal("Добро пожаловать! Привет", runtime.Story.Text);
        Assert.Equal(new[] { "demo.quest" }, runtime.PluginScreens);
    }
}
