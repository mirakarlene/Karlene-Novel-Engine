using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class DesignNavigationTests
{
    [Fact]
    public Task BlockPicker_AllThirteenCategoriesFitOneNarrowColumnWithGaps_AndCardsKeepTheirSizeAndFavorites() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var library = new BlockLibraryView(new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "preferences.json")));
        var owner = new Window { Content = library };
        try
        {
            EditorTheme.Apply(owner, "kne"); library.Open(_ => true); await library.LoadPreferencesAsync(); FileBrowserDesignTests.Layout(library, new Size(1178, 652));
            var groups = WorkspaceTabsTests.Find<ListBox>(library, "BlockGroups");
            Assert.Equal(13, groups.Items.Count); Assert.InRange(groups.ActualWidth, 150, 170);
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetVerticalScrollBarVisibility(groups));
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(groups));
            double bottom = 0;
            foreach (var item in groups.Items.Cast<BlockGroup>())
            {
                var container = Assert.IsType<ListBoxItem>(groups.ItemContainerGenerator.ContainerFromItem(item));
                var rect = container.TransformToAncestor(groups).TransformBounds(new Rect(container.RenderSize));
                Assert.InRange(rect.Top, 0, groups.ActualHeight); Assert.InRange(rect.Bottom, 0, groups.ActualHeight);
                if (bottom > 0) Assert.True(rect.Top - bottom >= 3, item.Title + " needs a visible separation.");
                bottom = rect.Bottom;
                Assert.DoesNotContain(FileBrowserDesignTests.Descendants(container), view => view is ButtonBase);
                var label = Assert.Single(FileBrowserDesignTests.Descendants(container).OfType<TextBlock>(), text => text.Text == item.Title);
                Assert.True(label.DesiredSize.Width <= label.ActualWidth + 1, item.Title + " must not be horizontally clipped.");
            }
            var narrative = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "narrative"); groups.SelectedItem = narrative;
            FileBrowserDesignTests.Layout(library);
            var card = WorkspaceTabsTests.Find<WrapPanel>(library, "BlockCards").Children.OfType<Button>().First();
            Assert.Equal(236, card.Width); Assert.Equal(130, card.Height);
            var star = Assert.Single(FileBrowserDesignTests.Descendants(card).OfType<Button>());
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await library.SavePreferencesAsync();
            groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "favorites");
            Assert.Equal(card.Tag, Assert.Single(WorkspaceTabsTests.Find<WrapPanel>(library, "BlockCards").Children.Cast<Button>()).Tag);
            FileBrowserDesignTests.Capture(library, "block-library-navigation.png", new Size(1178, 652));
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task CategoryHover_CardsAndNavigationFillFromRightToLeftAnimateBothDirectionsAndFollowAccessibleTints() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        var library = new BlockLibraryView(new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "hover.json"))); var owner = new Window { Content = library };
        try
        {
            EditorTheme.Apply(owner, "rose"); library.Open(_ => true); await library.LoadPreferencesAsync(); FileBrowserDesignTests.Layout(library);
            var groups = WorkspaceTabsTests.Find<ListBox>(library, "BlockGroups");
            var narrative = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "narrative");
            var item = Assert.IsType<ListBoxItem>(groups.ItemContainerGenerator.ContainerFromItem(narrative));
            var card = WorkspaceTabsTests.Find<WrapPanel>(library, "BlockCards").Children.OfType<Button>().First();
            foreach (var control in new Control[] { item, card })
            {
                Assert.Equal((Color)ColorConverter.ConvertFromString(narrative.Color), WelcomeOptionsTests.BrushColor(CategoryHover.GetColor(control)));
                CategoryHover.SetProgress(control, .5); FileBrowserDesignTests.Layout(library);
                var fill = Assert.IsType<Border>(control.Template.FindName("CategoryFill", control));
                Assert.Equal(new Point(1, .5), fill.RenderTransformOrigin);
                Assert.Equal(.5, Assert.IsType<ScaleTransform>(fill.RenderTransform).ScaleX, 6);
                Assert.Equal(WelcomeOptionsTests.BrushColor(CategoryHover.GetColor(control)), WelcomeOptionsTests.BrushColor(fill.Background));
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                await Until(() => CategoryHover.GetProgress(control) >= .999, control.Dispatcher);
                control.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
                await Until(() => CategoryHover.GetProgress(control) <= .0000001, control.Dispatcher);
                Assert.Equal(0, CategoryHover.GetProgress(control), 3);
            }
            foreach (string mode in new[] { "high_contrast", "protanopia" })
            {
                BlockAccessibility.Apply(owner, mode); FileBrowserDesignTests.Layout(library);
                var accessible = BlockAccessibility.Presentation("narrative", mode);
                foreach (var control in new Control[] { item, card })
                {
                    Assert.Equal(accessible.Color, WelcomeOptionsTests.BrushColor(CategoryHover.GetColor(control)));
                    Assert.True(WelcomeOptionsTests.Contrast(WelcomeOptionsTests.BrushColor(CategoryHover.GetInk(control)), accessible.Color) >= 4.5);
                }
            }
            Assert.Equal("narrative", Assert.IsType<BlockGroup>(groups.SelectedItem).Id);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task Settings_NewPalettesAreDistinctPersistableAndReadable_AndAccessibilityThumbnailsAreTallWithUniformScaling() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new AccessibilitySettingsTests.SettingsFixture(); var window = new MainWindow();
        typeof(MainWindow).GetField("settingsStore", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, fixture.Store);
        try
        {
            typeof(MainWindow).GetMethod("BtnSettings_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new Button(), new RoutedEventArgs()]);
            FileBrowserDesignTests.Layout((FrameworkElement)window.Content, new Size(1460, 1000));
            var themes = WorkspaceTabsTests.Find<ComboBox>(window, "ThemeInput");
            var welcomeColors = new HashSet<Color>();
            foreach (string id in new[] { "sapphire", "peach", "graphite", "mint", "plum" })
            {
                Assert.Contains(themes.Items.Cast<EditorThemeOption>(), theme => theme.Id == id && !string.IsNullOrWhiteSpace(theme.Label));
                themes.SelectedValue = id; await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
                Assert.Equal(id, (await fixture.Store.LoadAsync()).Theme);
                var surface = WelcomeOptionsTests.BrushColor(window.FindResource("Editor.Surface")); var ink = WelcomeOptionsTests.BrushColor(window.FindResource("Editor.Text"));
                Assert.True(WelcomeOptionsTests.Contrast(surface, ink) >= 4.5, id + " text must remain readable.");
                var gradient = Assert.IsType<LinearGradientBrush>(window.FindResource("Editor.WelcomeBackground")); Assert.True(welcomeColors.Add(gradient.GradientStops[0].Color));
            }
            var cards = WorkspaceTabsTests.Find<ItemsControl>(window, "AccessibilityCards");
            var previews = FileBrowserDesignTests.Descendants(cards).OfType<AccessibilityPreview>().ToArray(); Assert.Equal(6, previews.Length);
            foreach (var preview in previews)
            {
                Assert.InRange(preview.ActualHeight, 100, 180); Assert.True(preview.ActualWidth > 100);
                var drawing = VisualTreeHelper.GetDrawing(preview); Assert.NotNull(drawing);
                var scales = Drawings(drawing!).Select(group => group.Transform?.Value).Where(matrix => matrix.HasValue && Math.Abs(matrix.Value.M11 - 1) > .001 && matrix.Value.M11 > 0).ToArray();
                Assert.NotEmpty(scales); Assert.All(scales, matrix => Assert.Equal(matrix!.Value.M11, matrix.Value.M22, 6));
            }
            FileBrowserDesignTests.Capture((FrameworkElement)window.Content, "settings-tall-previews.png", new Size(1460, 1000));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Welcome_RecentProjectsHeadingAlignsWithTheProjectEntriesInsteadOfTheWideWelcomePanel() => WorkspaceTabsTests.Sta(() =>
    {
        var window = new MainWindow();
        try
        {
            var recent = WorkspaceTabsTests.Find<ItemsControl>(window, "RecentProjectsList");
            recent.ItemsSource = new[] { new { Path = "C:\\projects\\Novel", Label = "Novel", LastOpenedLabel = "Сегодня", IsAvailable = true } };
            var root = (FrameworkElement)window.Content; FileBrowserDesignTests.Layout(root);
            var heading = Assert.Single(FileBrowserDesignTests.Descendants(root).OfType<TextBlock>(), text => text.Text == "Последние проекты");
            var button = Assert.Single(FileBrowserDesignTests.Descendants(recent).OfType<Button>(), item => item.Content is StackPanel);
            double headingLeft = heading.TranslatePoint(new Point(), root).X; double rowLeft = button.TranslatePoint(new Point(), root).X;
            Assert.InRange(headingLeft, rowLeft, rowLeft + button.Padding.Left + 2);
            FileBrowserDesignTests.Capture(root, "welcome-heading.png", new Size(1460, 900));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static IEnumerable<DrawingGroup> Drawings(Drawing drawing)
    {
        if (drawing is not DrawingGroup group) yield break; yield return group;
        foreach (var child in group.Children) foreach (var nested in Drawings(child)) yield return nested;
    }
    private static async Task Until(Func<bool> complete, Dispatcher dispatcher)
    {
        for (int i = 0; i < 150; i++) { await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); if (complete()) return; await Task.Delay(10); }
        Assert.True(complete(), "Hover animation did not reach its endpoint.");
    }
}
