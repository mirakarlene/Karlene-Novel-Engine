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
    private static void Capture(FrameworkElement root, string name)
    {
        if (Environment.GetEnvironmentVariable("KNE_SETTINGS_REFINEMENTS_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var context = background.RenderOpen()) context.DrawRectangle(root.TryFindResource("Editor.Background") as Brush, null, new Rect(0, 0, width, height)); image.Render(background); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(output);
    }
}
