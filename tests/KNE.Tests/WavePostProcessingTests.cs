using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;
using Xunit.Abstractions;

namespace KNE.Tests;

public sealed class WavePostProcessingTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData("directx")]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public void CompositeChain_KeepsLayerOrderAndReadsBackOnlyTheFinalResult(string api)
    {
        using var renderer = NativeGraphicsRenderer.Create(api, false);
        var graph = Negative("scene");
        var program = ShaderGraphCompiler.Compile(graph, AppContext.BaseDirectory);
        var full = new Rect(0, 0, 64, 32);
        var frame = new GraphicsFrame(64, 32,
        [
            new(new GraphicsTexture(1, 1, 1, [20, 40, 80, 255]), full),
            new(new GraphicsTexture(2, 1, 1, [0, 128, 0, 128]), new Rect(-10, 0, 48, 24)),
            new(new GraphicsTexture(3, 1, 1, [200, 20, 0, 255]), new Rect(0, 0, 15, 7))
        ]);
        using var composed = renderer.Render(frame, 0);
        using var expected = renderer.RenderChain(new(64, 32, [new(new GraphicsTexture(4, 64, 32, composed.Pixels), full)]), [program]);
        int before = renderer.Readbacks;
        using var actual = renderer.RenderCompositeChain(frame, [program], 0, default, default);
        Assert.Equal(4, actual.DrawCalls);
        Assert.Equal(before + 1, renderer.Readbacks);
        Assert.Equal(expected.Pixels, actual.Pixels);

        var clearedFrame = frame with
        {
            Sprites = frame.Sprites.Skip(1).ToArray(),
            ClearColor = new(80 / 255f, 40 / 255f, 20 / 255f, 1)
        };
        using var cleared = renderer.RenderCompositeChain(clearedFrame, [program], 0, default, default);
        Assert.Equal(3, cleared.DrawCalls);
        Assert.Equal(before + 2, renderer.Readbacks);
        Assert.Equal(expected.Pixels, cleared.Pixels);
    }

    [GpuTheory]
    [InlineData("directx", "original", "background")]
    [InlineData("directx", "original", "scene")]
    [InlineData("directx", "half", "background")]
    public async Task GameWindow_WavesWithNegativeKeepUpdatingAndReuseTheirAtlas(string api, string quality, string target)
    {
        using var fixture = new TempProject();
        const string shader = "game/shaders/negative.kneshader";
        fixture.Write(shader, Negative(target).Serialize());
        fixture.Write("game/scenes/main.krl", $"function main() d(nil, 'waves'); apply_shader('{shader}'); d(nil, 'negative') end");
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new()
        {
            Preloading = false,
            Graphics = new() { Api = api, MaxFps = 60, TextureQuality = quality }
        });
        var project = await fixture.LoadAsync();
        project.Engine.Window.Width = 1920;
        project.Engine.Window.Height = 1080;
        project.Engine.Game.MainMenu = false;
        project.Engine.Theme.Pattern = true;
        project.Engine.Theme.PatternStyle = "waves";
        project.Engine.Theme.AnimationEnabled = true;
        fixture.Write("engine/window.json", JsonSerializer.Serialize(project.Engine.Window, EngineSettings.JsonOptions));
        fixture.Write("engine/theme.json", JsonSerializer.Serialize(project.Engine.Theme, EngineSettings.JsonOptions));
        await WorkspaceTabsTests.Sta(async () =>
        {
            bool profiling = Environment.GetEnvironmentVariable("KNE_WAVE_PROFILE") == "1";
            var window = new GameWindow(project)
            {
                ShowInTaskbar = false, ShowActivated = profiling, Topmost = profiling, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = profiling ? 0 : -10000, Top = profiling ? 0 : -10000,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize
            };
            var backdrop = WorkspaceTabsTests.Find<AnimatedBackground>(window, "AnimatedBackdrop");
            var targetLayer = WorkspaceTabsTests.Find<Grid>(window, target == "scene" ? "SceneArea" : "ShaderBackgroundLayer");
            var telemetry = GraphicsEnvironment.For(window);
            var presenter = (SceneShaderPresenter)typeof(GameWindow).GetField("shaders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            int compositionFrames = 0;
            void Count(object? _, EventArgs args) => compositionFrames++;
            CompositionTarget.Rendering += Count;
            try
            {
                window.Show();
                await Until(() => backdrop.IsAnimating && backdrop.GetValue(GraphicsEnvironment.LayerProperty) is GraphicsLayer { Bitmap: not null } &&
                    WorkspaceTabsTests.Find<TextBlock>(window, "DialogueText").Text == "waves");
                var layer = Assert.IsType<GraphicsLayer>(backdrop.GetValue(GraphicsEnvironment.LayerProperty));
                var atlas = Assert.IsType<System.Windows.Media.Imaging.WriteableBitmap>(layer.Bitmap);
                var warm = await Sample();
                long prepared = telemetry.PreparationCount;
                long before = presenter.RenderCount;
                var next = WorkspaceTabsTests.Find<Button>(window, "NextButton");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => presenter.RenderCount >= before + 3);
                var timer = (System.Windows.Threading.DispatcherTimer)typeof(SceneShaderPresenter)
                    .GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(presenter)!;
                Assert.Equal(TimeSpan.FromMilliseconds(1000d / 60), timer.Interval);
                var outputImage = ((Panel)targetLayer.Parent).Children.OfType<Image>().Single(image => image.Source != null);
                var outputBitmap = Assert.IsType<System.Windows.Media.Imaging.WriteableBitmap>(outputImage.Source);
                long captures = presenter.Captures;
                var filtered = await Sample();
                Assert.True(presenter.RenderCount > before + 3);
                Assert.Equal(captures, presenter.Captures);
                Assert.Same(atlas, layer.Bitmap);
                Assert.Equal(prepared, telemetry.PreparationCount);
                Assert.Same(outputBitmap, outputImage.Source);
                output.WriteLine($"{api}/{quality}/{target}: target {targetLayer.ActualWidth:F0}×{targetLayer.ActualHeight:F0}, output {outputImage.Source.Width:F0}×{outputImage.Source.Height:F0}, atlas {atlas.PixelWidth}×{atlas.PixelHeight}, composition {warm.Composition:F1} -> {filtered.Composition:F1} fps, effect {filtered.Effect:F1} fps, WPF recaptures {presenter.Captures - captures}");
            }
            finally
            {
                CompositionTarget.Rendering -= Count;
                await window.CloseForProjectDeletionAsync();
            }

            async Task<(double Composition, double Effect)> Sample()
            {
                int initialComposition = compositionFrames;
                long initialEffects = presenter.RenderCount;
                var elapsed = Stopwatch.StartNew();
                await Task.Delay(1500);
                return ((compositionFrames - initialComposition) / elapsed.Elapsed.TotalSeconds,
                    (presenter.RenderCount - initialEffects) / elapsed.Elapsed.TotalSeconds);
            }
        });
    }

    private static VisualScript Negative(string target)
    {
        var graph = ShaderGraphCompiler.Create();
        var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert);
        invert.Id = "negative";
        graph.Nodes.Add(invert);
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"] = new(invert.Id);
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderTexture).Shader!.Target = target;
        return graph;
    }

    private static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(15);
        Assert.True(condition());
    }
}
