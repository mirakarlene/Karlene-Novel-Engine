using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class GameplayUiTests
{
    [Fact]
    public async Task Configuration_ReplacesOnlyItsTarget_AndPriorFrameSnapshotsStayImmutable()
    {
        using var fixture = new TempProject();
        var original = GameplayUiStyle.ForTarget("dialogue") with { X = 42, Width = 700, FontFamily = "Consolas", FontSize = 27 };
        var updated = original with { FontSize = 35, TextColor = "#FF00FF" };
        var button = GameplayUiStyle.ForTarget("save") with { Width = 130, Opacity = .6 };
        fixture.Write("game/scenes/main.krl", "function main()\n" + Command("dialogue", original) + Command("save", button) +
            "d(nil,'first')\n" + Command("dialogue", updated) + "d(nil,'second')\nend");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var before = runtime.Visuals;
        Assert.Equal(2, before.UiVersion);
        Assert.Equal(original, before.UiConfigurations.Single(item => item.Target == "dialogue").Style);
        await runtime.AdvanceAsync();
        Assert.Equal("second", runtime.Story.Text);
        Assert.Equal(3, runtime.Visuals.UiVersion);
        Assert.Equal(2, runtime.Visuals.UiConfigurations.Length);
        Assert.Equal(updated, runtime.Visuals.UiConfigurations.Single(item => item.Target == "dialogue").Style);
        Assert.Equal(button, runtime.Visuals.UiConfigurations.Single(item => item.Target == "save").Style);
        Assert.Equal(original, before.UiConfigurations.Single(item => item.Target == "dialogue").Style);
        var transported = JsonSerializer.Deserialize<SceneVisuals>(JsonSerializer.Serialize(runtime.Visuals))!;
        Assert.Equal(runtime.Visuals.UiVersion, transported.UiVersion);
        Assert.Equal(runtime.Visuals.UiConfigurations, transported.UiConfigurations);
    }

    [Theory]
    [InlineData("dialogue", "null")]
    [InlineData("unknown", "{}")]
    [InlineData("dialogue", "{\"width\":0}")]
    [InlineData("dialogue", "{\"height\":16385}")]
    [InlineData("dialogue", "{\"x\":100001}")]
    [InlineData("dialogue", "{\"y\":-100001}")]
    [InlineData("dialogue", "{\"font_size\":0}")]
    [InlineData("dialogue", "{\"font_size\":513}")]
    [InlineData("dialogue", "{\"font_family\":\"https://example/font\"}")]
    [InlineData("dialogue", "{\"font_family\":null}")]
    [InlineData("dialogue", "{\"font_weight\":\"HeavyUnknown\"}")]
    [InlineData("dialogue", "{\"opacity\":-0.01}")]
    [InlineData("dialogue", "{\"opacity\":1.01}")]
    [InlineData("dialogue", "{\"corner_radius\":-1}")]
    [InlineData("dialogue", "{\"padding_left\":-1}")]
    [InlineData("dialogue", "{\"padding_top\":4097}")]
    [InlineData("dialogue", "{\"text_color\":\"not-a-color\"}")]
    [InlineData("dialogue", "{\"background_color\":null}")]
    [InlineData("dialogue", "{\"horizontal_alignment\":\"diagonal\"}")]
    [InlineData("dialogue", "{\"vertical_alignment\":\"diagonal\"}")]
    [InlineData("dialogue", "{\"text_alignment\":\"diagonal\"}")]
    [InlineData("dialogue", "{\"texture\":\"../escape.png\"}")]
    [InlineData("dialogue", "{\"texture\":\"missing.png\"}")]
    [InlineData("dialogue", "{\"texture\":\"game/scenes/main.krl\"}")]
    [InlineData("dialogue", "{\"unknown_option\":1}")]
    [InlineData("dialogue", "{\"opacity\":1e999}")]
    public async Task Configuration_InvalidValueReportsOriginalFileAndLine_WithoutPartiallyChangingUi(string target, string json)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n configure_ui('" + target + "',[==[" + json + "]==])\n d(nil,'unreachable')\nend");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(exception));
        Assert.Equal("game/scenes/main.krl", report.File);
        Assert.Equal(2, report.Line);
        Assert.Contains("configure_ui", report.Message);
        Assert.Empty(runtime.Visuals.UiConfigurations);
        Assert.Equal(0, runtime.Visuals.UiVersion);
    }

    [Fact]
    public void Configuration_ValidBoundaryValuesAndEveryButtonTarget_RetainTypedFields()
    {
        foreach (string target in new[] { "dialogue", "choice", "next", "save", "load", "auto_save", "pause" })
        {
            var initial = GameplayUiStyle.ForTarget(target); initial.Validate();
            var minimum = initial with
            {
                X = -100000, Y = -100000, Width = 1, Height = 1, FontSize = 1, Opacity = 0,
                CornerRadius = 0, PaddingLeft = 0, PaddingTop = 0, PaddingRight = 0, PaddingBottom = 0,
                HorizontalAlignment = "stretch", VerticalAlignment = "stretch", TextAlignment = "justify", FontWeight = "Thin"
            };
            minimum.Validate();
            var maximum = minimum with
            {
                X = 100000, Y = 100000, Width = 16384, Height = 16384, FontSize = 512, Opacity = 1,
                CornerRadius = 4096, PaddingLeft = 4096, PaddingTop = 4096, PaddingRight = 4096, PaddingBottom = 4096, FontWeight = "Black"
            };
            maximum.Validate();
            Assert.Equal(maximum, JsonSerializer.Deserialize<GameplayUiStyle>(JsonSerializer.Serialize(maximum, EngineSettings.JsonOptions), GameplayUiStyle.JsonOptions));
        }
        Assert.Throws<ArgumentException>(() => (new GameplayUiStyle { Width = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (new GameplayUiStyle { Opacity = double.PositiveInfinity }).Validate());
        Assert.Throws<ArgumentException>(() => (new GameplayUiStyle { PaddingBottom = -double.Epsilon }).Validate());
    }

    [Fact]
    public void Configuration_InvalidColorsUseArgumentErrorsForStructuralValidation()
    {
        foreach (var style in new[] { new GameplayUiStyle { TextColor = "#nothex" }, new GameplayUiStyle { BackgroundColor = "unknown-color" } })
        {
            var error = Assert.Throws<ArgumentException>(() => style.Validate());
            Assert.Contains("Некорректный цвет UI", error.Message);
            Assert.NotNull(error.InnerException);
        }
    }

    [Fact]
    public async Task Configuration_WorkerCheckpointAndSceneBackRestoreExactlyTheVisibleConfiguration()
    {
        using var fixture = new TempProject();
        var first = GameplayUiStyle.ForTarget("dialogue") with { FontSize = 30, Opacity = .65 };
        var second = first with { FontSize = 44, Opacity = .9 };
        string source = "function main()\n" + Command("dialogue", first) + "d(nil,'first')\n" + Command("dialogue", second) + "d(nil,'second')\nend";
        fixture.Write("game/scenes/main.krl", source);
        var project = await fixture.LoadAsync();
        await using var worker = new ProcessGameSession(project, _ => { });
        await worker.StartAsync();
        Assert.Equal(first, Assert.Single(worker.Visuals.UiConfigurations).Style);
        var checkpoint = await worker.GetCheckpointAsync();
        await worker.AdvanceAsync(); Assert.Equal(second, Assert.Single(worker.Visuals.UiConfigurations).Style);
        await worker.RestoreAsync(checkpoint);
        Assert.Equal("first", worker.Story.Text);
        Assert.Equal(first, Assert.Single(worker.Visuals.UiConfigurations).Style);
        await using var scene = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await scene.RestartAsync(); await scene.AdvanceAsync();
        Assert.Equal(second, Assert.Single(scene.Visuals.UiConfigurations).Style);
        await scene.BackAsync();
        Assert.Equal(first, Assert.Single(scene.Visuals.UiConfigurations).Style);
        Assert.Equal("first", scene.Story.Text);
        await scene.RestartAsync(); Assert.Equal(first, Assert.Single(scene.Visuals.UiConfigurations).Style);
    }

    [Fact]
    public async Task Configuration_GameAndPreviewRenderIdenticalGeometryFontsTextureAndButtons_KeepingActionsAndDefaultRestore()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            WriteTexture(fixture.ProjectPath);
            var dialogue = GameplayUiStyle.ForTarget("dialogue") with
            {
                X = 32, Y = 42, Width = 800, Height = 190, FontFamily = "Consolas", FontSize = 25, FontWeight = "Bold",
                TextColor = "#FF20C080", BackgroundColor = "#FF401050", Opacity = .73, CornerRadius = 23,
                Texture = "game/ui/dialogue.png", PaddingLeft = 17, PaddingTop = 13, PaddingRight = 11, PaddingBottom = 7,
                HorizontalAlignment = "left", VerticalAlignment = "top", TextAlignment = "right"
            };
            var choice = GameplayUiStyle.ForTarget("choice") with { X = 70, Y = 280, Width = 310, Height = 50, HorizontalAlignment = "left", VerticalAlignment = "top", FontWeight = "SemiBold", FontSize = 20, CornerRadius = 15, TextAlignment = "right" };
            var save = GameplayUiStyle.ForTarget("save") with { X = 600, Y = 100, Width = 125, Height = 48, HorizontalAlignment = "left", VerticalAlignment = "top", CornerRadius = 12 };
            fixture.Write("game/scenes/main.krl", "function main()\n" + Command("dialogue", dialogue) + Command("choice", choice) + Command("save", save) +
                "local answer=choice('Choose',{'Left','Right'})\n d(nil,'chosen:'..answer)\nend");
            var project = await fixture.LoadAsync();
            await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
            await runtime.StartAsync();
            var game = new GameWindow(project); var preview = new ScenePreview();
            try
            {
                var old = (IGameSession)Field(game, "runtime")!; await old.DisposeAsync();
                SetField(game, "runtime", runtime); SetField(game, "ready", true);
                Invoke(game, "ShowGameplay"); Invoke(game, "RenderOutput");
                preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals, runtime.Story, animate: false, allowChoices: true);
                var gameScene = Find<Grid>(game, "SceneArea"); var previewScene = Find<Grid>(preview, "SceneArea");
                Arrange(gameScene); Arrange(previewScene);
                foreach (FrameworkElement owner in new FrameworkElement[] { game, preview })
                {
                    var panel = Find<Border>(owner, "DialoguePanel");
                    Assert.Equal(800, panel.Width); Assert.Equal(190, panel.Height);
                    Assert.True(double.IsPositiveInfinity(panel.MaxHeight));
                    Assert.Equal(new Thickness(32, 42, 0, 0), panel.Margin); Assert.Equal(.73, panel.Opacity);
                    Assert.Equal(new CornerRadius(23), panel.CornerRadius); Assert.Equal(new Thickness(17, 13, 11, 7), panel.Padding);
                    Assert.IsType<ImageBrush>(panel.Background);
                    var text = Find<TextBlock>(owner, "DialogueText");
                    Assert.Equal("Consolas", text.FontFamily.Source); Assert.Equal(25, text.FontSize); Assert.Equal(FontWeights.Bold, text.FontWeight);
                    Assert.Equal(TextAlignment.Right, text.TextAlignment); Assert.Equal(Color.FromRgb(32, 192, 128), Assert.IsType<SolidColorBrush>(text.Foreground).Color);
                    var choices = Find<StackPanel>(owner, "ChoicePanel");
                    Assert.IsType<Grid>(choices.Parent); Assert.Equal(new Thickness(70, 280, 0, 0), choices.Margin);
                    var first = Assert.IsType<Button>(choices.Children[0]); first.ApplyTemplate();
                    Assert.Equal(310, first.Width); Assert.Equal(50, first.Height); Assert.Equal(FontWeights.SemiBold, first.FontWeight);
                    Assert.Equal(new CornerRadius(15), ((Border)first.Template.FindName("ConfiguredButtonBorder", first)).CornerRadius);
                    Assert.Equal(TextAlignment.Right, VisualText(first)!.TextAlignment);
                }
                Assert.Equal(Find<Border>(game, "DialoguePanel").TranslatePoint(new Point(), gameScene), Find<Border>(preview, "DialoguePanel").TranslatePoint(new Point(), previewScene));
                var gameSave = Find<Button>(game, "SaveButton");
                Assert.Same(gameScene, gameSave.Parent); Assert.Equal(new Thickness(600, 100, 0, 0), gameSave.Margin);
                Assert.True(gameSave.IsEnabled);
                var previewSave = previewScene.Children.OfType<Button>().Single(button => Equals(button.Content, "Сохранить"));
                Assert.Equal(gameSave.Width, previewSave.Width); Assert.Equal(gameSave.Margin, previewSave.Margin); Assert.False(previewSave.IsHitTestVisible);
                int selected = 0; preview.SceneChoiceRequested += (_, option) => selected = option;
                var previewChoices = Find<StackPanel>(preview, "ChoicePanel"); var existing = Assert.IsType<Button>(previewChoices.Children[1]);
                var existingTemplate = existing.Template;
                existing.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, selected);
                preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals with
                {
                    Title = "tick", UiConfigurations = runtime.Visuals.UiConfigurations.Select(item => item with { Style = item.Style with { } }).ToArray()
                }, runtime.Story, false, true);
                Assert.Same(existing, previewChoices.Children[1]);
                Assert.Same(existingTemplate, existing.Template);
                var actualChoice = Assert.IsType<Button>(Find<StackPanel>(game, "ChoicePanel").Children[1]);
                actualChoice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => runtime.Story.Text == "chosen:2" && Field(game, "execution") is null);
                Assert.Equal("chosen:2", Find<TextBlock>(game, "DialogueText").Text);
                Invoke(game, "ApplyGameplayUi", SceneVisuals.Empty);
                Assert.Same(Find<StackPanel>(game, "GameControls"), gameSave.Parent);
                Assert.NotSame(gameScene, Find<StackPanel>(game, "ChoicePanel").Parent);
                Assert.Equal(project.Engine.Theme.Dialogue.Height, Find<Border>(game, "DialoguePanel").Height);
                Assert.Equal(project.Engine.Theme.Dialogue.FontSize, Find<TextBlock>(game, "DialogueText").FontSize);
                preview.DisplayScene(project, runtime.WindowSettings, SceneVisuals.Empty, runtime.Story, false, false);
                Assert.Equal(Visibility.Collapsed, previewSave.Visibility);
                Assert.NotSame(previewScene, previewChoices.Parent);
            }
            finally { preview.Stop(); game.Close(); }
        });
    }

    [Fact]
    public async Task Configuration_UpdatingStyleWithoutChangingDialogueTextRefreshesPreview_AndOpacityZeroRemainsValid()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var preview = new ScenePreview(); var story = new StoryFrame("dialogue", "", "same", []);
            var original = GameplayUiStyle.ForTarget("dialogue") with { FontSize = 20 };
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty with { UiConfigurations = [new("dialogue", original)] }, story, false, false);
            var text = Find<TextBlock>(preview, "DialogueText"); Assert.Equal(20, text.FontSize);
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty with { UiConfigurations = [new("dialogue", original with { FontSize = 42, Opacity = 0 })] }, story, false, false);
            Assert.Equal(42, text.FontSize); Assert.Equal(0, Find<Border>(preview, "DialoguePanel").Opacity);
            preview.Stop(); return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Configuration_AllNamedButtonsKeepIndependentGeometryAndVisibility_AndRestoreTheirOriginalParents()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project); var preview = new ScenePreview();
            try
            {
                var names = new[] { ("next", "NextButton"), ("save", "SaveButton"), ("load", "LoadButton"), ("auto_save", "LoadAutoButton"), ("pause", "PauseButton") };
                var originalParents = names.ToDictionary(item => item.Item1, item => Find<Button>(window, item.Item2).Parent);
                var styles = names.Select((item, i) => new GameplayUiConfiguration(item.Item1, GameplayUiStyle.ForTarget(item.Item1) with
                {
                    X = 20 + i * 180, Y = 50 + i * 60, Width = 140 + i, Height = 35 + i,
                    HorizontalAlignment = "left", VerticalAlignment = "top", FontFamily = "Consolas", FontSize = 17 + i,
                    FontWeight = "Bold", TextColor = "#FFFFCC80", BackgroundColor = "#FF302040", Opacity = .8, CornerRadius = 11,
                    PaddingLeft = 5, PaddingTop = 6, PaddingRight = 7, PaddingBottom = 8, TextAlignment = "right"
                })).ToArray();
                var visuals = SceneVisuals.Empty with { UiConfigurations = styles };
                Invoke(window, "ShowGameplay"); Invoke(window, "ApplyGameplayUi", visuals);
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, new("dialogue", "", "text", []), false, false);
                var previewScene = Find<Grid>(preview, "SceneArea");
                for (int i = 0; i < names.Length; i++)
                {
                    var button = Find<Button>(window, names[i].Item2); var style = styles[i].Style;
                    Assert.Same(Find<Grid>(window, "SceneArea"), button.Parent);
                    Assert.Equal(style.Width, button.Width); Assert.Equal(style.Height, button.Height);
                    Assert.Equal(new Thickness(style.X, style.Y, 0, 0), button.Margin);
                    Assert.Equal(style.FontSize, button.FontSize); Assert.Equal("Consolas", button.FontFamily.Source);
                    Assert.Equal(FontWeights.Bold, button.FontWeight); Assert.Equal(.8, button.Opacity);
                    Assert.Equal(new Thickness(5, 6, 7, 8), button.Padding); Assert.Equal(HorizontalAlignment.Right, button.HorizontalContentAlignment);
                    Assert.Equal(Color.FromRgb(255, 204, 128), Assert.IsType<SolidColorBrush>(button.Foreground).Color);
                    Assert.Equal(Color.FromRgb(48, 32, 64), Assert.IsType<SolidColorBrush>(button.Background).Color);
                    Assert.Equal(Visibility.Visible, button.Visibility);
                    var mirror = previewScene.Children.OfType<Button>().Single(item => Equals(item.Content, button.Content));
                    Assert.Equal(button.Margin, mirror.Margin); Assert.Equal(button.Width, mirror.Width); Assert.Equal(button.Padding, mirror.Padding);
                }
                Find<StackPanel>(window, "GameControls").Visibility = Visibility.Collapsed;
                Assert.Equal(Visibility.Collapsed, Find<Button>(window, "SaveButton").Visibility);
                Assert.Equal(Visibility.Visible, Find<Button>(window, "NextButton").Visibility);
                Find<Border>(window, "DialoguePanel").Visibility = Visibility.Collapsed;
                Assert.Equal(Visibility.Collapsed, Find<Button>(window, "NextButton").Visibility);
                Invoke(window, "ApplyGameplayUi", SceneVisuals.Empty);
                foreach (var (target, name) in names) Assert.Same(originalParents[target], Find<Button>(window, name).Parent);
                Assert.Equal("auto", Find<Button>(window, "LoadAutoButton").Tag);
                Assert.Equal(new[] { "Сохранить", "Загрузить", "Автосохранение", "Меню" }, Find<StackPanel>(window, "GameControls").Children.Cast<Button>().Select(item => (string)item.Content));
            }
            finally { preview.Stop(); window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Configuration_CorruptTextureFailsBeforePublishingStyle()
    {
        using var fixture = new TempProject(); fixture.Write("game/ui/broken.png", "not an image");
        fixture.Write("game/scenes/main.krl", "function main()\n" + Command("dialogue", GameplayUiStyle.ForTarget("dialogue") with { Texture = "game/ui/broken.png" }) + "d(nil,'unreachable') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("текстуру UI", error.ToString()); Assert.Empty(runtime.Visuals.UiConfigurations);
        Assert.Equal(2, GameErrorReport.TryGet(error)!.Line);
    }

    private static string Command(string target, GameplayUiStyle style) => "configure_ui('" + target + "',[==[" + JsonSerializer.Serialize(style, EngineSettings.JsonOptions) + "]==])\n";
    private static void WriteTexture(string root)
    {
        string path = Path.Combine(root, "game/ui/dialogue.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 90, 30, 70, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static T Find<T>(FrameworkElement target, string name) => (T)target.FindName(name);
    private static TextBlock? VisualText(DependencyObject target)
    {
        if (target is TextBlock text) return text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(target); i++)
            if (VisualText(VisualTreeHelper.GetChild(target, i)) is { } child) return child;
        return null;
    }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static void Arrange(FrameworkElement target) { target.Measure(new Size(1280, 720)); target.Arrange(new Rect(0, 0, 1280, 720)); target.UpdateLayout(); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
