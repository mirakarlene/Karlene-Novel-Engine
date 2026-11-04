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
            Assert.InRange(Bounds(general).Bottom - Bounds(manualBackup).Bottom, 0, 48);
            foreach (var control in new FrameworkElement[] { general.BackupPath, general.BackupFrequency, general.BackupCopies, general.AutosaveInterval, general.HistoryLimit, general.KeepHistory }) { Assert.Contains(control, first); Assert.DoesNotContain(control, second); Fits(control); }
            Assert.Contains(first.OfType<Button>(), button => Equals(button.Content, "Настроить горячие клавиши"));
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
            Next(); Assert.Equal("4 / 4 · Плагины", Find<TextBlock>(window, "SettingsPageLabel").Text); Assert.False(Find<Button>(window, "NextSettingsPageButton").IsEnabled);
            for (int i = 0; i < 3; i++) Find<Button>(window, "PreviousSettingsPageButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout();
            Assert.Equal(expected, general.Value); Assert.False(Find<Button>(window, "PreviousSettingsPageButton").IsEnabled);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync"); Assert.Equal(expected, (await store.LoadAsync()).Editor);
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
