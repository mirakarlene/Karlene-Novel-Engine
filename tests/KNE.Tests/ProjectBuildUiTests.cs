using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBuildUiTests
{
    [Fact]
    public async Task ReleaseAction_SavesDraftAndBlocksBuildWithoutWritingReportOnAnalysisError()
    {
        using var fixture = new TempProject();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
            ReleaseAnalysisWindow? analysis = null;
            bool? canContinue = null;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                analysis = window.OwnedWindows.OfType<ReleaseAnalysisWindow>().FirstOrDefault(dialog => dialog.IsVisible);
                if (analysis == null) return;
                canContinue = WorkspaceTabsTests.Find<Button>(analysis, "ContinueButton").IsEnabled;
                timer.Stop();
                analysis.Close();
            };
            try
            {
                window.Show();
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                string path = Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl");
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", path);
                var tabs = WorkspaceTabsTests.Find<TabControl>(window, "DocumentTabs");
                var selected = (TabItem)tabs.SelectedItem; var editor = (ScriptEditor)selected.Tag;
                const string source = "function chapter() end"; // No main: fail validation before invoking dotnet publish.
                WorkspaceTabsTests.Find<KrlCodeEditor>(editor, "CodeEditor").Text = source;
                timer.Start();
                Click(WorkspaceTabsTests.Find<Button>(window, "BuildReleaseButton"));
                var status = WorkspaceTabsTests.Find<TextBlock>(window, "OperationStatus");
                for (int i = 0; i < 500 && analysis == null; i++) await Task.Delay(10);
                Assert.NotNull(analysis);
                Assert.False(canContinue);
                Assert.Equal("Сборка остановлена из-за ошибок проекта.", status.Text);
                Assert.Equal(source, File.ReadAllText(path)); Assert.False(editor.HasUnsavedChanges);
                Assert.Same(selected, tabs.SelectedItem); Assert.Single(tabs.Items.Cast<TabItem>());
                Assert.Equal(Visibility.Collapsed, WorkspaceTabsTests.Find<Grid>(window, "FileBrowserPanel").Visibility);
                Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "engine", "build.json")));
                Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, "reports")));
            }
            finally { timer.Stop(); window.Close(); }
        });
    }

    [Theory]
    [InlineData(680, 480, "kne")]
    [InlineData(470, 330, "light")]
    public Task ReleaseAnalysis_WarningAllowsContinueAndDialogFits(double width, double height, string theme) => WorkspaceTabsTests.Sta(() =>
    {
        var owner = new Window { ShowInTaskbar = false, ShowActivated = false, Left = -2000, WindowStartupLocation = WindowStartupLocation.Manual };
        owner.Show();
        try
        {
            var info = new ProjectDiagnostic(ProjectDiagnosticSeverity.Information, "KNE601", "game/scenes/main.krl", 12, "Теоретическая угроза безопасности");
            var warning = new ProjectDiagnostic(ProjectDiagnosticSeverity.Warning, "KNE307", "game/scenes/chapter.krl", 20, "Сцена недостижима.");
            Assert.False(new ProjectAnalysisResult("Игра", DateTimeOffset.Now, [info]).RequiresBuildConfirmation);
            var result = new ProjectAnalysisResult("Игра", DateTimeOffset.Now, [info, warning]);
            Assert.True(result.RequiresBuildConfirmation);
            var dialog = new ReleaseAnalysisWindow(owner, theme, result) { Width = width, Height = height };
            dialog.Loaded += (_, _) =>
            {
                dialog.UpdateLayout();
                var cancel = WorkspaceTabsTests.Find<Button>(dialog, "CancelBuildButton");
                var collect = WorkspaceTabsTests.Find<Button>(dialog, "ContinueButton");
                Assert.True(cancel.ActualWidth >= cancel.DesiredSize.Width - cancel.Margin.Right);
                Assert.True(collect.ActualWidth >= collect.DesiredSize.Width);
                Assert.True(collect.TranslatePoint(new Point(collect.ActualWidth, collect.ActualHeight), dialog).X < dialog.ActualWidth);
                Assert.Equal(2, WorkspaceTabsTests.Find<ListBox>(dialog, "Diagnostics").Items.Count);
                Click(collect);
            };
            Assert.True(dialog.ShowDialog());
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task FourProjectPagesKeepAllDraftsAndSaveBuildMetadataAndPerformanceTogether()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new ProjectParametersWindow(project, fixture.Root);
            try
            {
                Assert.Equal(0, window.Page); Assert.False(window.PreviousPage.IsEnabled); Assert.True(window.NextPage.IsEnabled);
                Assert.Equal("vm", window.BuildInput.Value.TestMode); Assert.Equal("self_contained", window.BuildInput.Value.ReleaseMode);
                Click(window.NextPage); Assert.Equal(1, window.Page); window.SavesInput.Text = "draft/saves";
                Assert.Equal("local", window.PersistentLocationInput.SelectedValue);
                Assert.Equal(new[] { "local", "appdata" }, window.PersistentLocationInput.Items.Cast<GraphicsOption<string>>().Select(option => option.Value));
                window.PersistentLocationInput.SelectedValue = "appdata";
                Click(window.NextPage); Assert.Equal(2, window.Page); Assert.Equal(Visibility.Visible, window.BuildInput.Visibility);
                window.BuildInput.TestMode.SelectedValue = "remote_debug"; Assert.True(window.BuildInput.TestPath.IsEnabled);
                window.BuildInput.ReleaseMode.SelectedValue = "portable"; Assert.Equal("local", window.BuildInput.Value.EffectiveSaveLocation); Assert.False(window.BuildInput.Saves.IsEnabled);
                Assert.DoesNotContain(window.BuildInput.Children.OfType<Button>(), button => Equals(button.Content, "Собрать релиз"));
                Click(window.NextPage); Assert.Equal(3, window.Page); Assert.False(window.TitleInput.IsReadOnly); window.BuildInput.Author.Text = "Draft author";
                Assert.False(window.NextPage.IsEnabled); window.BuildInput.UseProjectNameForBuild.IsChecked = true;
                Assert.Empty(Descendants(window.Content).OfType<ScrollViewer>());
                for (int page = 2; page >= 0; page--) { Click(window.PreviousPage); Assert.Equal(page, window.Page); }
                Assert.Equal("draft/saves", window.SavesInput.Text); Assert.Equal("Draft author", window.BuildInput.Author.Text); Assert.Equal("remote_debug", window.BuildInput.Value.TestMode);
                Assert.False(File.Exists(Path.Combine(project.Path, "engine", "build.json")));
                await window.SaveAsync(); var saved = ProjectBuildSettingsStore.Load(project.Path); Assert.Equal("remote_debug", saved.TestMode); Assert.Equal("portable", saved.ReleaseMode); Assert.Equal("Draft author", saved.Author); Assert.Equal("draft/saves", PerformanceSettingsStore.LoadProject(project.Path).SavesPath);
                Assert.Equal("appdata", PerformanceSettingsStore.LoadProject(project.Path).PersistentLocation);
                Assert.True(saved.UseProjectNameForBuild);
            }
            finally { window.Close(); }
            var cancel = new ProjectParametersWindow(project);
            try { cancel.ShowPage(1); cancel.PersistentLocationInput.SelectedValue = "local"; cancel.ShowPage(3); cancel.BuildInput.Version.Text = "9.9"; cancel.ShowPage(4); cancel.Close(); Assert.Equal("1.0.0", ProjectBuildSettingsStore.Load(project.Path).Version); Assert.Equal("appdata", PerformanceSettingsStore.LoadProject(project.Path).PersistentLocation); }
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
    private static IEnumerable<DependencyObject> Descendants(object root)
    {
        if (root is not DependencyObject owner) yield break;
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject node) { yield return node; foreach (var nested in Descendants(node)) yield return nested; }
    }
    private static void ScrollAndFit(FrameworkElement control, ScrollViewer scroll, Window window, double width, double height)
    {
        var body = (FrameworkElement)scroll.Content;
        scroll.ScrollToVerticalOffset(Math.Max(0, control.TransformToAncestor(body).Transform(new Point()).Y - 4)); Layout(window, width, height);
        Fits(control, (FrameworkElement)window.Content);
        var bounds = control.TransformToAncestor(scroll).TransformBounds(new Rect(control.RenderSize));
        Assert.InRange(bounds.Top, -.1, scroll.ActualHeight); Assert.InRange(bounds.Bottom, 0, scroll.ActualHeight + .1);
    }
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
