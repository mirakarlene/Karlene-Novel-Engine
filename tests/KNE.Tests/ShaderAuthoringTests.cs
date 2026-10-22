using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ShaderAuthoringTests
{
    [Fact]
    public async Task Editor_ShaderDataPortsAllowFanOutRejectCyclesAndPreserveUndoClipboardAndDrafts()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "effect.kneshader");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); var owner = new Window { Content = editor }; EditorTheme.Apply(owner, "kne"); await editor.OpenAsync(fixture.ProjectPath, path);
            try
            {
                Capture(editor, "shader-editor.png", new Size(1460, 850));
                Assert.True(editor.CanAddBlock(ScriptNodeKind.ShaderFloat)); Assert.False(editor.CanAddBlock(ScriptNodeKind.Dialogue));
                Assert.False(Find<Button>(editor, "MainSceneButton").IsEnabled); Assert.False(Find<Button>(editor, "TestSceneButton").IsEnabled);
                editor.AddBlock(ScriptNodeKind.ShaderFloat); string number = editor.Document!.Graph!.Nodes.Last().Id;
                editor.AddBlock(ScriptNodeKind.ShaderCombine); string combine = editor.Document.Graph.Nodes.Last().Id;
                Assert.True(editor.ConnectGraphPorts(number, 0, combine, -1)); Assert.True(editor.ConnectGraphPorts(combine, -2, number, 0));
                var result = editor.Document.Graph.Nodes.Single(node => node.Id == combine);
                Assert.Equal(new ShaderConnection(number), result.Shader!.Inputs["R"]); Assert.Equal(new ShaderConnection(number), result.Shader.Inputs["G"]);
                Assert.False(editor.ConnectGraphPorts(combine, 0, combine, -1));
                Assert.True(editor.ConnectGraphPorts(combine, 0, "texture", -1));
                Assert.False(editor.ConnectGraphPorts("texture", 0, combine, -3));
                string? clipboard = null; editor.WriteGraphClipboard = value => clipboard = value; editor.ReadGraphClipboard = () => clipboard;
                editor.SelectGraphBlocks([number, combine]); editor.CopyGraphSelection(); editor.PasteGraphSelection();
                var pasted = editor.Document.Graph.Nodes.TakeLast(2).ToArray(); Assert.Equal(6, editor.Document.Graph.Nodes.Count);
                var copyNumber = Assert.Single(pasted, node => node.Kind == ScriptNodeKind.ShaderFloat);
                var copyCombine = Assert.Single(pasted, node => node.Kind == ScriptNodeKind.ShaderCombine);
                Assert.Equal(copyNumber.Id, copyCombine.Shader!.Inputs["R"].Node); Assert.Equal(copyNumber.Id, copyCombine.Shader.Inputs["G"].Node);
                Assert.NotEqual(number, copyNumber.Id);
                Click(Find<Button>(editor, "UndoButton")); Assert.Equal(4, editor.Document.Graph.Nodes.Count);
                Click(Find<Button>(editor, "RedoButton")); Assert.Equal(6, editor.Document.Graph.Nodes.Count);
                await editor.SaveAsync(); var saved = await ScriptDocument.OpenAsync(fixture.ProjectPath, path); Assert.Equal(6, saved.Graph!.Nodes.Count);
                Assert.True(saved.Graph.IsShader); Assert.False(saved.IsDirty);
                Assert.Throws<InvalidOperationException>(() => editor.AddBlock(ScriptNodeKind.Dialogue));
            }
            finally { editor.Clear(); owner.Close(); }
        });
    }

    [Fact]
    public async Task Editor_ShaderArgumentsValidateBeforeChangingModelAndExportPortableSources()
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "tint.kneshader");
        await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, path);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            try
            {
                editor.AddBlock(ScriptNodeKind.ShaderFloat); string id = editor.Document!.Graph!.Nodes.Last().Id;
                var panel = Find<StackPanel>(editor, "ExtendedProperties");
                var value = Assert.Single(panel.Children.OfType<TextBox>(), box => box.Name == "Shader_x");
                value.Text = "NaN"; Click(Find<Button>(editor, "ApplyPropertiesButton"));
                Assert.Empty(editor.Document.Graph.Nodes.Single(node => node.Id == id).Shader!.Values);
                Assert.Contains("число", Find<TextBlock>(editor, "EditorStatus").Text);
                value.Text = "0.25"; Click(Find<Button>(editor, "ApplyPropertiesButton"));
                Assert.Equal(.25, editor.Document.Graph.Nodes.Single(node => node.Id == id).Shader!.Values["x"]);
                Assert.Equal(Visibility.Collapsed, Find<StackPanel>(editor, "SpeakerField").Visibility);
                await editor.SaveAsync();
                string glsl = Path.Combine(fixture.ProjectPath, "effect.frag"), hlsl = Path.Combine(fixture.ProjectPath, "effect.hlsl");
                await editor.Document.ExportAsync(glsl); await editor.Document.ExportAsync(hlsl);
                Assert.StartsWith("#version 450", await File.ReadAllTextAsync(glsl)); Assert.Contains("SV_Target", await File.ReadAllTextAsync(hlsl));
                await Assert.ThrowsAsync<IOException>(() => editor.Document.ExportAsync(glsl));
            }
            finally { editor.Clear(); }
        });
    }

    [Fact]
    public async Task Library_ShaderCategoryIsBeforePluginsPagedAndUsesRainbowWithoutChangingCardSizes()
    {
        using var fixture = new TempProject();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var view = new BlockLibraryView(new(Path.Combine(fixture.Root, "library.json"))); var owner = new Window { Content = view }; EditorTheme.Apply(owner, "kne"); view.Open(_ => true); await view.LoadPreferencesAsync();
            var groups = Find<ListBox>(view, "BlockGroups");
            var names = groups.Items.Cast<BlockGroup>().Select(group => group.Id).ToArray();
            int index = Array.IndexOf(names, "shaders"); Assert.Equal("configurations", names[index - 1]); Assert.Equal("plugins", names[index + 1]);
            groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "shaders");
            var cards = Find<WrapPanel>(view, "BlockCards").Children.OfType<Button>().ToArray(); Assert.Equal(12, cards.Length);
            Assert.All(cards, card => { Assert.IsType<LinearGradientBrush>(card.BorderBrush); Assert.Equal(236, card.Width); Assert.Equal(130, card.Height); });
            Assert.Equal("1 / 5", Find<TextBlock>(view, "PageStatus").Text);
            Capture(view, "shader-library.png", new Size(1178, 652));
            Click(Find<Button>(view, "NextPageButton")); Assert.Equal("2 / 5", Find<TextBlock>(view, "PageStatus").Text);
            Assert.Equal(12, Find<WrapPanel>(view, "BlockCards").Children.Count);
            owner.Close();
        });
    }

    [Fact]
    public async Task SceneSettings_ShaderBypassIsIndependentPerCodeAndGraphSceneAndCancelLeavesDiskUntouched()
    {
        using var fixture = new TempProject(); fixture.Write("other.knegraph", VisualScript.Create().Serialize());
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new SceneParametersWindow(project);
            try
            {
                var scenes = window.SceneInput;
                scenes.SelectedValue = Path.GetRelativePath(project.Path, project.MainScenePath).Replace('\\', '/');
                Assert.False(window.HideShadersInput.IsChecked);
                window.HideShadersInput.IsChecked = true; scenes.SelectedValue = "other.knegraph"; Assert.False(window.HideShadersInput.IsChecked);
                scenes.SelectedValue = Path.GetRelativePath(project.Path, project.MainScenePath).Replace('\\', '/'); Assert.True(window.HideShadersInput.IsChecked);
                await window.SaveAsync();
                var saved = PerformanceSettingsStore.LoadScenes(project.Path); Assert.True(saved.For(scenes.SelectedValue as string).HideShaders); Assert.False(saved.For("other.knegraph").HideShaders);
                string json = File.ReadAllText(Path.Combine(project.Path, "engine", "scene-performance.json")); Assert.Contains("\"hide_shaders\": true", json);
                window.HideShadersInput.IsChecked = false; window.Close(); Assert.Equal(json, File.ReadAllText(Path.Combine(project.Path, "engine", "scene-performance.json")));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Presenter_LoadsOnlyExplicitlyActivatedRootsAndValidatesTheirContainers()
    {
        using var fixture = new TempProject();
        var child = ShaderGraphCompiler.Create(); var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert";
        child.Nodes.Add(invert); child.Nodes[1].Shader!.Inputs["Color"] = new(invert.Id);
        fixture.Write("child.kneshader", child.Serialize());
        var parent = ShaderGraphCompiler.Create(); var container = BlockLibrary.Create(ScriptNodeKind.ShaderContainer); container.Id = "child"; container.Path = "child.kneshader"; parent.Nodes.Add(container);
        parent.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"] = new("child");
        string root = fixture.Write("root.kneshader", parent.Serialize());
        var project = await fixture.LoadAsync();
        Assert.Empty(SceneShaderPresenter.LoadPrograms(project));
        var programs = SceneShaderPresenter.LoadPrograms(project, ["root.kneshader"]); Assert.Single(programs); Assert.Equal(root, programs[0].Path);
        container.Path = "root.kneshader"; fixture.Write("root.kneshader", parent.Serialize());
        var cyclic = await fixture.LoadAsync();
        Assert.Empty(SceneShaderPresenter.LoadPrograms(cyclic));
        Assert.Throws<InvalidDataException>(() => SceneShaderPresenter.LoadPrograms(cyclic, ["root.kneshader"]));
    }

    [GpuTheory]
    [InlineData("opengl")] [InlineData("vulkan")]
    public async Task Presenter_ShadesRealWpfTargetPreservesInputAndRestoresMaskWhenSceneDisablesShaders(string api)
    {
        using var fixture = new TempProject();
        var graph = ShaderGraphCompiler.Create(); var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert"; graph.Nodes.Add(invert);
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"] = new("invert");
        fixture.Write("effect.kneshader", graph.Serialize()); await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } });
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["hidden.krl"] = new() { HideShaders = true } } });
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var target = new Border { Background = Brushes.Red }; var panel = new Grid(); panel.Children.Add(target);
            var window = new Window { Width = 200, Height = 160, Content = panel, ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -2000 };
            Exception? failure = null; var presenter = new SceneShaderPresenter(project, panel, _ => target, error => failure = error);
            try
            {
                window.Show(); await presenter.InitializeAsync();
                Assert.Equal(0, presenter.SurfaceCount); Assert.Empty(panel.Children.OfType<Image>());
                await presenter.SetActiveAsync(["effect.kneshader"]);
                await Until(() => failure != null || panel.Children.OfType<Image>().Any(image => image.Source != null));
                Assert.Null(failure); Assert.Equal(1, presenter.SurfaceCount); Assert.True(target.IsHitTestVisible);
                Assert.Same(target, panel.InputHitTest(new Point(panel.ActualWidth / 2, panel.ActualHeight / 2)));
                Assert.Same(Brushes.Transparent, target.OpacityMask);
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(Assert.Single(panel.Children.OfType<Image>()).Source);
                var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Assert.InRange(pixel[0], 250, 255); Assert.InRange(pixel[1], 250, 255); Assert.InRange(pixel[2], 0, 5);
                presenter.SetScene("hidden.krl"); Assert.True(presenter.Disabled); Assert.Equal(0, presenter.SurfaceCount); Assert.Null(target.OpacityMask);
                Assert.Empty(panel.Children.OfType<Image>()); Assert.Same(Brushes.Red, target.Background);
                presenter.SetScene("game/scenes/main.krl"); Assert.False(presenter.Disabled);
                await Until(() => failure != null || panel.Children.OfType<Image>().Any(image => image.Source != null)); Assert.Null(failure);
            }
            finally { await presenter.DisposeAsync(); window.Close(); }
            Assert.Null(target.OpacityMask);
        });
    }

    [GpuTheory]
    [InlineData("opengl")] [InlineData("vulkan")]
    public async Task PreviewWindow_ExposedPaletteChangesGpuPixelsWithoutRewritingTheSource(string api)
    {
        using var fixture = new TempProject(); var graph = ShaderGraphCompiler.Create();
        var color = BlockLibrary.Create(ScriptNodeKind.ShaderColor); color.Id = "color"; color.Shader!.Name = "Палитра"; graph.Nodes.Add(color);
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.ShaderOutput).Shader!.Inputs["Color"] = new("color");
        string path = fixture.Write("palette.kneshader", graph.Serialize()), saved = File.ReadAllText(path);
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { Api = api } }); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new ShaderPreviewWindow(project, graph, path) { Left = -2000, ShowInTaskbar = false, ShowActivated = false };
            EditorTheme.Apply(window, "kne");
            try
            {
                window.Show();
                var image = (Image)typeof(ShaderPreviewWindow).GetField("image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var inputs = (StackPanel)typeof(ShaderPreviewWindow).GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                await Until(() => image.Source != null); var before = image.Source;
                var sliders = inputs.Children.OfType<Slider>().ToArray(); Assert.Equal(4, sliders.Length);
                sliders[0].Value = 0;
                await Until(() => image.Source != null && !ReferenceEquals(before, image.Source));
                var bitmap = Assert.IsAssignableFrom<BitmapSource>(image.Source); var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(100, 100, 1, 1), pixel, 4, 0);
                Assert.Equal((byte)0, pixel[2]); Assert.InRange(pixel[0], 250, 255); Assert.InRange(pixel[1], 125, 130); Assert.Equal((byte)255, pixel[3]);
                Assert.Equal(saved, File.ReadAllText(path)); Capture((FrameworkElement)window.Content, "shader-preview.png", new Size(940, 600));
            }
            finally { await window.CloseForProjectDeletionAsync(); }
        });
    }

    [Fact]
    public async Task Analyzer_ReportsInvalidShaderFileWithoutTreatingItAsAnotherMainFunction()
    {
        using var fixture = new TempProject(); fixture.Write("effect.kneshader", ShaderGraphCompiler.Create().Serialize());
        var valid = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath); Assert.DoesNotContain(valid.Findings, item => item.Code == "KNE501");
        var invalid = ShaderGraphCompiler.Create(); invalid.Nodes.RemoveAll(node => node.Kind == ScriptNodeKind.ShaderOutput); fixture.Write("effect.kneshader", invalid.Serialize());
        var report = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath); Assert.Contains(report.Findings, item => item.Code == "KNE501" && item.File == "effect.kneshader" && item.Message.Contains("Fragment Output"));
    }

    private static void Capture(FrameworkElement view, string file, Size size)
    {
        if (Environment.GetEnvironmentVariable("KNE_SHADER_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }
}
