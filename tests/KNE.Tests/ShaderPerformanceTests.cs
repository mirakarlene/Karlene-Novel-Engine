using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ShaderPerformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Placement_ContainerAndEveryPluginAreAllowedInBothGraphTypes(bool shaderGraph)
    {
        foreach (var kind in new[] { ScriptNodeKind.ShaderContainer, ScriptNodeKind.IncludeLua, ScriptNodeKind.IncludeCs, ScriptNodeKind.Script })
            Assert.True(ShaderLibrary.CanPlace(kind, shaderGraph));
        Assert.Equal(shaderGraph, ShaderLibrary.CanPlace(ScriptNodeKind.ShaderInvert, shaderGraph));
        Assert.Equal(!shaderGraph, ShaderLibrary.CanPlace(ScriptNodeKind.Dialogue, shaderGraph));
    }

    [Fact]
    public void Containers_RoundTripAsFlowCommandsInScenesAndDataFunctionsInShaders()
    {
        var game = VisualScript.Create();
        var invoke = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); invoke.Id = "effect"; invoke.Path = "game/shaders/effect.kneshader";
        invoke.Shader!.Enabled = false; invoke.Next = "dialogue"; game.Nodes[0].Next = invoke.Id; game.Nodes.Add(invoke);
        var reopened = VisualScript.Parse(game.Serialize()); var flow = reopened.Nodes.Single(node => node.Id == "effect");
        Assert.True(flow.IsShaderInvocation); Assert.False(ShaderLibrary.IsData(flow));
        Assert.Equal(("Далее", "dialogue"), Assert.Single(flow.Outputs()));
        Assert.Contains("apply_shader(\"game/shaders/effect.kneshader\", false)", reopened.Compile());
        var shader = ShaderGraphCompiler.Create(); shader.Nodes.Add(BlockLibrary.Create(ScriptNodeKind.ShaderContainer));
        var data = VisualScript.Parse(shader.Serialize()).Nodes.Last();
        Assert.False(data.IsShaderInvocation); Assert.True(ShaderLibrary.IsData(data));
        Assert.Equal(("Значение", (string?)null), Assert.Single(data.Outputs()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Editor_OffersContainerAndPluginsPreservesTheirPortKindsAndRequestsNavigation(bool shaderGraph)
    {
        using var fixture = new TempProject();
        string path = fixture.Write(shaderGraph ? "effect.kneshader" : "scene.knegraph",
            (shaderGraph ? ShaderGraphCompiler.Create() : VisualScript.Create()).Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            try
            {
                foreach (var kind in new[] { ScriptNodeKind.ShaderContainer, ScriptNodeKind.IncludeLua, ScriptNodeKind.IncludeCs, ScriptNodeKind.Script })
                {
                    Assert.True(editor.CanAddBlock(kind)); editor.AddBlock(kind);
                    var added = editor.Document!.Graph!.Nodes.Last();
                    Assert.Equal(kind, added.Kind);
                    Assert.Equal(shaderGraph && kind == ScriptNodeKind.ShaderContainer, ShaderLibrary.IsData(added));
                    Assert.Single(added.Outputs());
                }
                var container = editor.Document!.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderContainer);
                editor.SetSubsystemPath(container.Id, "game/shaders/child.kneshader");
                string? requested = null; editor.SubsystemRequested += (_, id) => requested = id;
                editor.SelectGraphBlocks([container.Id]);
                var properties = WorkspaceTabsTests.Find<StackPanel>(editor, "ExtendedProperties");
                properties.Children.OfType<Button>().Single(button => button.Name == "OpenShaderContainerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(container.Id, requested);
                Assert.Equal("game/shaders/child.kneshader", container.Path);
                await editor.SaveAsync();
                var reopened = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
                Assert.Equal(!shaderGraph, reopened.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderContainer).IsShaderInvocation);
                Assert.Equal(3, reopened.Graph.Nodes.Count(node => ShaderLibrary.IsPlugin(node.Kind)));
                if (shaderGraph)
                {
                    var plugin = editor.Document.Graph.Nodes.First(node => ShaderLibrary.IsPlugin(node.Kind));
                    var ports = (Dictionary<(string, int?), GraphPort>)typeof(ScriptEditor).GetField("graphPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                    ports[("texture", 0)].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ports[(plugin.Id, null)].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Null(editor.Document.Graph.Nodes.Single(node => node.Id == "texture").Next);
                    Assert.Contains("соединяются отдельно", WorkspaceTabsTests.Find<TextBlock>(editor, "EditorStatus").Text);
                }
            }
            finally { editor.Clear(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_ActivatesContainerOnlyAtItsTurnAndRestoresActivationThroughWorkerCheckpoint(bool graphScene)
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/effect.kneshader", ShaderGraphCompiler.Create().Serialize());
        ProjectInfo project;
        if (graphScene)
        {
            var graph = VisualScript.Create();
            graph.Nodes.Single(node => node.Id == "dialogue").Text = "before";
            graph.Nodes.Single(node => node.Id == "dialogue").Next = "enable";
            var enable = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); enable.Id = "enable"; enable.Path = "game/shaders/effect.kneshader"; enable.Next = "during";
            var disable = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); disable.Id = "disable"; disable.Path = enable.Path; disable.Shader!.Enabled = false; disable.Next = "after";
            graph.Nodes.AddRange([enable, new() { Id = "during", Kind = ScriptNodeKind.Dialogue, Text = "during", Next = "disable" }, disable,
                new() { Id = "after", Kind = ScriptNodeKind.Dialogue, Text = "after", Next = "end" }]);
            fixture.Write("game/scenes/main.krl", "-- visual main"); fixture.Write("game/scenes/main.knegraph", graph.Serialize());
            project = await fixture.LoadAsync(); project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        }
        else
        {
            fixture.Write("game/scenes/main.krl", """
                function main()
                    d(nil, 'before')
                    apply_shader('game/shaders/effect.kneshader')
                    d(nil, 'during')
                    apply_shader('game/shaders/effect.kneshader', false)
                    d(nil, 'after')
                end
                """);
            project = await fixture.LoadAsync();
        }
        await using var runtime = new ProcessGameSession(project, _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text); Assert.Empty(runtime.Visuals.Shaders);
        await runtime.AdvanceAsync(); Assert.Equal("during", runtime.Story.Text); Assert.Equal(["game/shaders/effect.kneshader"], runtime.Visuals.Shaders);
        var checkpoint = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync(); Assert.Equal("after", runtime.Story.Text); Assert.Empty(runtime.Visuals.Shaders);
        await runtime.RestoreAsync(checkpoint); Assert.Equal("during", runtime.Story.Text); Assert.Equal(["game/shaders/effect.kneshader"], runtime.Visuals.Shaders);
        await runtime.AdvanceAsync(); Assert.Equal("after", runtime.Story.Text); Assert.Empty(runtime.Visuals.Shaders);
    }

    [Fact]
    public async Task Runtime_UnusedBrokenShaderDoesNotLoadAndSkippedContainerDoesNotActivate()
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/broken.kneshader", "not JSON");
        fixture.Write("game/scenes/main.krl", "function main() if false then apply_shader('game/shaders/broken.kneshader') end d(nil, 'unaffected') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync(); Assert.Equal("unaffected", runtime.Story.Text); Assert.Empty(runtime.Visuals.Shaders);
    }

    [Fact]
    public async Task Runtime_ShaderPluginHooksRunOnActivationWithIncludesOnceAndScriptsEachTime()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/once.lua", "state.lua_includes = (state.lua_includes or 0) + 1; state.order = 'lua'");
        fixture.Write("game/plugins/repeat.lua", "state.runs = (state.runs or 0) + 1; state.order = state.order .. ':script'");
        fixture.Write("game/plugins/once.cs", """
            using KNE;
            public sealed class Once : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    context.Set("cs_includes", System.Convert.ToDouble(context.Get("cs_includes") ?? 0d) + 1);
                    context.Set("order", context.Get("order") + ":cs");
                }
            }
            """);
        var graph = ShaderGraphCompiler.Create();
        var lua = BlockLibrary.Create(ScriptNodeKind.IncludeLua); lua.Id = "lua"; lua.Path = "game/plugins/once.lua"; lua.Next = "cs";
        var cs = BlockLibrary.Create(ScriptNodeKind.IncludeCs); cs.Id = "cs"; cs.Path = "game/plugins/once.cs"; cs.Next = "repeat";
        var script = BlockLibrary.Create(ScriptNodeKind.Script); script.Id = "repeat"; script.Path = "game/plugins/repeat.lua";
        // Storage order differs from the connected command order.
        graph.Nodes.AddRange([script, cs, lua]);
        fixture.Write("game/shaders/effect.kneshader", graph.Serialize());
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, 'before')
                apply_shader('game/shaders/effect.kneshader')
                d(nil, 'first')
                apply_shader('game/shaders/effect.kneshader', false)
                apply_shader('game/shaders/effect.kneshader')
                d(nil, 'second')
            end
            """);
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, TimeSpan.FromSeconds(30));
        await runtime.StartAsync(); Assert.Empty(runtime.Visuals.Shaders);
        Assert.False((await runtime.GetCheckpointAsync()).State.TryGetProperty("lua_includes", out _));
        await runtime.AdvanceAsync(); Assert.Equal("first", runtime.Story.Text);
        var first = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(1, first.GetProperty("lua_includes").GetInt32()); Assert.Equal(1d, first.GetProperty("cs_includes").GetDouble()); Assert.Equal(1, first.GetProperty("runs").GetInt32());
        Assert.Equal("lua:cs:script", first.GetProperty("order").GetString());
        await runtime.AdvanceAsync(); Assert.Equal("second", runtime.Story.Text);
        var second = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(1, second.GetProperty("lua_includes").GetInt32()); Assert.Equal(1d, second.GetProperty("cs_includes").GetDouble()); Assert.Equal(2, second.GetProperty("runs").GetInt32());
        Assert.Equal("lua:cs:script:script", second.GetProperty("order").GetString());
        Assert.Equal(["game/shaders/effect.kneshader"], runtime.Visuals.Shaders);
    }

    [Fact]
    public async Task Runtime_SafeModeSkipsShaderHostPluginsAndRetainsTheEffect()
    {
        using var fixture = new TempProject(); var graph = ShaderGraphCompiler.Create();
        foreach (var kind in new[] { ScriptNodeKind.IncludeLua, ScriptNodeKind.IncludeCs, ScriptNodeKind.Script })
        {
            var plugin = BlockLibrary.Create(kind); plugin.Id = kind.ToString();
            plugin.Path = "game/plugins/missing" + (kind == ScriptNodeKind.IncludeCs ? ".cs" : ".lua"); graph.Nodes.Add(plugin);
        }
        fixture.Write("game/shaders/effect.kneshader", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "function main() apply_shader('game/shaders/effect.kneshader') d(nil, 'safe') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { }, pluginsEnabled: false);
        await runtime.StartAsync();
        Assert.Equal("safe", runtime.Story.Text); Assert.Equal(["game/shaders/effect.kneshader"], runtime.Visuals.Shaders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_NestedShaderPluginHooksExecuteOnlyWhenTheirContainerFeedsFragmentOutput(bool connected)
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/root.lua", "state.root_hook = (state.root_hook or 0) + 1");
        fixture.Write("game/plugins/nested.lua", "state.nested_hook = (state.nested_hook or 0) + 1");
        var child = ShaderGraphCompiler.Create(); var nestedHook = BlockLibrary.Create(ScriptNodeKind.Script);
        nestedHook.Id = "nestedHook"; nestedHook.Path = "game/plugins/nested.lua"; child.Nodes.Add(nestedHook);
        fixture.Write("game/shaders/child.kneshader", child.Serialize());
        var graph = ShaderGraphCompiler.Create(); var container = BlockLibrary.Create(ScriptNodeKind.ShaderContainer);
        container.Id = "container"; container.Path = "game/shaders/child.kneshader"; graph.Nodes.Add(container);
        var rootHook = BlockLibrary.Create(ScriptNodeKind.Script); rootHook.Id = "rootHook"; rootHook.Path = "game/plugins/root.lua"; graph.Nodes.Add(rootHook);
        if (connected) graph.Nodes[1].Shader!.Inputs["Color"] = new(container.Id);
        var compiled = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        Assert.Contains("game/plugins/root.lua", compiled.ActivationScript);
        Assert.Equal(connected, compiled.ActivationScript.Contains("game/plugins/nested.lua", StringComparison.Ordinal));
        fixture.Write("game/shaders/root.kneshader", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "function main() apply_shader('game/shaders/root.kneshader') d(nil, 'activated') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync(); Assert.Equal("activated", runtime.Story.Text);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(1, state.GetProperty("root_hook").GetInt32());
        Assert.Equal(connected, state.TryGetProperty("nested_hook", out var nested));
        if (connected) Assert.Equal(1, nested.GetInt32());
    }

    [Fact]
    public async Task Runtime_ActivationLimitAllowsSixteenAndReplacementButRejectsSeventeenthWithoutChangingState()
    {
        using var fixture = new TempProject();
        for (int index = 0; index < 17; index++) fixture.Write($"game/shaders/effect{index}.kneshader", ShaderGraphCompiler.Create().Serialize());
        fixture.Write("game/scenes/main.krl", """
            function main()
                for i = 0, 15 do apply_shader('game/shaders/effect' .. i .. '.kneshader') end
                d(nil, 'sixteen')
                apply_shader('GAME/SHADERS/EFFECT0.KNESHADER')
                d(nil, 'replacement')
                apply_shader('game/shaders/effect16.kneshader')
                d(nil, 'unreachable')
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("sixteen", runtime.Story.Text); Assert.Equal(16, runtime.Visuals.Shaders.Length);
        await runtime.AdvanceAsync(); Assert.Equal("replacement", runtime.Story.Text); Assert.Equal(16, runtime.Visuals.Shaders.Length);
        Assert.Equal(16, runtime.Visuals.Shaders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("GAME/SHADERS/EFFECT0.KNESHADER", runtime.Visuals.Shaders.Last());
        var before = runtime.Visuals.Shaders.ToArray();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        Assert.Contains("16", error.Message); Assert.Equal(before, runtime.Visuals.Shaders); Assert.DoesNotContain("unreachable", runtime.Story.Text);
    }

    [Theory]
    [InlineData("../outside.kneshader")]
    [InlineData("game/scenes/main.krl")]
    [InlineData("game/shaders/missing.kneshader")]
    public async Task Runtime_RejectsInvalidShaderPathsAtActivation(string path)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", $"function main() d(nil, 'before') apply_shader({VisualScript.Quote(path)}) end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text); Assert.Empty(runtime.Visuals.Shaders);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        Assert.NotEmpty(error.Message); Assert.Empty(runtime.Visuals.Shaders);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("data-target")]
    [InlineData("wrong-extension")]
    [InlineData("plugin-as-data")]
    public void Compiler_RejectsMalformedShaderHostPluginChains(string fault)
    {
        using var fixture = new TempProject(); var graph = ShaderGraphCompiler.Create();
        var plugin = BlockLibrary.Create(ScriptNodeKind.IncludeLua); plugin.Id = "plugin"; plugin.Path = "game/plugins/valid.lua";
        graph.Nodes.Add(plugin);
        switch (fault)
        {
            case "cycle": plugin.Next = plugin.Id; break;
            case "data-target": plugin.Next = "texture"; break;
            case "wrong-extension": plugin.Path = "game/plugins/wrong.cs"; break;
            case "plugin-as-data": graph.Nodes[1].Shader!.Inputs["Color"] = new("plugin"); break;
        }
        Assert.Throws<InvalidDataException>(() => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath));
    }

    [Fact]
    public void Compiler_IdentityDetectionIncludesNestedPassthroughButExcludesRealEffects()
    {
        using var fixture = new TempProject(); var child = ShaderGraphCompiler.Create();
        fixture.Write("game/shaders/child.kneshader", child.Serialize());
        var parent = ShaderGraphCompiler.Create(); var container = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); container.Id = "child"; container.Path = "game/shaders/child.kneshader";
        parent.Nodes.Add(container); parent.Nodes[1].Shader!.Inputs["Color"] = new(container.Id);
        Assert.True(ShaderGraphCompiler.Compile(child, fixture.ProjectPath).IsIdentity);
        Assert.True(ShaderGraphCompiler.Compile(parent, fixture.ProjectPath).IsIdentity);
        var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert";
        parent.Nodes.Add(invert); parent.Nodes[1].Shader!.Inputs["Color"] = new(invert.Id);
        Assert.False(ShaderGraphCompiler.Compile(parent, fixture.ProjectPath).IsIdentity);
    }

    [Fact]
    public void Compiler_OnlyTimeNodesReachableFromFragmentOutputRequireContinuousRendering()
    {
        using var fixture = new TempProject(); var graph = Effect(ScriptNodeKind.ShaderInvert);
        var time = BlockLibrary.Create(ScriptNodeKind.ShaderTime); time.Id = "clock"; graph.Nodes.Add(time);
        var staticProgram = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        Assert.False(staticProgram.Animated); Assert.False(staticProgram.IsIdentity);
        graph.Nodes[1].Shader!.Inputs["Color"] = new(time.Id);
        var animatedProgram = ShaderGraphCompiler.Compile(graph, fixture.ProjectPath);
        Assert.True(animatedProgram.Animated); Assert.False(animatedProgram.IsIdentity);
    }

    [Fact]
    public async Task Library_IdleShaderPageKeepsRainbowColoursWithoutPerCardAnimationClocks()
    {
        using var fixture = new TempProject();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var view = new BlockLibraryView(new(Path.Combine(fixture.Root, "library.json")));
            var window = new Window { Content = view, Width = 1200, Height = 720, ShowInTaskbar = false, ShowActivated = false, Left = -2000 };
            EditorTheme.Apply(window, "kne"); view.Open(_ => true); await view.LoadPreferencesAsync();
            try
            {
                window.Show(); var groups = WorkspaceTabsTests.Find<ListBox>(view, "BlockGroups");
                groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "shaders");
                window.UpdateLayout();
                var cards = WorkspaceTabsTests.Find<WrapPanel>(view, "BlockCards").Children.OfType<Button>().ToArray();
                Assert.Equal(12, cards.Length);
                foreach (var card in cards)
                {
                    var brush = Assert.IsType<LinearGradientBrush>(card.BorderBrush);
                    Assert.Equal(7, brush.GradientStops.Count);
                    Assert.False(Assert.IsType<RotateTransform>(brush.RelativeTransform).HasAnimatedProperties);
                    Assert.Equal((Color)ColorConverter.ConvertFromString("#F06AA6"), brush.GradientStops[0].Color);
                    Assert.Equal((Color)ColorConverter.ConvertFromString("#D275E5"), brush.GradientStops[^1].Color);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Presenter_IdentityAndUnusedShadersNeverCaptureOrCreateOutputSurfaces()
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/identity.kneshader", ShaderGraphCompiler.Create().Serialize());
        fixture.Write("game/shaders/broken.kneshader", "broken unused shader"); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? error = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, problem => error = problem);
            try
            {
                window.Show(); await presenter.InitializeAsync();
                Assert.Equal(0, presenter.Captures); Assert.Equal(0, presenter.RenderCount); Assert.Equal(0, presenter.SurfaceCount);
                await presenter.SetActiveAsync(["game/shaders/identity.kneshader"]);
                await Task.Delay(180);
                Assert.Null(error); Assert.Equal(0, presenter.Captures); Assert.Equal(0, presenter.RenderCount); Assert.Equal(0, presenter.SurfaceCount);
                Assert.Empty(panel.Children.OfType<Image>()); Assert.Null(target.OpacityMask);
                await presenter.SetActiveAsync([]); Assert.Equal(0, presenter.SurfaceCount);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [Fact]
    public async Task Presenter_DeactivationWhileShaderLoadsPreventsStaleActivationAndNativeWork()
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/effect.kneshader", Effect(ScriptNodeKind.ShaderInvert).Serialize());
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? error = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, problem => error = problem);
            try
            {
                window.Show(); await presenter.InitializeAsync();
                Task activation = presenter.SetActiveAsync(["game/shaders/effect.kneshader"]);
                await presenter.SetActiveAsync([]); await activation; await Task.Delay(180);
                Assert.Null(error); Assert.Equal(0, presenter.Captures); Assert.Equal(0, presenter.RenderCount);
                Assert.Equal(0, presenter.SurfaceCount); Assert.Empty(panel.Children.OfType<Image>()); Assert.Null(target.OpacityMask);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_StaticEffectRendersOnceUntilContentChangesAndDeactivationRestoresOriginal(string api)
    {
        using var fixture = new TempProject(); var graph = ShaderGraphCompiler.Create();
        var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert"; graph.Nodes.Add(invert); graph.Nodes[1].Shader!.Inputs["Color"] = new("invert");
        fixture.Write("game/shaders/invert.kneshader", graph.Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, problem => failure = problem);
            try
            {
                window.Show(); await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/invert.kneshader"]);
                var frameWindow = new RuntimeWindowSettings("test", 200, 160, "#000000");
                var story = new StoryFrame("choice", "", "same frame", ["A", "B"]);
                presenter.SetFrame(frameWindow, SceneVisuals.Empty, story);
                await Until(() => failure != null || presenter.RenderCount > 0); Assert.Null(failure);
                Assert.Equal(1, presenter.Captures); Assert.Equal(1, presenter.RenderCount); Assert.Equal(1, presenter.SurfaceCount);
                AssertPixel(Assert.Single(panel.Children.OfType<Image>()), 255, 255, 0);
                await Task.Delay(180); Assert.Equal(1, presenter.Captures); Assert.Equal(1, presenter.RenderCount);
                // Reassigning unchanged activation state must not invalidate the static snapshot.
                await presenter.SetActiveAsync(["game/shaders/invert.kneshader"]); await Task.Delay(100); Assert.Equal(1, presenter.RenderCount);
                for (int repeat = 0; repeat < 3; repeat++)
                    presenter.SetFrame(Clone(frameWindow), Clone(SceneVisuals.Empty), Clone(story));
                await Task.Delay(100); Assert.Equal(1, presenter.Captures); Assert.Equal(1, presenter.RenderCount);
                target.Background = Brushes.Lime; presenter.NotifyContentChanged();
                await Until(() => failure != null || presenter.RenderCount == 2); Assert.Null(failure);
                Assert.Equal(2, presenter.Captures); AssertPixel(Assert.Single(panel.Children.OfType<Image>()), 255, 0, 255);
                await presenter.SetActiveAsync([]);
                Assert.Empty(panel.Children.OfType<Image>()); Assert.Null(target.OpacityMask); Assert.Same(Brushes.Lime, target.Background);
                await Task.Delay(100); Assert.Equal(2, presenter.RenderCount);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_WindowMovementRefreshesScreenPositionPixelsWithoutRecapturingSource(string api)
    {
        using var fixture = new TempProject(); var graph = Effect(ScriptNodeKind.ShaderTilingOffset);
        var screen = BlockLibrary.Create(ScriptNodeKind.ShaderScreenPosition); screen.Id = "screen"; graph.Nodes.Add(screen);
        var coordinates = graph.Nodes.Single(node => node.Id == "effect");
        coordinates.Shader!.Inputs["UV"] = new(screen.Id); coordinates.Shader.Values["x"] = .001; coordinates.Shader.Values["y"] = .001;
        coordinates.Shader.Values["z"] = 2.1;
        fixture.Write("game/shaders/screen.kneshader", graph.Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Top = 200, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show(); window.UpdateLayout();
                // Keep the offscreen test window's initial red value inside 0..1 at any monitor DPI.
                double offset = .25 - target.PointToScreen(new Point(target.ActualWidth / 2, 0)).X * .001;
                coordinates.Shader.Values["z"] = offset; fixture.Write("game/shaders/screen.kneshader", graph.Serialize());
                await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/screen.kneshader"]);
                await Until(() => failure != null || presenter.RenderCount > 0); Assert.Null(failure);
                var output = Assert.Single(panel.Children.OfType<Image>()); int beforeRed = CenterPixel(output)[2];
                Assert.Equal(1, presenter.Captures); long renders = presenter.RenderCount;
                window.Left += 200;
                await Until(() => failure != null || presenter.RenderCount > renders); Assert.Null(failure);
                int movedRed = CenterPixel(output)[2];
                Assert.True(movedRed > beforeRed + 20, $"Screen-position colour must change when the window moves: {beforeRed} -> {movedRed}.");
                Assert.Equal(1, presenter.Captures);
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(output.Source);
                double center = (bitmap.PixelWidth / 2 + .5) / bitmap.PixelWidth;
                double screenX = target.PointToScreen(new Point(target.ActualWidth * center, 0)).X;
                int expected = (int)Math.Round(Math.Clamp(screenX * .001 + offset, 0, 1) * 255);
                Assert.InRange(movedRed, Math.Max(0, expected - 2), Math.Min(255, expected + 2));
                await Task.Delay(150); Assert.Equal(1, presenter.Captures); Assert.Equal(renders + 1, presenter.RenderCount);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_AnimatedShaderUpdatesPixelsOverTicksWhileReusingStaticSourceCapture(string api)
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/scanlines.kneshader", Effect(ScriptNodeKind.ShaderScanlines).Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show(); await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/scanlines.kneshader"]);
                await Until(() => failure != null || presenter.RenderCount >= 3); Assert.Null(failure);
                var output = Assert.Single(panel.Children.OfType<Image>()); byte before = CenterPixel(output)[2]; long rendered = presenter.RenderCount;
                await Until(() => failure != null || presenter.RenderCount >= rendered + 2 && CenterPixel(output)[2] != before);
                Assert.Null(failure); var pixel = CenterPixel(output);
                Assert.Equal((byte)0, pixel[0]); Assert.Equal((byte)0, pixel[1]); Assert.InRange(pixel[2], (byte)150, (byte)255);
                Assert.NotEqual(before, pixel[2]); Assert.Equal((byte)255, pixel[3]); Assert.Equal(1, presenter.Captures);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_AnimatedLockedChoiceRefreshesSnapshotsUntilChoicePresentationStops(string api)
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/ui.kneshader", Effect(ScriptNodeKind.ShaderInvert).Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var choices = new StackPanel(); var target = new Border { Background = Brushes.Black, Child = choices }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 240, Height = 180, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            var choicePresentation = new ChoicePresentation(choices, new(), button => { button.FontSize = 22; button.Foreground = Brushes.White; }, (_, _) => { });
            var story = new StoryFrame("choice", "", "", ["Locked choice"]) { ChoiceOptions = [new("Locked choice", false, "glitch")] };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show(); choicePresentation.Display(story, true, Brushes.Purple);
                var button = Assert.IsType<ChoiceOptionButton>(Assert.Single(choices.Children.Cast<UIElement>()));
                Assert.False(button.IsEnabled); Assert.True(button.IsAnimating);
                await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/ui.kneshader"]);
                await Until(() => failure != null || presenter.Captures >= 3 && presenter.RenderCount >= 3); Assert.Null(failure);
                Assert.NotNull(Assert.Single(panel.Children.OfType<Image>()).Source);
                choicePresentation.Stop(); Assert.False(choicePresentation.IsAnimating); Assert.False(button.IsAnimating);
                presenter.NotifyContentChanged(); long beforeStop = presenter.RenderCount;
                await Until(() => failure != null || presenter.RenderCount > beforeStop); Assert.Null(failure);
                await Task.Delay(100); long captures = presenter.Captures, renders = presenter.RenderCount;
                await Task.Delay(180); Assert.Equal(captures, presenter.Captures); Assert.Equal(renders, presenter.RenderCount);
            }
            finally { choicePresentation.Reset(); await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_AnimatedBrushRefreshesActualPixelsAndStopsCapturingWhenAnimationIsRemoved(string api)
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/brush.kneshader", Effect(ScriptNodeKind.ShaderInvert).Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var background = new SolidColorBrush(Colors.Red); var target = new Border { Background = background }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show();
                background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Red, Colors.Blue, TimeSpan.FromSeconds(.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/brush.kneshader"]);
                await Until(() => failure != null || presenter.Captures >= 3 && presenter.RenderCount >= 3); Assert.Null(failure);
                var output = Assert.Single(panel.Children.OfType<Image>()); var before = CenterPixel(output); long rendered = presenter.RenderCount;
                await Until(() => failure != null || presenter.RenderCount > rendered && !before.SequenceEqual(CenterPixel(output)));
                Assert.Null(failure); Assert.True(background.HasAnimatedProperties);
                var changed = CenterPixel(output); Assert.NotEqual(before, changed); Assert.Equal((byte)255, changed[1]); Assert.Equal((byte)255, changed[3]);
                background.BeginAnimation(SolidColorBrush.ColorProperty, null); Assert.False(background.HasAnimatedProperties);
                presenter.NotifyContentChanged(); rendered = presenter.RenderCount;
                await Until(() => failure != null || presenter.RenderCount > rendered); Assert.Null(failure);
                AssertPixel(output, 255, 255, 0);
                await Task.Delay(100); long captures = presenter.Captures, frames = presenter.RenderCount;
                await Task.Delay(180); Assert.Equal(captures, presenter.Captures); Assert.Equal(frames, presenter.RenderCount);
            }
            finally { background.BeginAnimation(SolidColorBrush.ColorProperty, null); await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task Presenter_CompletedHoldEndBrushClockRetainsFinalPixelsWithoutContinuousCapture(string api)
    {
        using var fixture = new TempProject(); fixture.Write("game/shaders/hover.kneshader", Effect(ScriptNodeKind.ShaderInvert).Serialize());
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var background = new SolidColorBrush(Colors.Red); var target = new Border { Background = background }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Content = panel, Width = 200, Height = 160, Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show(); await presenter.InitializeAsync(); await presenter.SetActiveAsync(["game/shaders/hover.kneshader"]);
                await Until(() => failure != null || presenter.RenderCount > 0); Assert.Null(failure);
                var output = Assert.Single(panel.Children.OfType<Image>()); AssertPixel(output, 255, 255, 0);
                long before = presenter.Captures;
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var animation = new ColorAnimation(Colors.Red, Colors.Blue, TimeSpan.FromSeconds(.15)) { FillBehavior = FillBehavior.HoldEnd };
                animation.Completed += (_, _) => completed.TrySetResult(); background.BeginAnimation(SolidColorBrush.ColorProperty, animation);
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Until(() => failure != null || CenterPixel(output).SequenceEqual(new byte[] { 0, 255, 255, 255 })); Assert.Null(failure);
                Assert.True(background.HasAnimatedProperties); Assert.Equal(Colors.Blue, background.Color); Assert.True(presenter.Captures > before);
                await Task.Delay(100); long captures = presenter.Captures, renders = presenter.RenderCount;
                await Task.Delay(180);
                Assert.True(background.HasAnimatedProperties); Assert.Equal(captures, presenter.Captures); Assert.Equal(renders, presenter.RenderCount);
                AssertPixel(output, 0, 255, 255);
            }
            finally { background.BeginAnimation(SolidColorBrush.ColorProperty, null); await presenter.DisposeAsync(); window.Close(); }
        });
    }

    [GpuTheory]
    [InlineData("opengl", 2)]
    [InlineData("opengl", 3)]
    [InlineData("vulkan", 2)]
    [InlineData("vulkan", 3)]
    public void NativeChain_MatchesSerialPixelsWithOneReadbackAndRetainsSurfacesAndPipelines(string api, int passCount)
    {
        using var fixture = new TempProject(); using var renderer = NativeGraphicsRenderer.Create(api, false);
        var programs = new[] { Effect(ScriptNodeKind.ShaderInvert), Effect(ScriptNodeKind.ShaderBrightness), Effect(ScriptNodeKind.ShaderHueShift) }
            .Take(passCount).Select(graph => ShaderGraphCompiler.Compile(graph, fixture.ProjectPath)).ToArray();
        var source = new GraphicsTexture(9100, 2, 2, [0, 0, 255, 255, 0, 255, 0, 255, 128, 0, 0, 128, 64, 32, 16, 128]);
        var original = new GraphicsFrame(2, 2, [new(source, new Rect(0, 0, 2, 2))]);
        var serial = original; byte[] expected = [];
        for (int index = 0; index < programs.Length; index++)
        {
            using var image = renderer.Render(serial, 0, programs[index]); expected = image.Pixels.ToArray();
            serial = new(2, 2, [new(new GraphicsTexture(9101 + index, 2, 2, expected), new Rect(0, 0, 2, 2))]);
        }
        Assert.Equal(255, expected[3]); Assert.Equal(128, expected[11]);
        Assert.NotEqual(expected[0], expected[8]); // Distinct top/bottom texels expose flipped intermediate UVs.
        int reads = renderer.Readbacks, surfaces = renderer.SurfaceCreations, pipelines = renderer.PipelineCreations;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            using var actual = renderer.RenderChain(original, programs);
            Assert.Equal(passCount, actual.DrawCalls);
            for (int channel = 0; channel < expected.Length; channel++)
                Assert.InRange((int)actual.Pixels[channel], Math.Max(0, expected[channel] - 2), Math.Min(255, expected[channel] + 2));
            Assert.Equal(reads + repeat + 1, renderer.Readbacks);
            Assert.Equal(surfaces, renderer.SurfaceCreations); Assert.Equal(pipelines, renderer.PipelineCreations);
        }
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public void NativeRenderer_SwitchingEffectsReusesTargetsAndOnlyNewDimensionsRecreateThem(string api)
    {
        using var fixture = new TempProject(); using var renderer = NativeGraphicsRenderer.Create(api, false);
        var first = ShaderGraphCompiler.Compile(Effect(ScriptNodeKind.ShaderInvert), fixture.ProjectPath);
        var second = ShaderGraphCompiler.Compile(Effect(ScriptNodeKind.ShaderBrightness), fixture.ProjectPath);
        var texture = new GraphicsTexture(9200, 1, 1, [0, 0, 255, 255]);
        var frame = new GraphicsFrame(17, 11, [new(texture, new Rect(0, 0, 17, 11))]);
        foreach (var program in new[] { first, second, first, second, first })
        {
            using var image = renderer.Render(frame, 0, program);
            Assert.Equal(1, renderer.SurfaceCreations);
            var pixel = image.Pixels.AsSpan((5 * 17 + 8) * 4, 4).ToArray();
            if (ReferenceEquals(program, first)) Assert.Equal(new byte[] { 255, 255, 0, 255 }, pixel);
            else
            {
                Assert.Equal((byte)0, pixel[0]); Assert.Equal((byte)0, pixel[1]);
                Assert.InRange(pixel[2], (byte)127, (byte)128); Assert.Equal((byte)255, pixel[3]);
            }
        }
        Assert.Equal(2, renderer.PipelineCreations); Assert.Equal(5, renderer.Readbacks);
        using (var resized = renderer.Render(new(3, 7, [new(texture, new Rect(0, 0, 3, 7))]), 0, first))
        {
            Assert.Equal(3, resized.Width); Assert.Equal(7, resized.Height);
            Assert.Equal(2, renderer.SurfaceCreations); Assert.Equal(3, renderer.PipelineCreations);
        }
        using var repeated = renderer.Render(new(3, 7, [new(texture, new Rect(0, 0, 3, 7))]), 0, first);
        Assert.Equal(2, renderer.SurfaceCreations); Assert.Equal(3, renderer.PipelineCreations);
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public void NativeRenderer_NewSnapshotIdentityReusesTextureAndChangedDimensionsAllocateOnlyOnce(string api)
    {
        using var fixture = new TempProject(); using var renderer = NativeGraphicsRenderer.Create(api, false);
        var shader = ShaderGraphCompiler.Compile(Effect(ScriptNodeKind.ShaderInvert), fixture.ProjectPath);
        byte[][] snapshots = [[0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255]];
        byte[][] inverted = [[255, 255, 0, 255], [255, 0, 255, 255], [0, 255, 255, 255]];
        for (int index = 0; index < snapshots.Length; index++)
        {
            var texture = new GraphicsTexture(9400 + index, 1, 1, snapshots[index]);
            using var image = renderer.Render(new(1, 1, [new(texture, new Rect(0, 0, 1, 1))]), 0, shader);
            Assert.Equal(inverted[index], image.Pixels);
            Assert.Equal(1, renderer.TextureCreations); Assert.Equal(1, renderer.SurfaceCreations); Assert.Equal(1, renderer.PipelineCreations);
        }
        foreach (long identity in new long[] { 9410, 9411 })
        {
            var wider = new GraphicsTexture(identity, 2, 1, [0, 0, 255, 255, 0, 255, 0, 255]);
            using var image = renderer.Render(new(2, 1, [new(wider, new Rect(0, 0, 2, 1))]), 0, shader);
            Assert.Equal(new byte[] { 255, 255, 0, 255, 255, 0, 255, 255 }, image.Pixels);
            Assert.Equal(2, renderer.TextureCreations); Assert.Equal(2, renderer.SurfaceCreations); Assert.Equal(2, renderer.PipelineCreations);
        }
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public void NativeRenderer_ReadbackBuffersAreReusedAfterDisposalButNeverLeasedToTwoLiveFrames(string api)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        var red = new GraphicsTexture(9500, 1, 1, [0, 0, 255, 255]);
        var green = new GraphicsTexture(9501, 1, 1, [0, 255, 0, 255]);
        var blue = new GraphicsTexture(9502, 1, 1, [255, 0, 0, 255]);
        GraphicsFrame Frame(GraphicsTexture texture, int width = 1) => new(width, 1, [new(texture, new Rect(0, 0, width, 1))]);
        for (int repeat = 0; repeat < 3; repeat++)
        {
            using var warm = renderer.Render(Frame(red), 0);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, warm.Pixels); Assert.Equal(1, renderer.ReadbackAllocations);
        }
        using var first = renderer.Render(Frame(red), 0); using var second = renderer.Render(Frame(green), 0);
        byte[] firstBuffer = first.Pixels;
        Assert.Equal(2, renderer.ReadbackAllocations); Assert.NotSame(firstBuffer, second.Pixels);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, first.Pixels); Assert.Equal(new byte[] { 0, 255, 0, 255 }, second.Pixels);
        first.Dispose();
        using var third = renderer.Render(Frame(blue), 0); Assert.Same(firstBuffer, third.Pixels); Assert.Equal(2, renderer.ReadbackAllocations);
        first.Dispose(); // Must not return the buffer now owned by the third frame a second time.
        using var fourth = renderer.Render(Frame(red), 0);
        Assert.Equal(3, renderer.ReadbackAllocations); Assert.NotSame(third.Pixels, fourth.Pixels);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, third.Pixels); Assert.Equal(new byte[] { 0, 0, 255, 255 }, fourth.Pixels);
        second.Dispose(); third.Dispose(); fourth.Dispose();
        for (int repeat = 0; repeat < 2; repeat++)
        {
            using var resized = renderer.Render(Frame(red, 2), 0);
            Assert.Equal(8, resized.Pixels.Length); Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, resized.Pixels);
            Assert.Equal(4, renderer.ReadbackAllocations);
        }
    }

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public async Task ShaderSession_SharesTheWindowWorkerAndReleasesOnlyItsOwnReference(string api)
    {
        using var fixture = new TempProject(); var invert = ShaderGraphCompiler.Compile(Effect(ScriptNodeKind.ShaderInvert), fixture.ProjectPath);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new Window(); var worker = SharedGraphicsWorker.Acquire(window); bool released = false;
            var session = new ShaderGpuSession(window); var settings = new GraphicsSettings { Api = api };
            try
            {
                Assert.Same(worker, session.Worker); Assert.True(worker.WorkerThread.IsAlive);
                var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[] { 0, 0, 255, 255 }, 4); source.Freeze();
                var result = await session.RenderAsync(source, [invert], settings, 0);
                Assert.True(result.IsFrozen); var pixel = new byte[4]; result.CopyPixels(pixel, 4, 0);
                Assert.Equal(new byte[] { 255, 255, 0, 255 }, pixel);
                await session.DisposeAsync(); Assert.True(worker.WorkerThread.IsAlive);
                // The existing graphics layer reference must remain usable after the shader closes.
                var completion = new TaskCompletionSource<(GraphicsPixels? Image, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
                var texture = new GraphicsTexture(9300, 1, 1, [0, 0, 255, 255]);
                Assert.True(worker.Enqueue(new(1, 1, [new(texture, new Rect(0, 0, 1, 1))]), settings, (image, error) => completion.TrySetResult((image, error))));
                var prepared = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                using (prepared.Image)
                {
                    Assert.Null(prepared.Error); Assert.NotNull(prepared.Image); Assert.Equal(api, prepared.Image.Backend);
                    Assert.Equal(new byte[] { 0, 0, 255, 255 }, prepared.Image.Pixels);
                }
                worker.Release(); released = true;
                Assert.True(await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5))));
            }
            finally
            {
                await session.DisposeAsync(); if (!released) worker.Release();
                window.Close(); await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5)));
            }
        });
    }

    [Theory]
    [InlineData(ProjectAuthoringMode.Code)]
    [InlineData(ProjectAuthoringMode.Visual)]
    public async Task ProjectCreation_CreatesShadersDirectoryForBothAuthoringModes(ProjectAuthoringMode mode)
    {
        using var fixture = new TempProject(); string path = await ProjectCreator.CreateProjectAsync("Shader layout " + mode, fixture.Root, mode);
        string shaders = Path.Combine(path, "game", "shaders");
        Assert.True(Directory.Exists(shaders)); Assert.Empty(Directory.GetFiles(shaders));
        Assert.Equal(shaders, ProjectShaderPaths.Directory(path));
    }

    [Fact]
    public void ProjectPaths_RouteNewRootShadersToGameShadersAndKeepExplicitProjectFolders()
    {
        using var fixture = new TempProject();
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "shaders", "effect.kneshader"), ProjectShaderPaths.NewFile(fixture.ProjectPath, "effect.kneshader", true));
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "shaders", "absolute.kneshader"), ProjectShaderPaths.NewFile(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "absolute.kneshader"), true));
        Assert.Equal(Path.Combine(fixture.ProjectPath, "custom", "effect.kneshader"), ProjectShaderPaths.NewFile(fixture.ProjectPath, "custom/effect.kneshader", true));
        Assert.Equal(Path.Combine(fixture.ProjectPath, "scene.knegraph"), ProjectShaderPaths.NewFile(fixture.ProjectPath, "scene.knegraph", false));
        Assert.Throws<ArgumentException>(() => ProjectShaderPaths.NewFile(fixture.ProjectPath, "../escape.kneshader", true));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.kneshader")));
    }

    private static void AssertPixel(Image image, byte blue, byte green, byte red)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source); var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Assert.Equal(new[] { blue, green, red, (byte)255 }, pixel);
    }

    private static byte[] CenterPixel(Image image)
    {
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source); var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }

    private static VisualScript Effect(ScriptNodeKind kind)
    {
        var graph = ShaderGraphCompiler.Create(); var effect = BlockLibrary.Create(kind); effect.Id = "effect";
        if (kind == ScriptNodeKind.ShaderBrightness) effect.Shader!.Values["amount"] = .5;
        graph.Nodes.Add(effect); graph.Nodes[1].Shader!.Inputs["Color"] = new(effect.Id);
        return graph;
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
