using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class GraphRuntimePerformanceTests
{
    [Fact]
    public async Task WorkerLogicMeasurement_IsPositiveForExecutedWorkAndCheckpointRequestsDoNotOverwriteIt()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                local total = 0
                for i = 1, 20000 do total = total + math.sqrt(i) end
                state.total = total
                d(nil, "Measured first frame")
                for i = 1, 30000 do total = total + math.sqrt(i) end
                state.total = total
                d(nil, "Measured second frame")
            end
            """);
        await using var session = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        Assert.Equal(0, session.LogicMilliseconds);
        await session.StartAsync(); Assert.Equal("Measured first frame", session.Story.Text);
        double first = session.LogicMilliseconds; Assert.True(double.IsFinite(first) && first > 0, "Actual worker execution must produce a positive finite duration.");
        var checkpoint = await session.GetCheckpointAsync(); Assert.True(checkpoint.State.GetProperty("total").GetDouble() > 0); Assert.Equal(first, session.LogicMilliseconds);
        await session.AdvanceAsync(); Assert.Equal("Measured second frame", session.Story.Text);
        double second = session.LogicMilliseconds; Assert.True(double.IsFinite(second) && second > 0);
        Assert.True((await session.GetCheckpointAsync()).State.GetProperty("total").GetDouble() > checkpoint.State.GetProperty("total").GetDouble());
        Assert.Equal(second, session.LogicMilliseconds);
    }

    [Fact]
    public async Task GameWindow_AppliesProjectGraphicsAndDebugConsoleStartsOpenCanCloseAndReopenWithoutForcingItOnEachFrame()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.score=11; d(nil,'Debug frame'); state.score=22; d(nil,'Updated frame') end");
        var graphics = new GraphicsSettings { Api = "opengl", VSync = false, MaxFps = 30, MsaaSamples = 4 };
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = graphics, DebugMode = true, Preloading = false, StatisticsOverlay = true, FpsCounter = true, FpsCorner = "bottom_left" });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("KNE performance keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
            var window = new GameWindow(project);
            try
            {
                Assert.Equal(graphics, GraphicsEnvironment.GetSettings(window)); Assert.Equal(graphics, GraphicsEnvironment.GetSettings(Find<AnimatedBackground>(window, "AnimatedBackdrop")));
                var console = Find<Border>(window, "DebugOverlay"); Assert.Equal(Visibility.Visible, console.Visibility);
                await InvokeTask(window, "StartGameAsync");
                Assert.Contains("Debug frame", Find<TextBlock>(window, "DialogueText").Text);
                string variables = Find<TextBox>(window, "VariablesOutput").Text;
                Assert.Contains(Environment.MachineName, variables); Assert.Contains(".NET ", variables); Assert.Contains("API:", variables); Assert.Contains("11", variables);
                void F3()
                {
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F3) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    window.RaiseEvent(key); Assert.True(key.Handled);
                }
                F3(); Assert.Equal(Visibility.Collapsed, console.Visibility);
                var runtime = Field<ProcessGameSession>(window, "runtime"); await runtime.AdvanceAsync(); Invoke(window, "RenderOutput");
                Assert.Equal(Visibility.Collapsed, console.Visibility);
                F3(); Assert.Equal(Visibility.Visible, console.Visibility);
                Assert.Contains("22", Find<TextBox>(window, "VariablesOutput").Text); Assert.NotEqual(variables, Find<TextBox>(window, "VariablesOutput").Text);
                Invoke(window, "CloseConsole_Click", new Button(), new RoutedEventArgs()); Assert.Equal(Visibility.Collapsed, console.Visibility);
                Invoke(window, "ShowMenu"); Invoke(window, "ShowGameplay"); Assert.Equal(Visibility.Collapsed, console.Visibility);
                F3(); Assert.Equal(Visibility.Visible, console.Visibility);
                var overlay = Field<Border>(window, "performanceOverlay"); Assert.Equal(Visibility.Visible, overlay.Visibility);
                Assert.Equal(HorizontalAlignment.Left, overlay.HorizontalAlignment); Assert.Equal(VerticalAlignment.Bottom, overlay.VerticalAlignment);
                string stats = Assert.IsType<TextBlock>(overlay.Child).Text; Assert.Contains("RAM:", stats); Assert.Contains("VRAM: н/д", stats); Assert.Contains("Draw Calls слоя:", stats); Assert.Contains("Логика:", stats);
                Invoke(window, "ShowStartupError", new InvalidOperationException("debug error"), "Проверка отладки");
                Assert.Equal(Visibility.Visible, Find<Border>(window, "StartupErrorOverlay").Visibility); Assert.Equal(Visibility.Visible, console.Visibility);
                Find<Button>(window, "ErrorConsoleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(Visibility.Collapsed, console.Visibility);
                Find<Button>(window, "ErrorConsoleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(Visibility.Visible, console.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("high", true, true, true, 8100)]
    [InlineData("high", false, true, false, 8100)]
    [InlineData("medium", true, true, true, 4860)]
    [InlineData("low", true, false, false, 2430)]
    public async Task GameWindow_UsesExecutingGraphQualityAndPostProcessingToControlAnimationsAndNativeGeometry(string quality, bool postProcessing, bool animate, bool postEffects, int vertices)
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["game/scenes/visual.knegraph"] = new() { EffectsQuality = quality, PostProcessing = postProcessing } } });
        var graph = VisualScript.Create(); graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Start).Next = "animate";
        graph.Nodes.Add(new() { Id = "animate", Kind = ScriptNodeKind.Animation, Target = "sprite", Text = "fade_in", Duration = 10, Next = "shake" });
        graph.Nodes.Add(new() { Id = "shake", Kind = ScriptNodeKind.Shake, Amount = 8, Duration = 10, Next = "flash" });
        graph.Nodes.Add(new() { Id = "flash", Kind = ScriptNodeKind.Flash, Value = "#FFFFFF", Duration = 10, Next = "dialogue" });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph); project.Engine.Theme.PatternStyle = "waves"; project.Engine.Theme.AnimationEnabled = true;
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync");
                Assert.Equal("game/scenes/visual.knegraph", Field<string>(window, "performanceScene"));
                Assert.Equal(quality, Field<ScenePerformanceSettings>(window, "activeScenePerformance").EffectsQuality);
                Assert.Equal(animate, Find<Image>(window, "CharacterImage").HasAnimatedProperties);
                Assert.Equal(postEffects, Find<Border>(window, "SceneFlash").HasAnimatedProperties);
                var camera = Assert.IsType<TransformGroup>(Find<Grid>(window, "VisualLayer").RenderTransform);
                Assert.Equal(postEffects, camera.Children.OfType<TranslateTransform>().Last().HasAnimatedProperties);
                var backdrop = Find<AnimatedBackground>(window, "AnimatedBackdrop"); backdrop.Measure(new Size(400, 240)); backdrop.Arrange(new Rect(0, 0, 400, 240));
                var frame = (GraphicsFrame)typeof(AnimatedBackground).GetMethod("CreateFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(backdrop, null)!;
                Assert.Equal(vertices, frame.Geometry!.Count);
                Assert.Contains("Логика:", SceneMetrics.Describe(project.Path, "game/scenes/visual.knegraph"));
                Assert.Equal(graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Dialogue).Text, Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task NormalMode_KeepsStatisticsHiddenAndAllowsTheOptionalConsoleToBeToggledAndClosed()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Game.DebugEnabled = true;
        await WorkspaceTabsTests.Sta(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("KNE normal keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
            var window = new GameWindow(project);
            try
            {
                var console = Find<Border>(window, "DebugOverlay"); Assert.Equal(Visibility.Collapsed, console.Visibility);
                Invoke(window, "UpdatePerformance"); Assert.Equal(Visibility.Collapsed, Field<Border>(window, "performanceOverlay").Visibility);
                void F3() => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.F3) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                F3(); Assert.Equal(Visibility.Visible, console.Visibility); F3(); Assert.Equal(Visibility.Collapsed, console.Visibility);
                F3(); Invoke(window, "CloseConsole_Click", new Button(), new RoutedEventArgs()); Assert.Equal(Visibility.Collapsed, console.Visibility);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("medium", true)] [InlineData("low", false)]
    public async Task GameWindow_UsesExecutingTraditionalKrlScenePerformanceOverrides(string quality, bool postProcessing)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'Traditional scene') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = false });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["game/scenes/main.krl"] = new() { EffectsQuality = quality, PostProcessing = postProcessing } } });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await InvokeTask(window, "StartGameAsync");
                Assert.Equal("game/scenes/main.krl", Field<string>(window, "performanceScene"));
                var applied = Field<ScenePerformanceSettings>(window, "activeScenePerformance");
                Assert.Equal(quality, applied.EffectsQuality); Assert.Equal(postProcessing, applied.PostProcessing);
                Assert.Equal("Traditional scene", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Contains("Логика:", SceneMetrics.Describe(project.Path, "game/scenes/main.krl"));
            }
            finally { window.Close(); }
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static T Field<T>(GameWindow owner, string name) => (T)typeof(GameWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Invoke(GameWindow owner, string name, params object[] args) => typeof(GameWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static Task InvokeTask(GameWindow owner, string name) => (Task)typeof(GameWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;
}
