using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectSetupTests
{
    [Theory]
    [InlineData(ProjectAuthoringMode.Code)]
    [InlineData(ProjectAuthoringMode.Visual)]
    public async Task Creation_DefaultsToFullHdAutomaticApiSixtyFpsAndVioletForBothAuthoringModes(ProjectAuthoringMode mode)
    {
        using var fixture = new TempProject();
        string path = await ProjectCreator.CreateProjectAsync("Default " + mode, fixture.Root, mode);
        var project = await ProjectLoader.LoadProjectAsync(path);
        Assert.Equal(1920, project.Engine.Window.Width); Assert.Equal(1080, project.Engine.Window.Height);
        Assert.Equal("auto", project.Engine.Performance.Graphics.Api); Assert.Equal(60, project.Engine.Performance.Graphics.MaxFps);
        Assert.Equal("violet", project.Engine.Theme.Palette); Assert.Equal("#FF201134", project.Engine.Theme.BackgroundColor);
        Assert.Equal(mode, project.Configuration.AuthoringMode);
        string main = mode == ProjectAuthoringMode.Code ? "game/scenes/main.krl" : "game/scenes/main.knegraph";
        Assert.Equal(main, project.Configuration.EntryPoints!.MainScene); Assert.True(File.Exists(Path.Combine(path, main)));
        Assert.Null(project.Engine.Screens.Theme);
        await using var worker = new ProcessGameSession(project, _ => { });
        Assert.Equal(1920, worker.WindowSettings.Width); Assert.Equal(1080, worker.WindowSettings.Height);
        Assert.Equal(1280, new WindowDefaults().Width); Assert.Equal(720, new WindowDefaults().Height);
    }

    [Fact]
    public void Options_ExposeExactlyTheSevenRequestedResolutionPresetsAndFiveNonCustomPalettes()
    {
        Assert.Equal(new[] { (1920, 1080), (1280, 720), (2560, 1440), (1024, 768), (800, 600), (2560, 1080), (1080, 1920) },
            ProjectCreationOptions.Resolutions.Select(preset => (preset.Width, preset.Height)));
        Assert.Equal(7, ProjectCreationOptions.Resolutions.Select(preset => preset.Id).Distinct().Count());
        Assert.All(ProjectCreationOptions.Resolutions, preset => { Assert.NotEmpty(preset.Title); Assert.NotEmpty(preset.Description); });
        Assert.Equal(new[] { "violet", "ocean", "rose", "forest", "amber" }, ProjectCreationOptions.Palettes.Select(palette => palette.Value));
    }

    public static IEnumerable<object[]> ResolutionChoices => ProjectCreationOptions.Resolutions.Select(preset => new object[] { preset.Id, preset.Width, preset.Height });

    [Theory]
    [MemberData(nameof(ResolutionChoices))]
    public async Task Creation_EachResolutionPresetIsPersistedAndBecomesTheRuntimeWindow(string id, int width, int height)
    {
        using var fixture = new TempProject();
        string path = ProjectCreator.CreateProject("Preset " + id, fixture.Root, ProjectAuthoringMode.Code,
            new ProjectCreationOptions { Width = width, Height = height });
        var project = await ProjectLoader.LoadProjectAsync(path);
        Assert.Equal(width, project.Engine.Window.Width); Assert.Equal(height, project.Engine.Window.Height);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "engine/window.json")));
        Assert.Equal(width, json.RootElement.GetProperty("width").GetInt32()); Assert.Equal(height, json.RootElement.GetProperty("height").GetInt32());
        Assert.Equal(new RuntimeWindowSettings(project.Title, width, height, project.Engine.Theme.BackgroundColor), project.Engine.RuntimeDefaults(project.Title));
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(3840, 2160)]
    [InlineData(640, 2160)]
    [InlineData(3840, 480)]
    [InlineData(1377, 777)]
    public async Task Creation_CustomResolutionAcceptsExactBoundariesAndInteriorWithoutClamping(int width, int height)
    {
        using var fixture = new TempProject();
        var options = new ProjectCreationOptions { Width = width, Height = height }; options.Validate();
        string path = await ProjectCreator.CreateProjectAsync("Custom", fixture.Root, ProjectAuthoringMode.Visual, options);
        var project = await ProjectLoader.LoadProjectAsync(path);
        Assert.Equal(width, project.Engine.Window.Width); Assert.Equal(height, project.Engine.Window.Height);
    }

    [Theory]
    [InlineData("auto", 30, "violet")]
    [InlineData("opengl", 60, "ocean")]
    [InlineData("vulkan", 144, "rose")]
    [InlineData("opengl", 0, "forest")]
    [InlineData("vulkan", 30, "amber")]
    public async Task Creation_ArchitectureFpsAndPaletteRoundTripTogetherWithoutScreensOverridingTheSelection(string api, int fps, string palette)
    {
        using var fixture = new TempProject(); var options = new ProjectCreationOptions { Width = 1080, Height = 1920, GraphicsApi = api, MaxFps = fps, Palette = palette };
        string path = ProjectCreator.CreateProject("Chosen settings", fixture.Root, ProjectAuthoringMode.Visual, options);
        var project = await ProjectLoader.LoadProjectAsync(path);
        Assert.Equal(api, project.Engine.Performance.Graphics.Api); Assert.Equal(fps, project.Engine.Performance.Graphics.MaxFps);
        Assert.Equal(1080, project.Engine.Window.Width); Assert.Equal(1920, project.Engine.Window.Height);
        var expected = new GameTheme(); if (palette != "violet") ThemePalettes.Apply(expected, palette);
        Assert.Null(project.Engine.Screens.Theme);
        foreach (GameTheme actual in new[] { project.Engine.Theme })
        {
            Assert.Equal(palette, actual.Palette); Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
            Assert.Equal(expected.PatternColor, actual.PatternColor); Assert.Equal(expected.AccentColor, actual.AccentColor);
            Assert.Equal(expected.ButtonColor, actual.ButtonColor); Assert.Equal(expected.Dialogue.SpeakerColor, actual.Dialogue.SpeakerColor);
        }
        using var theme = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "engine/theme.json")));
        Assert.Equal(palette, theme.RootElement.GetProperty("palette").GetString());
        using var graphics = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "engine/performance.json")));
        Assert.Equal(api, graphics.RootElement.GetProperty("graphics").GetProperty("api").GetString());
        Assert.Equal(fps, graphics.RootElement.GetProperty("graphics").GetProperty("max_fps").GetInt32());
    }

    public static IEnumerable<object[]> InvalidChoices()
    {
        foreach (int width in new[] { 639, 3841, 0, int.MaxValue }) yield return [new ProjectCreationOptions { Width = width }];
        foreach (int height in new[] { 479, 2161, 0, int.MaxValue }) yield return [new ProjectCreationOptions { Height = height }];
        foreach (string api in new[] { "", "directx", "OpenGL", null! }) yield return [new ProjectCreationOptions { GraphicsApi = api }];
        foreach (int fps in new[] { -1, 1, 59, 145 }) yield return [new ProjectCreationOptions { MaxFps = fps }];
        foreach (string palette in new[] { "", "missing", "custom", null! }) yield return [new ProjectCreationOptions { Palette = palette }];
    }

    [Theory]
    [MemberData(nameof(InvalidChoices))]
    public async Task Creation_InvalidChoicesFailBeforeAnyProjectOrStagingDirectoryIsCreated(ProjectCreationOptions options)
    {
        using var fixture = new TempProject(); var before = Directory.GetFileSystemEntries(fixture.Root).Order().ToArray();
        Assert.Throws<InvalidDataException>(options.Validate);
        Assert.Throws<InvalidDataException>(() => options.CreateEngineSettings());
        Assert.Throws<InvalidDataException>(() => ProjectCreator.CreateProject("Invalid sync", fixture.Root, ProjectAuthoringMode.Code, options));
        await Assert.ThrowsAsync<InvalidDataException>(() => ProjectCreator.CreateProjectAsync("Invalid async", fixture.Root, ProjectAuthoringMode.Visual, options));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Root).Order());
    }

    [Fact]
    public void Options_CreateIndependentSettingsWithoutChangingLegacyDefaultsOrAnotherNewProject()
    {
        var options = new ProjectCreationOptions { Palette = "rose" }; var first = options.CreateEngineSettings(); var second = options.CreateEngineSettings();
        first.Theme.AccentColor = "#FF010203"; first.Window.Width = 1800;
        Assert.Equal("#FFFFB8D4", second.Theme.AccentColor); Assert.Equal(1920, second.Window.Width);
        Assert.Equal(1920, options.Width); Assert.Equal("#FFD3ACFF", new GameTheme().AccentColor);
        Assert.Equal(1280, new EngineSettings().Window.Width);
    }

    [Fact]
    public async Task Wizard_DefaultChoicesOfferSevenAccurateResolutionThumbnailsAndFiveDistinctPalettePreviews()
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var dialog = new ProjectCreationDialog(); EditorTheme.Apply(dialog, "kne");
            try
            {
                Assert.Equal(Visibility.Visible, Find<Grid>(dialog, "AuthoringPage").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Grid>(dialog, "SettingsPage").Visibility);
                Click(Find<Button>(dialog, "ContinueButton"));
                Assert.Equal(Visibility.Collapsed, Find<Grid>(dialog, "AuthoringPage").Visibility);
                Assert.Equal(Visibility.Visible, Find<Grid>(dialog, "SettingsPage").Visibility);
                Assert.Equal(new ProjectCreationOptions(), dialog.Options);
                Assert.True(Find<RadioButton>(dialog, "Resolution_fhd").IsChecked); Assert.True(Find<RadioButton>(dialog, "Palette_violet").IsChecked);
                Assert.Equal("auto", Find<ComboBox>(dialog, "GraphicsApiInput").SelectedValue); Assert.Equal(60, Find<ComboBox>(dialog, "FrameRateInput").SelectedValue);
                Assert.False(Find<StackPanel>(dialog, "CustomResolutionFields").IsEnabled);
                var grid = Find<UniformGrid>(dialog, "ResolutionCards"); Assert.Equal(4, grid.Columns); Assert.Equal(2, grid.Rows); Assert.Equal(8, grid.Children.Count);
                var thumbnails = new Dictionary<string, Border>();
                foreach (var preset in ProjectCreationOptions.Resolutions)
                {
                    var card = Find<RadioButton>(dialog, "Resolution_" + preset.Id);
                    Assert.Equal(preset, card.Tag);
                    var body = Assert.IsType<StackPanel>(card.Content);
                    var thumbnail = Assert.Single(Assert.IsType<Canvas>(body.Children[0]).Children.OfType<Border>()); thumbnails.Add(preset.Id, thumbnail);
                    Assert.Equal((double)preset.Width / preset.Height, thumbnail.Width / thumbnail.Height, 10);
                    Assert.Equal(preset.Title, ((TextBlock)body.Children[1]).Text); Assert.Equal(preset.Description, ((TextBlock)body.Children[2]).Text);
                    card.IsChecked = true; Assert.Equal(preset.Width, dialog.Options.Width); Assert.Equal(preset.Height, dialog.Options.Height);
                }
                Assert.True(thumbnails["wide"].Width > thumbnails["fhd"].Width); Assert.True(thumbnails["qhd"].Height > thumbnails["fhd"].Height);
                Assert.True(thumbnails["portrait"].Height > thumbnails["portrait"].Width);
                var palettes = Find<UniformGrid>(dialog, "PaletteCards"); Assert.Equal(5, palettes.Columns); Assert.Equal(5, palettes.Children.Count);
                var previewColors = new HashSet<Color>();
                foreach (var palette in ProjectCreationOptions.Palettes)
                {
                    var card = Find<RadioButton>(dialog, "Palette_" + palette.Value); card.IsChecked = true; Assert.Equal(palette.Value, dialog.Options.Palette);
                    var preview = Assert.IsType<Border>(Assert.IsType<StackPanel>(card.Content).Children[0]);
                    var expected = new GameTheme(); if (palette.Value != "violet") ThemePalettes.Apply(expected, palette.Value);
                    Assert.Equal((Color)ColorConverter.ConvertFromString(expected.BackgroundColor), Assert.IsType<SolidColorBrush>(preview.Background).Color);
                    previewColors.Add(Assert.IsType<SolidColorBrush>(preview.Background).Color);
                    var swatches = Assert.IsType<UniformGrid>(preview.Child).Children.OfType<Border>().Select(border => Assert.IsType<SolidColorBrush>(border.Background).Color).ToArray();
                    Assert.Equal(new[] { expected.PatternColor, expected.AccentColor, expected.ButtonColor }.Select(color => (Color)ColorConverter.ConvertFromString(color)), swatches);
                }
                Assert.Equal(5, previewColors.Count);
                Find<RadioButton>(dialog, "Resolution_fhd").IsChecked = true; Find<RadioButton>(dialog, "Palette_violet").IsChecked = true;
                CaptureSettings(dialog);
                return Task.CompletedTask;
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public async Task Wizard_BackPreservesAuthoringModeCustomSizeArchitectureFpsAndPalette()
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var dialog = new ProjectCreationDialog();
            try
            {
                Find<RadioButton>(dialog, "VisualModeCard").IsChecked = true; Click(Find<Button>(dialog, "ContinueButton"));
                Find<RadioButton>(dialog, "Resolution_custom").IsChecked = true; Assert.True(Find<StackPanel>(dialog, "CustomResolutionFields").IsEnabled);
                Find<TextBox>(dialog, "CustomWidthInput").Text = "1377"; Find<TextBox>(dialog, "CustomHeightInput").Text = "777";
                Find<ComboBox>(dialog, "GraphicsApiInput").SelectedValue = "opengl"; Find<ComboBox>(dialog, "FrameRateInput").SelectedValue = 144;
                Find<RadioButton>(dialog, "Palette_forest").IsChecked = true;
                var expected = new ProjectCreationOptions { Width = 1377, Height = 777, GraphicsApi = "opengl", MaxFps = 144, Palette = "forest" };
                Assert.True(dialog.TryReadOptions(out var chosen)); Assert.Equal(expected, chosen);
                Click(Find<Button>(dialog, "BackButton")); Assert.Equal(Visibility.Visible, Find<Grid>(dialog, "AuthoringPage").Visibility);
                Assert.Equal(ProjectAuthoringMode.Visual, dialog.AuthoringMode);
                Click(Find<Button>(dialog, "ContinueButton")); Assert.Equal(expected, dialog.Options); Assert.True(Find<RadioButton>(dialog, "Resolution_custom").IsChecked);
                Find<RadioButton>(dialog, "Resolution_hd").IsChecked = true; Assert.False(Find<StackPanel>(dialog, "CustomResolutionFields").IsEnabled);
                Assert.Equal(1280, dialog.Options.Width); Assert.Equal(720, dialog.Options.Height);
                Find<RadioButton>(dialog, "Resolution_custom").IsChecked = true; Assert.Equal(expected, dialog.Options);
                return Task.CompletedTask;
            }
            finally { dialog.Close(); }
        });
    }

    [Theory]
    [InlineData(1024, 701)]
    [InlineData(760, 600)]
    [InlineData(640, 520)]
    public async Task Wizard_SettingsCardsAndCreateButtonRemainInsideSmallWorkAreasWithoutScrolling(int width, int height)
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var dialog = new ProjectCreationDialog(); EditorTheme.Apply(dialog, "kne");
            try
            {
                Click(Find<Button>(dialog, "ContinueButton")); var root = (FrameworkElement)dialog.Content;
                root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                var cards = Find<UniformGrid>(dialog, "ResolutionCards").Children.OfType<FrameworkElement>()
                    .Concat(Find<UniformGrid>(dialog, "PaletteCards").Children.OfType<FrameworkElement>()).Append(Find<Button>(dialog, "CreateButton"));
                foreach (var element in cards)
                {
                    var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    Assert.True(bounds.Width > 0 && bounds.Height > 0); Assert.True(bounds.Left >= 0 && bounds.Top >= 0);
                    Assert.True(bounds.Right <= width + .1, $"{element.Name} extends beyond width {width}: {bounds}");
                    Assert.True(bounds.Bottom <= height + .1, $"{element.Name} extends beyond height {height}: {bounds}");
                }
                return Task.CompletedTask;
            }
            finally { dialog.Close(); }
        });
    }

    [Theory]
    [InlineData("639", "480")]
    [InlineData("3841", "2160")]
    [InlineData("640", "479")]
    [InlineData("3840", "2161")]
    [InlineData("", "1080")]
    [InlineData("abc", "1080")]
    [InlineData("1920.5", "1080")]
    [InlineData("2147483648", "1080")]
    [InlineData("1920", "height")]
    public async Task Wizard_InvalidCustomDimensionsKeepTheDialogOnSettingsAndShowAnActionableError(string width, string height)
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var dialog = new ProjectCreationDialog();
            try
            {
                Click(Find<Button>(dialog, "ContinueButton")); Find<RadioButton>(dialog, "Resolution_custom").IsChecked = true;
                Find<TextBox>(dialog, "CustomWidthInput").Text = width; Find<TextBox>(dialog, "CustomHeightInput").Text = height;
                Click(Find<Button>(dialog, "CreateButton"));
                Assert.NotEqual(true, dialog.DialogResult); Assert.Equal(Visibility.Visible, Find<Grid>(dialog, "SettingsPage").Visibility);
                Assert.False(dialog.TryReadOptions(out var invalid)); Assert.Null(invalid); Assert.NotEmpty(Find<TextBlock>(dialog, "SetupErrorText").Text);
                Find<TextBox>(dialog, "CustomWidthInput").Text = "640"; Find<TextBox>(dialog, "CustomHeightInput").Text = "480";
                Assert.True(dialog.TryReadOptions(out var lower)); Assert.Equal(640, lower!.Width); Assert.Equal(480, lower.Height); Assert.Empty(Find<TextBlock>(dialog, "SetupErrorText").Text);
                Find<TextBox>(dialog, "CustomWidthInput").Text = "3840"; Find<TextBox>(dialog, "CustomHeightInput").Text = "2160";
                Assert.True(dialog.TryReadOptions(out var upper)); Assert.Equal(3840, upper!.Width); Assert.Equal(2160, upper.Height);
                return Task.CompletedTask;
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public async Task Wizard_ValidSelectionsSubmitTheActualOptionsThroughTheCreateButton()
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var dialog = new ProjectCreationDialog { ShowInTaskbar = false, ShowActivated = false, Left = -2000, WindowStartupLocation = WindowStartupLocation.Manual };
            dialog.Loaded += (_, _) =>
            {
                Find<RadioButton>(dialog, "VisualModeCard").IsChecked = true; Click(Find<Button>(dialog, "ContinueButton"));
                Find<RadioButton>(dialog, "Resolution_portrait").IsChecked = true; Find<RadioButton>(dialog, "Palette_rose").IsChecked = true;
                Find<ComboBox>(dialog, "GraphicsApiInput").SelectedValue = "vulkan"; Find<ComboBox>(dialog, "FrameRateInput").SelectedValue = 30;
                Click(Find<Button>(dialog, "CreateButton"));
            };
            try
            {
                Assert.True(dialog.ShowDialog()); Assert.Equal(ProjectAuthoringMode.Visual, dialog.AuthoringMode);
                Assert.Equal(new ProjectCreationOptions { Width = 1080, Height = 1920, Palette = "rose", GraphicsApi = "vulkan", MaxFps = 30 }, dialog.Options);
                return Task.CompletedTask;
            }
            finally { dialog.Close(); }
        });
    }

    private static T Find<T>(ProjectCreationDialog dialog, string name) where T : class => Assert.IsAssignableFrom<T>(dialog.FindName(name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void CaptureSettings(ProjectCreationDialog dialog)
    {
        if (Environment.GetEnvironmentVariable("KNE_SETUP_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); var content = (FrameworkElement)dialog.Content;
        content.Measure(new Size(1024, 701)); content.Arrange(new Rect(0, 0, 1024, 701)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1024, 701, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, "project-setup-wizard.png")); encoder.Save(file);
    }
}
