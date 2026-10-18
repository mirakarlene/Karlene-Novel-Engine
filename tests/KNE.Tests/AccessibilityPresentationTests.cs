using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class AccessibilityPresentationTests
{
    [Theory]
    [InlineData("normal", 255, 0, 0)]
    [InlineData("protanopia", 109, 95, 0)]
    [InlineData("deuteranopia", 163, 144, 0)]
    [InlineData("tritanopia", 255, 0, 15)]
    [InlineData("achromatopsia", 127, 127, 127)]
    [InlineData("high_contrast", 255, 0, 0)]
    public void ColorVision_SixModesUseLinearLightReferenceValuesAndPreserveAlpha(string mode, byte red, byte green, byte blue)
    {
        // Full-severity Machado matrices applied to unit linear red, then encoded as sRGB.
        Assert.Equal(Color.FromArgb(73, red, green, blue), ColorVisionFilter.Transform(Color.FromArgb(73, 255, 0, 0), mode));
        Assert.Equal(Color.FromArgb(0, red, green, blue), ColorVisionFilter.Transform(Color.FromArgb(0, 255, 0, 0), mode));
        Assert.Equal(Colors.Black, ColorVisionFilter.Transform(Colors.Black, mode));
        Assert.Equal(Colors.White, ColorVisionFilter.Transform(Colors.White, mode));
    }

    [Fact]
    public void ColorVision_GrayscaleUsesPerceptualLuminanceInsteadOfChannelAverage_AndInvalidModeIsIdentity()
    {
        Assert.Equal(Color.FromRgb(127, 127, 127), ColorVisionFilter.Transform(Colors.Red, "achromatopsia"));
        Assert.Equal(Color.FromRgb(220, 220, 220), ColorVisionFilter.Transform(Color.FromRgb(0, 255, 0), "achromatopsia"));
        Assert.Equal(Color.FromRgb(76, 76, 76), ColorVisionFilter.Transform(Colors.Blue, "achromatopsia"));
        var input = Color.FromArgb(33, 18, 123, 201);
        foreach (string? mode in new[] { null, "", "missing" })
        {
            Assert.Equal("normal", BlockAccessibility.Normalize(mode));
            Assert.Equal(input, ColorVisionFilter.Transform(input, mode!));
        }
        Assert.Equal(Color.FromRgb(128, 128, 128), ColorVisionFilter.Transform(Color.FromRgb(128, 128, 128), "protanopia"));
        Assert.NotEqual(Color.FromRgb(128, 128, 128), ColorVisionFilter.Transform(Color.FromRgb(128, 128, 128), "high_contrast"));
    }

    [Theory]
    [InlineData("protanopia", 255, 229, 0, 0, 89, 255)]
    [InlineData("deuteranopia", 239, 214, 58, 0, 61, 251)]
    [InlineData("tritanopia", 0, 247, 217, 0, 107, 150)]
    public void ColorVision_GreenAndBlueReferenceVectorsProtectEveryMatrixColumn(string mode, byte gr, byte gg, byte gb, byte br, byte bg, byte bb)
    {
        Assert.Equal(Color.FromRgb(gr, gg, gb), ColorVisionFilter.Transform(Color.FromRgb(0, 255, 0), mode));
        Assert.Equal(Color.FromRgb(br, bg, bb), ColorVisionFilter.Transform(Colors.Blue, mode));
    }

    [Fact]
    public void AdaptedCategories_AlwaysCarrySymbolsReadableTextAndStrongerContrastBorders_WhileNormalPreservesColors()
    {
        Assert.Equal(new[] { "normal", "protanopia", "deuteranopia", "tritanopia", "achromatopsia", "high_contrast" }, BlockAccessibility.Modes.Select(option => option.Id));
        Assert.All(BlockAccessibility.Modes, option => { Assert.False(string.IsNullOrWhiteSpace(option.Title)); Assert.False(string.IsNullOrWhiteSpace(option.Description)); });
        Assert.Equal("speech", CategoryIcon.SymbolFor("narrative")); Assert.Equal("gear", CategoryIcon.SymbolFor("logic")); Assert.Equal("eye", CategoryIcon.SymbolFor("visual"));
        Assert.Equal(Color.FromRgb(230, 159, 0), BlockAccessibility.Presentation("narrative", "protanopia").Color);
        Assert.Equal(Color.FromRgb(0, 114, 178), BlockAccessibility.Presentation("logic", "deuteranopia").Color);
        Assert.Equal(Color.FromRgb(144, 63, 128), BlockAccessibility.Presentation("visual", "tritanopia").Color);
        Assert.Equal(11, BlockLibrary.Groups.Select(group => CategoryIcon.SymbolFor(group.Id)).Distinct().Count());
        foreach (var group in BlockLibrary.Groups)
        {
            var normal = BlockAccessibility.Presentation(group.Id, "normal");
            Assert.Equal((Color)ColorConverter.ConvertFromString(group.Color), normal.Color); Assert.False(normal.Badges);
            Assert.Equal(normal, BlockAccessibility.Presentation(group.Id, "invalid"));
            foreach (var mode in BlockAccessibility.Modes)
            {
                var display = BlockAccessibility.Presentation(group.Id, mode.Id);
                Assert.Equal(mode.Id != "normal", display.Badges);
                Assert.True(WelcomeOptionsTests.Contrast(display.Color, display.Foreground) >= 4.5, group.Id + "/" + mode.Id);
                Assert.Equal(mode.Id == "high_contrast" ? 3d : 1.5, display.BorderWidth);
                if (mode.Id == "achromatopsia") { Assert.Equal(display.Color.R, display.Color.G); Assert.Equal(display.Color.G, display.Color.B); }
            }
        }
    }

    [Fact]
    public Task AccessibilityPreviews_ShowSixDistinctSimulationsWithoutApplyingAnEffectToTheEditor() => WorkspaceTabsTests.Sta(() =>
    {
        var previews = new List<byte[]>();
        foreach (var mode in BlockAccessibility.Modes)
        {
            var view = new AccessibilityPreview { Mode = mode.Id };
            byte[] pixels = WelcomeOptionsTests.Pixels(view, new Size(200, 76));
            Assert.Null(view.Effect); Assert.DoesNotContain(previews, before => before.SequenceEqual(pixels)); previews.Add(pixels);
            if (mode.Id == "achromatopsia")
                for (int i = 0; i < pixels.Length; i += 4) { Assert.Equal(pixels[i], pixels[i + 1]); Assert.Equal(pixels[i + 1], pixels[i + 2]); }
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task CategoryIcons_InheritModeExposeCategoryNamesAndRetainDifferentSilhouettes_AndNormalHidesThem() => WorkspaceTabsTests.Sta(() =>
    {
        var panel = new StackPanel(); var owner = new Window { Content = panel };
        try
        {
            var silhouettes = new List<byte[]>();
            foreach (var group in BlockLibrary.Groups)
            {
                var icon = new CategoryIcon { Group = group.Id, Foreground = Brushes.White }; panel.Children.Add(icon);
                Assert.Equal(Visibility.Collapsed, icon.Visibility); Assert.False(icon.IsHitTestVisible);
                Assert.Equal("Категория: " + group.Title, AutomationProperties.GetName(icon));
            }
            BlockAccessibility.Apply(owner, "deuteranopia");
            foreach (var icon in panel.Children.Cast<CategoryIcon>())
            {
                Assert.Equal("deuteranopia", BlockAccessibility.GetMode(icon)); Assert.Equal(Visibility.Visible, icon.Visibility);
                var pixels = WelcomeOptionsTests.Pixels(icon, new Size(24, 24));
                Assert.Contains(pixels, value => value > 0); Assert.DoesNotContain(silhouettes, before => before.SequenceEqual(pixels)); silhouettes.Add(pixels);
            }
            BlockAccessibility.Apply(owner, "invalid"); Assert.Equal("normal", BlockAccessibility.GetMode(owner));
            Assert.All(panel.Children.Cast<CategoryIcon>(), icon => Assert.Equal(Visibility.Collapsed, icon.Visibility));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditorAndCatalog_LiveAccessibilityChangesColorsBadgesAndBordersWithoutChangingGraphDraftSelectionOrFavorites() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new AccessibilitySettingsTests.SettingsFixture();
        string project = ProjectCreator.CreateProject("Accessibility", fixture.Root);
        var graph = VisualScript.Create(); graph.FunctionName = "accessible";
        string path = Path.Combine(project, "game", "scenes", "accessible.knegraph"); await File.WriteAllTextAsync(path, graph.Serialize());
        var editor = new ScriptEditor(); var library = new BlockLibraryView(new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "favorites.json")));
        var panel = new StackPanel(); panel.Children.Add(editor); panel.Children.Add(library); var owner = new Window { Content = panel };
        try
        {
            EditorTheme.Apply(owner, "kne"); await editor.OpenAsync(project, path); editor.AddBlock(ScriptNodeKind.Dialogue);
            var node = editor.Document!.Graph!.Nodes.Last(); string draft = editor.Document.Graph.Serialize(); Assert.True(editor.Document.IsDirty);
            await library.LoadPreferencesAsync(); library.Open(_ => true);
            var groups = WorkspaceTabsTests.Find<ListBox>(library, "BlockGroups");
            groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "narrative");
            var cards = WorkspaceTabsTests.Find<WrapPanel>(library, "BlockCards");
            panel.Measure(new Size(1460, 1800)); panel.Arrange(new Rect(0, 0, 1460, 1800)); panel.UpdateLayout();
            Button Card() => cards.Children.Cast<Button>().Single(card => Equals(card.Tag, ScriptNodeKind.Dialogue));
            var favorite = Descendants(Card()).OfType<Button>().Single(); favorite.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await library.SavePreferencesAsync();
            foreach (string mode in new[] { "protanopia", "high_contrast", "normal" })
            {
                BlockAccessibility.Apply(owner, mode);
                panel.UpdateLayout();
                var graphCard = WorkspaceTabsTests.Find<Canvas>(editor, "GraphCanvas").Children.OfType<Border>().Single(card => Equals(card.Tag, node.Id));
                var header = Assert.IsType<Border>(Assert.IsType<StackPanel>(graphCard.Child).Children[0]);
                var expected = BlockAccessibility.Presentation("narrative", mode);
                Assert.Equal(expected.Color, WelcomeOptionsTests.BrushColor(header.Background));
                var graphIcon = Assert.Single(Descendants(graphCard).OfType<CategoryIcon>());
                Assert.Equal(expected.Badges ? Visibility.Visible : Visibility.Collapsed, graphIcon.Visibility);
                var pickerIcon = Assert.Single(Descendants(Card()).OfType<CategoryIcon>());
                Assert.Equal("narrative", pickerIcon.Group); Assert.Equal(graphIcon.Visibility, pickerIcon.Visibility);
                Assert.Equal(expected.Color, WelcomeOptionsTests.BrushColor(Card().BorderBrush));
                Assert.Equal(new Thickness(expected.BorderWidth + 1), graphCard.BorderThickness);
                Assert.Equal(expected.BorderWidth == 3 ? new Thickness(3, 5, 3, 3) : new Thickness(0, 5, 0, 1), Card().BorderThickness);
                Assert.Equal(node.Title, WorkspaceTabsTests.Find<TextBlock>(editor, "SelectedTitle").Text);
                Assert.Equal(2, Descendants(graphCard).OfType<GraphPort>().Count());
                Assert.Equal(draft, editor.Document.Graph.Serialize()); Assert.True(editor.Document.IsDirty);
                Assert.Equal("★", Descendants(Card()).OfType<Button>().Single().Content);
                Assert.Null(editor.Effect); Assert.Null(graphCard.Effect);
            }
            groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "favorites");
            Assert.Equal(ScriptNodeKind.Dialogue, Assert.Single(cards.Children.Cast<Button>()).Tag);
            editor.Clear();
        }
        finally { editor.Clear(); owner.Close(); }
    });

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
