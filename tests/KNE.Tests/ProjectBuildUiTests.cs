using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBuildUiTests
{
    [Theory]
    [InlineData(780, 840)]
    [InlineData(540, 500)]
    public async Task Pages_KeepFirstPageDesignAndBothDrafts_SaveCancelAndBuildControlsFit(double width, double height)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new ProjectParametersWindow(project, fixture.Root); EditorTheme.Apply(window, "kne");
            try
            {
                window.Width = width; window.Height = height; Layout(window, width, height);
                Assert.Equal(0, window.Page); Assert.Equal(Visibility.Collapsed, window.BuildInput.Visibility); Assert.False(window.PreviousPage.IsEnabled); Assert.True(window.NextPage.IsEnabled);
                Assert.Equal("vm", window.BuildInput.Value.TestMode); Assert.Equal("self_contained", window.BuildInput.Value.ReleaseMode);
                Assert.Equal(420, window.TitleInput.Width); Assert.Equal(3, Parent<Grid>(Parent<Border>(window.GraphicsInput.ApiInput)).ColumnDefinitions.Count);
                double x = window.TitleInput.TransformToAncestor((Visual)window.Content).Transform(new Point()).X;
                window.SavesInput.Text = "draft/saves"; window.BuildInput.Author.Text = "Draft author";
                Capture(window, width, height, "project");
                Click(window.NextPage); Layout(window, width, height);
                Assert.Equal(1, window.Page); Assert.Equal(Visibility.Collapsed, window.TitleInput.Visibility == Visibility.Collapsed ? window.TitleInput.Visibility : Parent<StackPanel>(Parent<StackPanel>(window.TitleInput)).Visibility);
                Assert.True(window.PreviousPage.IsEnabled); Assert.False(window.NextPage.IsEnabled); Assert.Equal(Visibility.Visible, window.BuildInput.Visibility);
                window.BuildInput.TestMode.SelectedValue = "remote_debug"; Assert.True(window.BuildInput.TestPath.IsEnabled);
                window.BuildInput.ReleaseMode.SelectedValue = "portable"; Assert.Equal("local", window.BuildInput.Value.EffectiveSaveLocation); Assert.False(window.BuildInput.Saves.IsEnabled);
                Layout(window, width, height);
                foreach (FrameworkElement control in new FrameworkElement[] { window.BuildInput.TestMode, window.BuildInput.TestPath, window.BuildInput.ReleaseMode, window.BuildInput.ReleasePath, window.BuildInput.Author, window.BuildInput.Team, window.BuildInput.Version, window.BuildInput.Saves, window.BuildInput.BuildButton, window.PreviousPage, window.NextPage }) Fits(control, (FrameworkElement)window.Content);
                Capture(window, width, height, "builds");
                Click(window.PreviousPage); Layout(window, width, height); Assert.Equal("draft/saves", window.SavesInput.Text); Assert.Equal("Draft author", window.BuildInput.Author.Text);
                Assert.Equal(x, window.TitleInput.TransformToAncestor((Visual)window.Content).Transform(new Point()).X, 3);
                Assert.False(File.Exists(Path.Combine(project.Path, "engine", "build.json")));
                await window.SaveAsync(); var saved = ProjectBuildSettingsStore.Load(project.Path); Assert.Equal("remote_debug", saved.TestMode); Assert.Equal("portable", saved.ReleaseMode); Assert.Equal("Draft author", saved.Author); Assert.Equal("draft/saves", PerformanceSettingsStore.LoadProject(project.Path).SavesPath);
            }
            finally { window.Close(); }
            var cancel = new ProjectParametersWindow(project);
            try { cancel.BuildInput.Version.Text = "9.9"; Click(cancel.NextPage); cancel.Close(); Assert.Equal("1.0.0", ProjectBuildSettingsStore.Load(project.Path).Version); }
            finally { cancel.Close(); }
        });
    }

    [Fact]
    public async Task InvalidBuildDraftCannotChangeProjectTitlePerformanceOrStoredBuildSettings()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string performance = File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new ProjectParametersWindow(project);
            try
            {
                window.TitleInput.Text = "Changed"; window.SavesInput.Text = "changed"; window.BuildInput.Version.Text = "bad";
                await Assert.ThrowsAsync<InvalidDataException>(() => window.SaveAsync());
                Assert.Equal("Новелла с пробелами", (await fixture.LoadAsync()).Title); Assert.Equal(performance, File.ReadAllText(Path.Combine(project.Path, "engine", "performance.json"))); Assert.False(File.Exists(Path.Combine(project.Path, "engine", "build.json")));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(1500, 950)] [InlineData(1100, 700)]
    public async Task RemoteDebugTab_FileAndBlockSelectorsAddActualBreakpointsWithoutChangingSources(double width, double height)
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "child"; fixture.Write("game/scenes/child.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); string before = File.ReadAllText(project.MainScenePath);
        await WorkspaceTabsTests.Sta(async () =>
        {
            await using var view = new RemoteDebugView(project); var host = new Window { Content = view }; EditorTheme.Apply(host, "kne");
            view.Files.SelectedItem = "game/scenes/main.krl"; view.Line.Text = "2"; await view.AddBreakpointAsync();
            view.Files.SelectedItem = "game/scenes/child.knegraph"; Assert.Equal(Visibility.Visible, view.Nodes.Visibility); await view.AddBreakpointAsync();
            Assert.Equal(2, view.Breakpoints.Items.Count); Assert.Equal(new DebugBreakpoint("game/scenes/main.krl", 2), view.Breakpoints.Items[0]); Assert.NotNull(Assert.IsType<DebugBreakpoint>(view.Breakpoints.Items[1]).NodeId);
            view.Measure(new Size(width - 300, height - 230)); view.Arrange(new Rect(0, 0, width - 300, height - 230)); view.UpdateLayout();
            Fits(view.Files, view); Fits(view.Nodes, view); Fits(view.Breakpoints, view); Fits(view.Variables, view);
            if (Environment.GetEnvironmentVariable("KNE_BUILD_RENDER") is { } directory) Render(view, directory, "remote-debug-" + width);
            Assert.Equal(before, File.ReadAllText(project.MainScenePath));
            host.Close();
        });
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(Window window, double width, double height) { var content = (FrameworkElement)window.Content; content.Measure(new Size(width - 20, height - 40)); content.Arrange(new Rect(0, 0, width - 20, height - 40)); content.UpdateLayout(); }
    private static T Parent<T>(DependencyObject child) where T : DependencyObject { for (var item = LogicalTreeHelper.GetParent(child); item != null; item = LogicalTreeHelper.GetParent(item)) if (item is T match) return match; throw new Exception("Missing parent"); }
    private static void Fits(FrameworkElement child, FrameworkElement root) { Assert.True(child.ActualWidth > 0); Assert.True(child.ActualHeight > 0); var bounds = child.TransformToAncestor(root).TransformBounds(new Rect(child.RenderSize)); Assert.InRange(bounds.Left, -.1, root.ActualWidth); Assert.InRange(bounds.Top, -.1, root.ActualHeight); Assert.InRange(bounds.Right, 0, root.ActualWidth + .1); Assert.InRange(bounds.Bottom, 0, root.ActualHeight + .1); }
    private static void Capture(Window window, double width, double height, string page)
    {
        if (Environment.GetEnvironmentVariable("KNE_BUILD_RENDER") is not { } directory) return;
        Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap((int)width - 20, (int)height - 40, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var context = background.RenderOpen()) context.DrawRectangle(window.Background, null, new Rect(0, 0, width - 20, height - 40));
        bitmap.Render(background); bitmap.Render((Visual)window.Content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, page + "-" + width + ".png")); encoder.Save(stream);
    }
    private static void Render(FrameworkElement content, string directory, string name) { Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, name + ".png")); png.Save(stream); }
}
