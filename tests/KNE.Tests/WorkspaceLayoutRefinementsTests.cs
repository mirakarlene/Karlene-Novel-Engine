using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class WorkspaceLayoutRefinementsTests
{
    [Theory]
    [InlineData(1500, 950)] [InlineData(1100, 700)]
    public Task SettingsPages_KeepAssignedGroupsFullSizeAndPreserveDraftsAcrossAllFourPages(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new MainWindow(); var store = new UserSettingsStore(Path.Combine(fixture.Root, "settings.json"));
        typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store);
        try
        {
            typeof(MainWindow).GetMethod("BtnSettings_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
            var root = (FrameworkElement)window.Content; var general = Find<GeneralSettingsEditor>(window, "GeneralSettingsPage");
            void Layout() { root.Measure(new Size(width - 20, height - 40)); root.Arrange(new Rect(0, 0, width - 20, height - 40)); root.UpdateLayout(); }
            Rect Bounds(FrameworkElement item) => item.TransformToAncestor(root).TransformBounds(new Rect(item.RenderSize));
            void Fits(FrameworkElement item)
            {
                var bounds = Bounds(item); Assert.True(item.ActualWidth > 0 && item.ActualHeight > 0);
                Assert.InRange(bounds.Left, 0, root.ActualWidth); Assert.InRange(bounds.Right, 0, root.ActualWidth); Assert.InRange(bounds.Top, 0, root.ActualHeight); Assert.InRange(bounds.Bottom, 0, root.ActualHeight);
            }
            void Next() { Find<Button>(window, "NextSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(); }
            general.BackupPath.Text = Path.Combine(fixture.Root, "backups"); general.AutosaveInterval.SelectedValue = 10; general.BackupCopies.SelectedValue = 10; Layout();
            Assert.Equal("1 / 4 · Сохранение и горячие клавиши", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.Equal(0, general.ScrollableHeight); Assert.Equal(1, general.LayoutTransform.Value.M11);
            var first = LogicalChildren(general).ToArray(); var second = LogicalChildren(general.BehaviorPage).ToArray();
            Assert.InRange(((Grid)general.Content).ActualHeight, general.ViewportHeight, general.ViewportHeight + 1);
            var manualBackup = first.OfType<Button>().Single(button => Equals(button.Content, "Создать резервную копию"));
            var generalGrid = (Grid)general.Content;
            Assert.InRange(generalGrid.ActualWidth, 1, 1300);
            Assert.InRange(Math.Abs(Bounds(generalGrid).Left - Bounds(general).Left - (general.ActualWidth - generalGrid.ActualWidth) / 2), 0, 2);
            Assert.True(Bounds(general).Bottom - Bounds(manualBackup).Bottom > 48);
            Assert.InRange(Bounds(manualBackup).Top - Bounds(general.BackupCopies).Bottom, 0, 90);
            Fits(general.ExtendedSceneBlockLimit);
            Assert.Contains(general.ExtendedSceneBlockLimit, first);
            Assert.DoesNotContain(general.ExtendedSceneBlockLimit, second);
            Assert.False(general.ExtendedSceneBlockLimit.IsChecked);
            Assert.InRange(Bounds(general.ExtendedSceneBlockLimit).Top - Bounds(manualBackup).Bottom, 0, 95);
            general.ExtendedSceneBlockLimit.IsChecked = true;
            Fits(general.BlockGlowInput);
            Fits(general.BlockGlowSmoothingInput);
            Fits(general.ShaderGradientInput);
            Assert.True(Bounds(general.ShaderGradientInput).Top >= Bounds(general.BlockGlowInput).Bottom);
            Assert.Equal("Градиент для блоков шейдеров", System.Windows.Automation.AutomationProperties.GetName(general.ShaderGradientInput));
            var gradientRow = Assert.IsType<DockPanel>(general.ShaderGradientInput.Parent);
            var gradientLabel = Assert.Single(gradientRow.Children.OfType<TextBlock>());
            Fits(gradientLabel);
            Assert.Equal(12, gradientLabel.FontSize);
            Assert.Equal(110, general.ShaderGradientInput.ActualWidth);
            var graphSection = Assert.IsAssignableFrom<Panel>(gradientRow.Parent);
            Assert.Same(gradientRow, graphSection.Children[^1]);
            Assert.Equal(21, graphSection.Children.OfType<TextBlock>().Single().FontSize);
            Assert.Equal(BlockGlowMode.Medium, general.BlockGlowInput.SelectedValue);
            Assert.Equal(true, general.BlockGlowSmoothingInput.SelectedValue);
            general.BlockGlowInput.SelectedValue = BlockGlowMode.Weak;
            general.BlockGlowSmoothingInput.SelectedValue = false;
            Assert.Equal(true, general.ShaderGradientInput.SelectedValue);
            general.ShaderGradientInput.SelectedValue = false;
            foreach (var control in new FrameworkElement[] { general.BackupPath, general.BackupFrequency, general.BackupCopies, general.AutosaveInterval, general.HistoryLimit, general.KeepHistory }) { Assert.Contains(control, first); Assert.DoesNotContain(control, second); Fits(control); }
            Assert.Contains(first.OfType<Button>(), button => Equals(button.Content, "Настроить горячие клавиши"));
            Assert.DoesNotContain(first.OfType<Button>(), button => Equals(button.Content, "Удалить весь кэш"));
            foreach (var control in new FrameworkElement[] { general.Startup, general.StartupProject, general.Toasts, general.Breaks, general.WorkTime }) { Assert.DoesNotContain(control, first); Assert.Contains(control, second); }
            Fits(Find<TextBlock>(window, "SettingsPageLabel")); Capture(root, "saving-" + width);
            Next(); Assert.Equal("2 / 4 · Поведение и уведомления", Find<TextBlock>(window, "SettingsPageLabel").Text);
            general.Startup.SelectedValue = "project"; general.StartupProject.Text = fixture.ProjectPath; general.Tips.IsChecked = true; general.Breaks.IsChecked = true; general.BreakType.SelectedValue = "strict"; general.BreakInterval.SelectedValue = 90; general.WorkTime.IsChecked = true;
            general.Status.Text = "Проверка обратной связи"; Layout(); Assert.Equal(general.Status.Text, general.BehaviorStatus.Text);
            if (height == 950) Assert.Equal(0, general.BehaviorPage.ScrollableHeight);
            Assert.True(((Grid)general.BehaviorPage.Content).ActualHeight >= general.BehaviorPage.ViewportHeight);
            Fits(general.Startup); Fits(general.Toasts); Capture(root, "behavior-top-" + width);
            general.BehaviorPage.ScrollToEnd(); Layout(); Fits(general.WorkTime); Capture(root, "behavior-bottom-" + width);
            var expected = general.Value;
            Next(); Assert.Equal("3 / 4 · Проекты и Рендер", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "ProjectsRenderSettingsPage").Visibility);
            var projectSettings = Find<Viewbox>(window, "ProjectsRenderSettingsPage");
            var settingsGrid = (Grid)projectSettings.Child;
            // Compare the rendered scale with the page before the minimap controls were added.
            Assert.True(Bounds(settingsGrid).Width >= (width == 1500 ? 1265 : 830), "Settings text and cards were scaled down.");
            var render = Find<Border>(window, "RenderSettingsGroup");
            var cache = Find<Button>(window, "ClearEditorCacheButton");
            var motion = Find<CheckBox>(window, "DisableLuaLibraryMotionInput");
            var renderControls = LogicalChildren(render).ToArray();
            Assert.Contains(cache, renderControls); Assert.Contains(motion, renderControls);
            Fits(cache); Fits(motion); motion.IsChecked = true;
            var objects = Find<ComboBox>(window, "WelcomeObjectInput");
            var files = Find<ComboBox>(window, "ProjectBrowserViewInput");
            Fits(objects); Fits(files);
            Assert.Equal(Bounds(objects).Top, Bounds(files).Top, 2);
            Assert.Equal(Bounds(objects).Width, Bounds(files).Width, 2);
            Assert.True(Bounds(objects).Right < Bounds(files).Left);
            var treeNames = Find<ComboBox>(window, "ProjectTreeNamesInput");
            Assert.Contains(treeNames, LogicalChildren(Find<Border>(window, "DesignSettingsGroup")));
            Fits(treeNames);
            Assert.Equal(2, treeNames.Items.Count);
            double renderedWidth = Bounds(settingsGrid).Width;
            var treeNamesRow = Assert.IsType<DockPanel>(treeNames.Parent);
            treeNamesRow.Visibility = Visibility.Collapsed;
            Layout();
            Assert.Equal(renderedWidth, Bounds(settingsGrid).Width, 2);
            treeNamesRow.Visibility = Visibility.Visible;
            Layout();
            var minimapColors = Find<CheckBox>(window, "GraphMinimapColorsInput");
            var minimapSize = Find<ComboBox>(window, "GraphMinimapSizeInput");
            var minimapCorner = Find<ComboBox>(window, "GraphMinimapCornerInput");
            foreach (var control in new FrameworkElement[] { minimapColors, minimapSize, minimapCorner }) Fits(control);
            Assert.False(minimapColors.IsChecked);
            Assert.Equal(126d, minimapSize.SelectedValue);
            Assert.Equal("bottom_right", minimapCorner.SelectedValue);
            Assert.Equal(3, minimapSize.Items.Count);
            Assert.Equal(4, minimapCorner.Items.Count);
            Capture(root, "projects-render-" + width);
            minimapColors.IsChecked = true;
            minimapSize.SelectedValue = 168d;
            minimapCorner.SelectedValue = "top_left";
            Next(); Assert.Equal("4 / 4 · Плагины", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.False(Find<Button>(window, "NextSettingsPageButton").IsEnabled);
            for (int i = 0; i < 3; i++) Find<Button>(window, "PreviousSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Assert.Equal(expected, general.Value); Assert.False(Find<Button>(window, "PreviousSettingsPageButton").IsEnabled);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            var saved = await store.LoadAsync();
            Assert.Equal(expected with { LuaLibraryMotion = false }, saved.Editor);
            Assert.True(Find<ScriptEditor>(window, "SceneEditor").ExtendedSceneBlockLimit);
            Assert.Equal(new GraphMinimapSettings(true, 168, "top_left"), saved.GraphMinimap);
            var canvasMinimap = Find<EditorMinimap>(Find<ScriptEditor>(window, "SceneEditor"), "GraphMinimap");
            Assert.True(canvasMinimap.ColorCategories);
            Assert.Equal(168d, canvasMinimap.Width);
            Assert.Equal(HorizontalAlignment.Left, canvasMinimap.HorizontalAlignment);
            Assert.Equal(VerticalAlignment.Top, canvasMinimap.VerticalAlignment);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1500, 950)]
    [InlineData(1100, 700)]
    public Task WorkCounter_UsesReleaseRowWithoutMovingEditorOrToolbarWhenToggled(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var window = new MainWindow();
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
            await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
            var root = (FrameworkElement)window.Content;
            var preferences = (UserSettings)typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var counter = Find<TextBlock>(window, "WorkTimeStatus");
            string[] names = ["EditorWorkspace", "ProjectHeader", "DocumentTabs", "ProjectFilesTree", "RunProjectButton", "BuildReleaseButton", "AnalyzeProjectButton", "ExitActionsGroup", "ImportActionsGroup", "FileViewActionsGroup"];
            void Layout() { root.Measure(new Size(width - 20, height - 40)); root.Arrange(new Rect(0, 0, width - 20, height - 40)); root.UpdateLayout(); }
            Rect Bounds(FrameworkElement element) => element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
            void ShowCounter(bool enabled) { preferences.Editor.ShowWorkTime = enabled; typeof(MainWindow).GetMethod("UpdateBreakUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Layout(); }
            ShowCounter(false);
            var before = names.ToDictionary(name => name, name => Bounds(Find<FrameworkElement>(window, name)));
            Capture(root, "counter-off-" + width);
            window.SessionClock.Advance(TimeSpan.FromHours(1), preferences.Editor); ShowCounter(true);
            Assert.Equal("Время работы: 01:00:00", counter.Text); Assert.Equal(Visibility.Visible, counter.Visibility);
            foreach (string name in names)
            {
                Rect actual = Bounds(Find<FrameworkElement>(window, name)); Rect expected = before[name];
                Assert.Equal(expected.X, actual.X, 3); Assert.Equal(expected.Y, actual.Y, 3); Assert.Equal(expected.Width, actual.Width, 3); Assert.Equal(expected.Height, actual.Height, 3);
            }
            Rect clock = Bounds(counter), build = Bounds(Find<Button>(window, "BuildReleaseButton"));
            Assert.Equal(build.Top, clock.Top, 3); Assert.Equal(build.Height, clock.Height, 3); Assert.True(clock.Right < build.Left);
            Assert.InRange(clock.Left, 0, root.ActualWidth); Assert.InRange(clock.Bottom, 0, root.ActualHeight);
            var text = new FormattedText(counter.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(counter.FontFamily, counter.FontStyle, counter.FontWeight, counter.FontStretch), counter.FontSize, Brushes.White, 1) { MaxTextWidth = counter.ActualWidth };
            Assert.True(text.Height <= counter.ActualHeight, "The full counter text must fit without a smaller font.");
            Capture(root, "counter-on-" + width);
            ShowCounter(false);
            Assert.Equal(before["EditorWorkspace"], Bounds(Find<FrameworkElement>(window, "EditorWorkspace")));
        }
        finally { Find<ScriptEditor>(window, "SceneEditor").Clear(); window.Close(); }
    });

    private static T Find<T>(FrameworkElement root, string name) where T : class => WorkspaceTabsTests.Find<T>(root, name);
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        { yield return child; foreach (var nested in LogicalChildren(child)) yield return nested; }
    }
    private static void Capture(FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("KNE_SETTINGS_REFINEMENTS_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var context = background.RenderOpen()) context.DrawRectangle(root.TryFindResource("Editor.Background") as Brush, null, new Rect(0, 0, width, height)); image.Render(background); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
}
