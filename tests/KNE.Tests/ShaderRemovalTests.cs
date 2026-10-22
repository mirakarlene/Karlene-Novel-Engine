using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ShaderRemovalTests
{
    [Theory]
    [InlineData(ScriptNodeKind.RemoveLayerShaders, false)]
    [InlineData(ScriptNodeKind.RemoveLayerShaders, true)]
    [InlineData(ScriptNodeKind.RemoveAllShaders, false)]
    [InlineData(ScriptNodeKind.RemoveAllShaders, true)]
    public void Blocks_UseShaderCategoryFlowPortsAndPersistSuppression(ScriptNodeKind kind, bool suppress)
    {
        var node = BlockLibrary.Create(kind); node.Id = "remove"; node.Next = "end";
        node.SuppressShadersUntilContainer = suppress;
        Assert.Equal("shaders", BlockLibrary.Definitions.Single(definition => definition.Kind == kind).Group);
        Assert.True(ShaderLibrary.CanPlace(kind, false)); Assert.False(ShaderLibrary.CanPlace(kind, true));
        Assert.False(ShaderLibrary.IsData(node)); Assert.Equal(("Далее", "end"), Assert.Single(node.Outputs()));
        if (kind == ScriptNodeKind.RemoveLayerShaders) Assert.Equal("background", node.Target);
        node.Target = "screen"; node.Path = "Настройки";
        var graph = VisualScript.Create(); graph.Nodes.Single(n => n.Id == "dialogue").Next = node.Id; graph.Nodes.Add(node);
        var reopened = VisualScript.Parse(graph.Serialize()); var copy = reopened.Nodes.Single(n => n.Id == node.Id);
        Assert.Equal(suppress, copy.SuppressShadersUntilContainer); Assert.Equal(node.Path, copy.Path); Assert.Equal(node.Target, copy.Target);
        string expected = kind == ScriptNodeKind.RemoveAllShaders ? $"clear_shaders({suppress.ToString().ToLowerInvariant()})"
            : $"clear_layer_shaders(\"screen\", \"Настройки\", {suppress.ToString().ToLowerInvariant()})";
        Assert.Contains(expected, reopened.Compile());
        var shader = ShaderGraphCompiler.Create(); shader.Nodes.Add(node);
        Assert.Throws<InvalidDataException>(() => shader.Serialize());
    }

    [Theory]
    [InlineData(ScriptNodeKind.RemoveLayerShaders)]
    [InlineData(ScriptNodeKind.RemoveAllShaders)]
    public async Task Editor_RemovalBlocksExposeEditableSuppressionAndLayerFieldsAndPersistTheChanges(ScriptNodeKind kind)
    {
        using var fixture = new TempProject(); string path = fixture.Write("removal.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            try
            {
                Assert.True(editor.CanAddBlock(kind)); editor.AddBlock(kind);
                var properties = WorkspaceTabsTests.Find<StackPanel>(editor, "ExtendedProperties");
                var suppress = properties.Children.OfType<CheckBox>().Single(box => box.Name == "SuppressShadersInput");
                Assert.False(suppress.IsChecked); suppress.IsChecked = true;
                suppress.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (kind == ScriptNodeKind.RemoveLayerShaders)
                {
                    var target = properties.Children.OfType<ComboBox>().Single(box => box.Name == "ShaderRemovalTargetInput");
                    Assert.Equal("background", target.SelectedValue);
                    target.SelectedValue = "screen";
                    properties.Children.OfType<TextBox>().Single(box => box.Name == "ShaderRemovalScreenInput").Text = "Мой экран";
                }
                else Assert.DoesNotContain(properties.Children.OfType<ComboBox>(), box => box.Name == "ShaderRemovalTargetInput");
                WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var node = Assert.Single(editor.Document!.Graph!.Nodes, n => n.Kind == kind);
                Assert.True(node.SuppressShadersUntilContainer);
                if (kind == ScriptNodeKind.RemoveLayerShaders) { Assert.Equal("screen", node.Target); Assert.Equal("Мой экран", node.Path); }
                await editor.SaveAsync(); var saved = (await ScriptDocument.OpenAsync(fixture.ProjectPath, path)).Graph!.Nodes.Single(n => n.Kind == kind);
                Assert.True(saved.SuppressShadersUntilContainer); Assert.Equal(node.Target, saved.Target); Assert.Equal(node.Path, saved.Path);
            }
            finally { editor.Clear(); }
        });
    }

    [Theory]
    [InlineData(ScriptNodeKind.RemoveLayerShaders)]
    [InlineData(ScriptNodeKind.RemoveAllShaders)]
    public async Task GraphWorker_ExecutesRemovalThroughTheGeneratedCommandAndNextContainerResumesItsFlow(ScriptNodeKind kind)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "background", "background");
        var graph = VisualScript.Create();
        var container = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); container.Id = "enable"; container.Path = ShaderPath("background"); container.Next = "dialogue";
        var removal = BlockLibrary.Create(kind); removal.Id = "remove"; removal.Next = "removed"; removal.SuppressShadersUntilContainer = true;
        var resume = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); resume.Id = "resume"; resume.Path = container.Path; resume.Next = "resumed";
        graph.Nodes[0].Next = container.Id; graph.Nodes.Single(n => n.Id == "dialogue").Text = "active"; graph.Nodes.Single(n => n.Id == "dialogue").Next = removal.Id;
        graph.Nodes.AddRange([container, removal, new() { Id = "removed", Kind = ScriptNodeKind.Dialogue, Text = "removed", Next = resume.Id },
            resume, new() { Id = "resumed", Kind = ScriptNodeKind.Dialogue, Text = "resumed", Next = "end" }]);
        fixture.Write("game/scenes/main.krl", "-- visual main"); fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        await using var session = new ProcessGameSession(project, _ => { });
        await session.StartAsync(); Assert.Equal("active", session.Story.Text); Assert.Equal([ShaderPath("background")], session.Visuals.Shaders);
        await session.AdvanceAsync(); Assert.Equal("removed", session.Story.Text); Assert.Empty(session.Visuals.Shaders); Assert.Single(session.Visuals.ShaderSuppressions);
        await session.AdvanceAsync(); Assert.Equal("resumed", session.Story.Text); Assert.Equal([ShaderPath("background")], session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
    }

    [Theory]
    [InlineData("background", "")]
    [InlineData("sprite", "")]
    [InlineData("ui", "")]
    [InlineData("scene", "")]
    [InlineData("screen", " sEtTiNgS ")]
    public async Task Worker_RemoveLayerDeletesEveryMatchingEffectAndPreservesOtherLayersInOrder(string target, string screen)
    {
        using var fixture = new TempProject();
        foreach (string layer in new[] { "background", "sprite", "ui", "scene", "screen" }) WriteShader(fixture, layer, layer, "Settings");
        WriteShader(fixture, "matching", target, "Settings"); WriteShader(fixture, "other-screen", "screen", "Pause");
        var paths = new[] { "background", "sprite", "ui", "scene", "screen", "matching", "other-screen" }.Select(ShaderPath).ToArray();
        fixture.Write("game/scenes/main.krl", "function main()\n" + string.Join("\n", paths.Select(path => $"apply_shader('{path}')")) +
            $"\nd(nil,'before')\nclear_layer_shaders('{target}', '{screen}', false)\nd(nil,'after')\nend");
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal("before", session.Story.Text); Assert.Equal(paths, session.Visuals.Shaders);
        var checkpoint = await session.GetCheckpointAsync();
        await session.AdvanceAsync(); Assert.Equal("after", session.Story.Text);
        var expected = paths.Where(path => path != ShaderPath(target) && path != ShaderPath("matching")).ToArray();
        Assert.Equal(expected, session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
        var transported = JsonSerializer.Deserialize<SceneVisuals>(JsonSerializer.Serialize(session.Visuals))!;
        Assert.Equal(expected, transported.Shaders); Assert.Empty(transported.ShaderSuppressions);
        await session.RestoreAsync(checkpoint); Assert.Equal(paths, session.Visuals.Shaders);
        await session.AdvanceAsync(); Assert.Equal("after", session.Story.Text); Assert.Equal(expected, session.Visuals.Shaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_ClearAllRemovesEveryEffectAndCheckboxControlsSuppressionUntilNextContainer(bool suppress)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "background", "background"); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                apply_shader('{{ShaderPath("background")}}')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'before')
                clear_shaders({{suppress.ToString().ToLowerInvariant()}})
                d(nil,'cleared')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'resumed')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal(2, session.Visuals.Shaders.Length);
        await session.AdvanceAsync(); Assert.Equal("cleared", session.Story.Text); Assert.Empty(session.Visuals.Shaders);
        Assert.Equal(suppress ? 1 : 0, session.Visuals.ShaderSuppressions.Length);
        var checkpoint = await session.GetCheckpointAsync();
        await session.AdvanceAsync(); Assert.Equal("resumed", session.Story.Text); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
        await session.RestoreAsync(checkpoint); Assert.Equal("cleared", session.Story.Text); Assert.Empty(session.Visuals.Shaders);
        Assert.Equal(suppress ? 1 : 0, session.Visuals.ShaderSuppressions.Length);
        await session.AdvanceAsync(); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_LayerSuppressionRetainsOtherEffectsButOnlyItsOwnFlowCanResumeThem(bool suppress)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "background", "background"); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                apply_shader('{{ShaderPath("background")}}')
                apply_shader('{{ShaderPath("ui")}}')
                clear_layer_shaders('background', '', {{suppress.ToString().ToLowerInvariant()}})
                state.task = kne_async({function()
                    kne_pause(1)
                    apply_shader('{{ShaderPath("background")}}')
                end})
                d(nil,'suppressed')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'resumed')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
        Assert.Equal(suppress ? 1 : 0, session.Visuals.ShaderSuppressions.Length);
        string[] markers = session.Visuals.ShaderSuppressions.ToArray();
        await session.TickAsync(1); Assert.Equal("suppressed", session.Story.Text);
        Assert.Equal(new[] { ShaderPath("ui"), ShaderPath("background") }, session.Visuals.Shaders);
        Assert.Equal(markers, session.Visuals.ShaderSuppressions);
        await session.AdvanceAsync(); Assert.Equal("resumed", session.Story.Text); Assert.Empty(session.Visuals.ShaderSuppressions);
        Assert.Equal(new[] { ShaderPath("background"), ShaderPath("ui") }, session.Visuals.Shaders);
    }

    [Theory]
    [InlineData("call_scene(child)")]
    [InlineData("goto_scene(child)")]
    public async Task Worker_CallAndGotoKeepTheSuppressedFlowIdentityUntilItsContainer(string transition)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function child()
                d(nil,'child-before')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'child-after')
            end
            function main()
                clear_shaders(true)
                {{transition}}
                d(nil,'caller')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal("child-before", session.Story.Text); Assert.Single(session.Visuals.ShaderSuppressions);
        await session.AdvanceAsync(); Assert.Equal("child-after", session.Story.Text); Assert.Empty(session.Visuals.ShaderSuppressions);
        Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
        await session.AdvanceAsync();
        if (transition.StartsWith("call", StringComparison.Ordinal)) Assert.Equal("caller", session.Story.Text);
        else Assert.True(session.Story.Completed);
    }

    [Fact]
    public async Task Worker_ConcurrentSuppressionsNeedEachOwningFlowToReachItsOwnContainer()
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                clear_shaders(true)
                state.task = kne_async({function()
                    clear_shaders(true)
                    kne_pause(1)
                    apply_shader('{{ShaderPath("ui")}}')
                end})
                d(nil,'two')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'none')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal(2, session.Visuals.ShaderSuppressions.Distinct().Count());
        var checkpoint = await session.GetCheckpointAsync();
        await session.TickAsync(1); Assert.Single(session.Visuals.ShaderSuppressions); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
        await session.AdvanceAsync(); Assert.Equal("none", session.Story.Text); Assert.Empty(session.Visuals.ShaderSuppressions);
        await session.RestoreAsync(checkpoint); Assert.Equal(2, session.Visuals.ShaderSuppressions.Distinct().Count());
        await session.TickAsync(1); Assert.Single(session.Visuals.ShaderSuppressions);
        await session.AdvanceAsync(); Assert.Empty(session.Visuals.ShaderSuppressions);
    }

    [Fact]
    public async Task Worker_ReturnFromCalledFunctionRetainsSuppressionUntilTheCallerReachesItsContainer()
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function child()
                clear_shaders(true)
                d(nil,'child')
            end
            function main()
                call_scene(child)
                d(nil,'caller')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'resumed')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal("child", session.Story.Text); string marker = Assert.Single(session.Visuals.ShaderSuppressions);
        await session.AdvanceAsync(); Assert.Equal("caller", session.Story.Text); Assert.Equal(marker, Assert.Single(session.Visuals.ShaderSuppressions));
        await session.AdvanceAsync(); Assert.Equal("resumed", session.Story.Text); Assert.Empty(session.Visuals.ShaderSuppressions); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
    }

    [Fact]
    public async Task Worker_EndedSuppressionOwnerCannotBeResumedByAnotherFlow()
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                kne_async({function() clear_shaders(true) end})
                d(nil,'hidden')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'still-hidden')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); string marker = Assert.Single(session.Visuals.ShaderSuppressions);
        Assert.False(session.Story.HasBackgroundTasks);
        await session.AdvanceAsync(); Assert.Equal("still-hidden", session.Story.Text); Assert.Equal(marker, Assert.Single(session.Visuals.ShaderSuppressions));
        Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_EmptyAndRepeatedRemovalAreIdempotentAndDoNotDuplicateSuppressionMarkers(bool suppress)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                clear_layer_shaders('background', '', {{suppress.ToString().ToLowerInvariant()}})
                clear_layer_shaders('background', '', {{suppress.ToString().ToLowerInvariant()}})
                clear_shaders({{suppress.ToString().ToLowerInvariant()}})
                clear_shaders({{suppress.ToString().ToLowerInvariant()}})
                d(nil,'empty')
                apply_shader('{{ShaderPath("ui")}}')
                d(nil,'resumed')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Equal("empty", session.Story.Text); Assert.Empty(session.Visuals.Shaders);
        Assert.Equal(suppress ? 1 : 0, session.Visuals.ShaderSuppressions.Length);
        await session.AdvanceAsync(); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
    }

    [Fact]
    public async Task Worker_NextDisabledContainerAlsoEndsSuppressionWithoutEnablingAnEffect()
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $$"""
            function main()
                clear_shaders(true)
                d(nil,'hidden')
                apply_shader('{{ShaderPath("ui")}}',false)
                d(nil,'resumed')
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await session.StartAsync(); Assert.Single(session.Visuals.ShaderSuppressions); Assert.Empty(session.Visuals.Shaders);
        await session.AdvanceAsync(); Assert.Equal("resumed", session.Story.Text); Assert.Empty(session.Visuals.ShaderSuppressions); Assert.Empty(session.Visuals.Shaders);
    }

    [Theory]
    [InlineData("clear_layer_shaders('missing')")]
    [InlineData("clear_layer_shaders('screen', '')")]
    [InlineData("clear_layer_shaders('screen', string.rep('x',129))")]
    public async Task Runtime_InvalidLayerCommandsFailAtTheirLineWithoutRemovingActiveEffects(string command)
    {
        using var fixture = new TempProject(); WriteShader(fixture, "ui", "ui");
        fixture.Write("game/scenes/main.krl", $"function main()\napply_shader('{ShaderPath("ui")}')\nd(nil,'before')\n{command}\nend");
        await using var session = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await session.StartAsync(); Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(4, report.Line);
        Assert.Equal([ShaderPath("ui")], session.Visuals.Shaders); Assert.Empty(session.Visuals.ShaderSuppressions);
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Preview_SuppressionRestoresTheOriginalLayerAndResumptionRecreatesTheVisibleEffect(string api)
    {
        using var fixture = new TempProject(); var graph = ShaderGraphCompiler.Create(); graph.Nodes[0].Shader!.Target = "background";
        var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert"; graph.Nodes.Add(invert); graph.Nodes[1].Shader!.Inputs["Color"] = new(invert.Id);
        fixture.Write(ShaderPath("invert"), graph.Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } });
        var project = await fixture.LoadAsync(); project.Engine.Theme.Pattern = false;
        await WorkspaceTabsTests.Sta(async () =>
        {
            var preview = new ScenePreview(); var window = new Window { Content = preview, Width = 420, Height = 320, Left = -2000, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                window.Show(); var visuals = SceneVisuals.Empty with { Shaders = [ShaderPath("invert")] }; var story = new StoryFrame("dialogue", "", "frame", []);
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story, animate: false, allowChoices: false);
                var presenter = Assert.IsType<SceneShaderPresenter>(typeof(ScenePreview).GetField("shaders", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview));
                var target = WorkspaceTabsTests.Find<Grid>(preview, "ShaderBackgroundLayer");
                await Until(() => presenter.RenderCount > 0);
                Assert.Equal(1, presenter.SurfaceCount); Assert.Same(Brushes.Transparent, target.OpacityMask);
                var layer = (Panel)target.Parent;
                var first = Assert.IsType<Image>(layer.Children[layer.Children.IndexOf(target) + 1]); Assert.IsAssignableFrom<BitmapSource>(first.Source);
                long count = presenter.RenderCount;
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals with { ShaderSuppressions = ["flow:1"] }, story, animate: false, allowChoices: false);
                Assert.Equal(0, presenter.SurfaceCount); Assert.Null(target.OpacityMask);
                Assert.DoesNotContain(first, ((Panel)target.Parent).Children.OfType<Image>());
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story, animate: false, allowChoices: false);
                await Until(() => presenter.RenderCount > count);
                Assert.Equal(1, presenter.SurfaceCount); Assert.Same(Brushes.Transparent, target.OpacityMask);
                var restored = Assert.IsType<Image>(layer.Children[layer.Children.IndexOf(target) + 1]); Assert.NotSame(first, restored); Assert.IsAssignableFrom<BitmapSource>(restored.Source);
            }
            finally { preview.Stop(); await preview.WaitForShaderShutdownAsync(); window.Close(); }
        });
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(15, deadline.Token);
    }

    private static string ShaderPath(string name) => "game/shaders/" + name + ".kneshader";

    private static void WriteShader(TempProject fixture, string name, string target, string screen = "")
    {
        var graph = ShaderGraphCompiler.Create(); graph.Nodes[0].Shader!.Target = target; graph.Nodes[0].Shader!.Screen = screen;
        fixture.Write(ShaderPath(name), graph.Serialize());
    }
}
