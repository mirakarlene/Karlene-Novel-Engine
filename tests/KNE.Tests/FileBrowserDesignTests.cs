using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class FileBrowserDesignTests
{
    [Fact]
    public Task DoubleClick_UsesTheActualOwnedRowInsteadOfSelection_AndIgnoresBlankHeadersControlsAndForeignRows() => WorkspaceTabsTests.Sta(() =>
    {
        var first = new ProjectFile { FilePath = "first.krl" }; var second = new ProjectFile { FilePath = "second.knegraph" };
        var grid = Grid(first, second); var owner = new Window { Content = grid };
        var foreign = Grid(new ProjectFile { FilePath = "foreign.krl" }); var foreignOwner = new Window { Content = foreign };
        try
        {
            EditorTheme.Apply(owner, "dark"); Layout(grid); Layout(foreign); grid.SelectedItem = first;
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(second));
            var cell = Descendants(row).OfType<DataGridCell>().First();
            var text = Assert.Single(Descendants(cell).OfType<TextBlock>());
            Assert.Same(second, MainWindow.FileAtDoubleClick(grid, text)); Assert.Same(first, grid.SelectedItem);
            Assert.Same(second, MainWindow.FileAtDoubleClick(grid, row));
            Assert.Null(MainWindow.FileAtDoubleClick(grid, grid));
            Assert.Null(MainWindow.FileAtDoubleClick(grid, Descendants(grid).OfType<DataGridColumnHeader>().First()));
            Assert.Null(MainWindow.FileAtDoubleClick(grid, foreign.ItemContainerGenerator.ContainerFromIndex(0)));
            Assert.Null(MainWindow.FileAtDoubleClick(grid, null));
            foreach (var interactive in new Control[] { new Button { Content = "action" }, new ComboBox { ItemsSource = new[] { "action" } }, new TextBox { Text = "edit" } })
            {
                cell.Content = interactive; Layout(grid);
                Assert.Null(MainWindow.FileAtDoubleClick(grid, interactive));
                var child = Descendants(interactive).OfType<TextBlock>().FirstOrDefault();
                if (child != null) Assert.Null(MainWindow.FileAtDoubleClick(grid, child));
            }
        }
        finally { owner.Close(); foreignOwner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task DoubleClick_TreeOpensOnlyTheClickedFileHeader_AndRejectsFoldersAndAnotherTreesItems() => WorkspaceTabsTests.Sta(() =>
    {
        var file = new ProjectFile { FilePath = "chapter.krl" };
        var fileNode = new ProjectTreeNode { Name = "chapter.krl", RelativePath = "chapter.krl", File = file };
        var folder = new ProjectTreeNode { Name = "folder", RelativePath = "folder" };
        var template = new DataTemplate(); var visual = new FrameworkElementFactory(typeof(TextBlock)); visual.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); template.VisualTree = visual;
        var tree = new TreeView { ItemsSource = new[] { folder, fileNode }, ItemTemplate = template }; var owner = new Window { Content = tree };
        var another = new TreeView { ItemsSource = new[] { fileNode } }; var otherOwner = new Window { Content = another };
        try
        {
            Layout(tree); Layout(another);
            var item = Assert.IsType<TreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(fileNode));
            var label = Assert.Single(Descendants(item).OfType<TextBlock>(), text => text.Text == fileNode.Name);
            Assert.Same(file, MainWindow.FileAtDoubleClick(tree, label));
            Assert.Null(MainWindow.FileAtDoubleClick(tree, tree));
            Assert.Null(MainWindow.FileAtDoubleClick(tree, tree.ItemContainerGenerator.ContainerFromItem(folder)));
            Assert.Null(MainWindow.FileAtDoubleClick(tree, another.ItemContainerGenerator.ContainerFromItem(fileNode)));
        }
        finally { owner.Close(); otherOwner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task FileTable_UsesThemedThinColumnHeadersNoRowSelectorsAndExactlyFiveActions_AndSearchHasARealPlaceholder()
    {
        using var fixture = new TempProject(); fixture.Write("chapter.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); Layout((FrameworkElement)window.Content, new Size(1460, 900));
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Layout((FrameworkElement)window.Content, new Size(1460, 900));
                var table = WorkspaceTabsTests.Find<DataGrid>(window, "FilesDataGrid");
                typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new UserSettings { ProjectBrowserView = "table" });
                typeof(MainWindow).GetMethod("ApplyBrowserView", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Assert.Equal(DataGridHeadersVisibility.Column, table.HeadersVisibility); Assert.Equal(0, table.RowHeaderWidth);
                var search = WorkspaceTabsTests.Find<TextBox>(window, "FileSearch");
                Assert.Equal("", search.Text);
                var placeholder = Assert.Single(Descendants((DependencyObject)search.Parent).OfType<TextBlock>(), text => text.Text == "Поиск...");
                Assert.False(placeholder.IsHitTestVisible); Assert.InRange(placeholder.Opacity, .01, .99); Assert.Equal(Visibility.Visible, placeholder.Visibility);
                search.Text = "chapter"; Layout((FrameworkElement)window.Content);
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
                search.Clear(); Layout((FrameworkElement)window.Content); Assert.Equal(Visibility.Visible, placeholder.Visibility);
                foreach (string theme in new[] { "dark", "light", "rose" })
                {
                    EditorTheme.Apply(window, theme); Layout((FrameworkElement)window.Content);
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); Layout((FrameworkElement)window.Content);
                    var headers = Descendants(table).OfType<DataGridColumnHeader>().Where(header => header.Column != null).ToArray(); Assert.Equal(4, headers.Length);
                    foreach (var header in headers)
                    {
                        Assert.Equal(WelcomeOptionsTests.BrushColor(window.FindResource("Editor.Surface")), WelcomeOptionsTests.BrushColor(header.Background));
                        Assert.Equal(WelcomeOptionsTests.BrushColor(window.FindResource("Editor.Text")), WelcomeOptionsTests.BrushColor(header.Foreground));
                        Assert.True(header.BorderThickness.Bottom > 0 && header.BorderThickness.Bottom <= 1);
                        Assert.InRange(header.ActualWidth, 50, table.ActualWidth);
                        Assert.Contains(Descendants(header).OfType<TextBlock>(), text => text.Text == header.Column.Header.ToString() && text.ActualWidth > 0);
                    }
                    var combos = Descendants(table).OfType<ComboBox>().Where(combo => combo.Tag is ProjectFile).ToArray(); Assert.NotEmpty(combos);
                    foreach (var combo in combos)
                    {
                        Assert.Equal(new[] { "Редактировать", "Переименовать", "Дублировать", "Переместить", "Удалить" }, combo.Items.Cast<ComboBoxItem>().Select(item => (string)item.Content));
                        Assert.InRange(combo.ActualWidth, 80, table.ActualWidth);
                    }
                    var rows = Descendants(table).OfType<DataGridRow>().ToArray(); Assert.NotEmpty(rows);
                    foreach (var row in rows)
                    {
                        Assert.All(Descendants(row).OfType<DataGridCell>(), cell => Assert.InRange(cell.ActualWidth, 50, table.ActualWidth));
                        string fileName = Assert.IsType<ProjectBrowserEntry>(row.Item).Name;
                        Assert.Contains(Descendants(row).OfType<TextBlock>(), text => text.Text == fileName && text.ActualWidth > 0 && text.ActualHeight > 0);
                    }
                    Assert.DoesNotContain(Descendants(table).OfType<DataGridRowHeader>(), header => header.ActualWidth > 0 && header.Visibility == Visibility.Visible);
                }
                Capture((FrameworkElement)window.Content, "file-table.png", new Size(1460, 900));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task FileActions_EditOpensCodeAndGraphInInternalTabsAndAssetsInConfiguredExternalEditor_AndResetsActionSelection()
    {
        using var fixture = new TempProject();
        string code = fixture.Write("extra.krl", "function extra() end");
        string graph = fixture.Write("extra.knegraph", VisualScript.Create().Serialize());
        string asset = fixture.Write("notes.txt", "plain text"); string editorProgram = fixture.Write("configured editor.exe", "stub");
        await WorkspaceTabsTests.Sta(async () =>
        {
            var launches = new List<System.Diagnostics.ProcessStartInfo>(); var window = new MainWindow(null, null, null, launches.Add);
            typeof(MainWindow).GetField("settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, new UserSettings { EditorPath = editorProgram, ProjectBrowserView = "table" });
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                foreach (string path in new[] { code, graph })
                {
                    var table = WorkspaceTabsTests.Find<DataGrid>(window, "FilesDataGrid");
                    table.Visibility = Visibility.Visible; FileBrowserDesignTests.Layout((FrameworkElement)window.Content);
                    typeof(MainWindow).GetMethod("ShowFileBrowser", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                    var file = table.Items.Cast<ProjectBrowserEntry>().Single(entry => entry.File?.FilePath == path); table.ScrollIntoView(file); table.UpdateLayout();
                    var row = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromItem(file));
                    var action = Assert.Single(Descendants(row).OfType<ComboBox>());
                    action.SelectedItem = action.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Content, "Редактировать"));
                    Assert.Null(action.SelectedItem);
                    var tabs = WorkspaceTabsTests.Find<TabControl>(window, "DocumentTabs");
                    for (int attempt = 0; attempt < 150; attempt++)
                    {
                        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        if (tabs.SelectedItem is TabItem tab && tab.Tag is ScriptEditor editor && editor.Document?.FilePath == path) break;
                        await Task.Delay(10);
                    }
                    var selected = Assert.IsType<ScriptEditor>(Assert.IsType<TabItem>(tabs.SelectedItem).Tag);
                    Assert.Equal(path, selected.Document!.FilePath); Assert.Equal(path == graph, selected.Document.IsVisual);
                    Assert.Empty(launches);
                }
                var files = WorkspaceTabsTests.Find<DataGrid>(window, "FilesDataGrid"); files.Visibility = Visibility.Visible;
                FileBrowserDesignTests.Layout((FrameworkElement)window.Content);
                typeof(MainWindow).GetMethod("ShowFileBrowser", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var assetFile = files.Items.Cast<ProjectBrowserEntry>().Single(entry => entry.File?.FilePath == asset); files.ScrollIntoView(assetFile); files.UpdateLayout();
                var assetRow = Assert.IsType<DataGridRow>(files.ItemContainerGenerator.ContainerFromItem(assetFile));
                var assetAction = Assert.Single(Descendants(assetRow).OfType<ComboBox>());
                assetAction.SelectedItem = assetAction.Items.Cast<ComboBoxItem>().Single(item => Equals(item.Content, "Редактировать"));
                for (int attempt = 0; attempt < 150 && launches.Count == 0; attempt++)
                { await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); await Task.Delay(10); }
                var launch = Assert.Single(launches); Assert.Equal(editorProgram, launch.FileName); Assert.Equal(new[] { asset }, launch.ArgumentList);
                Assert.False(launch.UseShellExecute); Assert.Null(assetAction.SelectedItem);
                Assert.Equal(2, WorkspaceTabsTests.Find<TabControl>(window, "DocumentTabs").Items.Count);
            }
            finally
            {
                var tabs = WorkspaceTabsTests.Find<TabControl>(window, "DocumentTabs");
                foreach (TabItem tab in tabs.Items.Cast<TabItem>().ToArray()) await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", (ScriptEditor)tab.Tag);
                window.Close();
            }
        });
    }

    [Fact]
    public Task HiddenScrollbars_ShiftWheelReachesBothHorizontalEdgesWithoutHijackingOrdinaryVerticalWheel() => WorkspaceTabsTests.Sta(() =>
    {
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Width = 1300, Height = 1400 } };
        var owner = new Window { Content = viewer };
        try
        {
            EditorTheme.Apply(owner, "dark"); Layout(viewer, new Size(300, 200));
            Assert.True(ScrollNavigation.GetEnabled(viewer)); Assert.False(ScrollNavigation.TryScrollHorizontal(viewer, -120, false));
            Assert.False(ScrollNavigation.TryScrollHorizontal(viewer, 0, true)); Assert.Equal(0, viewer.HorizontalOffset);
            Assert.True(ScrollNavigation.TryScrollHorizontal(viewer, -120, true)); viewer.UpdateLayout(); Assert.Equal(120, viewer.HorizontalOffset);
            Assert.True(ScrollNavigation.TryScrollHorizontal(viewer, -10000, true)); viewer.UpdateLayout(); Assert.Equal(viewer.ScrollableWidth, viewer.HorizontalOffset);
            Assert.False(ScrollNavigation.TryScrollHorizontal(viewer, -120, true));
            Assert.True(ScrollNavigation.TryScrollHorizontal(viewer, 10000, true)); viewer.UpdateLayout(); Assert.Equal(0, viewer.HorizontalOffset);
            Assert.False(ScrollNavigation.TryScrollHorizontal(viewer, 120, true));
            ((Border)viewer.Content).Height = 50; Layout(viewer, new Size(300, 200));
            Assert.Equal(0, viewer.ScrollableHeight); Assert.True(ScrollNavigation.TryScrollHorizontal(viewer, -120, false)); viewer.UpdateLayout(); Assert.Equal(120, viewer.HorizontalOffset);
            ((Border)viewer.Content).Width = 50; Layout(viewer, new Size(300, 200));
            Assert.False(ScrollNavigation.TryScrollHorizontal(viewer, -120, true));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScrollChrome_RemovesBothBarsWithoutBreakingProgrammaticScrollingOrTextEditing() => WorkspaceTabsTests.Sta(() =>
    {
        var viewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            Content = new Border { Width = 1300, Height = 1400, Background = Brushes.Red } };
        var owner = new Window { Content = viewer };
        try
        {
            EditorTheme.Apply(owner, "kne"); Layout(viewer, new Size(300, 200));
            Assert.True(viewer.ScrollableHeight > 1000); Assert.True(viewer.ScrollableWidth > 900);
            var bars = Descendants(viewer).OfType<ScrollBar>().ToArray(); Assert.Equal(2, bars.Length);
            foreach (var bar in bars) { Assert.Equal(0, bar.ActualWidth); Assert.Equal(0, bar.ActualHeight); Assert.Empty(Descendants(bar).OfType<Thumb>()); }
            viewer.ScrollToVerticalOffset(500); viewer.ScrollToHorizontalOffset(300); viewer.UpdateLayout();
            Assert.Equal(500, viewer.VerticalOffset); Assert.Equal(300, viewer.HorizontalOffset);
            var text = new TextBox { AcceptsReturn = true, Text = string.Join("\n", Enumerable.Range(1, 100)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            owner.Content = text; Layout(text, new Size(300, 150)); text.CaretIndex = text.Text.Length; text.ScrollToEnd(); text.UpdateLayout();
            Assert.True(text.VerticalOffset > 0); Assert.Contains("100", text.Text); Assert.All(Descendants(text).OfType<ScrollBar>(), bar => Assert.True(bar.ActualWidth == 0 || bar.ActualHeight == 0));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ScrollChrome_CodeAndStandaloneGameErrorKeepLongContentReachableWithoutScrollbarChrome()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(() =>
        {
            string content = string.Join("\n", Enumerable.Range(1, 100).Select(index => $"-- line {index} " + new string('x', 200)));
            var code = new KrlCodeEditor { Text = content }; var owner = new Window { Content = code };
            var game = new GameWindow(project);
            try
            {
                EditorTheme.Apply(owner, "dark"); Layout(code, new Size(400, 180));
                var scroller = Assert.Single(Descendants(code).OfType<ScrollViewer>());
                Assert.True(scroller.ScrollableHeight > 0); Assert.True(scroller.ScrollableWidth > 0);
                scroller.ScrollToBottom(); scroller.ScrollToRightEnd(); code.UpdateLayout();
                Assert.True(scroller.VerticalOffset > 0); Assert.True(scroller.HorizontalOffset > 0); Assert.Equal(content, code.Text);
                var codeBars = Descendants(code).OfType<ScrollBar>().ToArray(); Assert.NotEmpty(codeBars);
                Assert.All(codeBars, bar => { Assert.Equal(0, bar.ActualWidth); Assert.Equal(0, bar.ActualHeight); });
                // No EditorTheme.Apply: the standalone player must load the shared chrome policy itself.
                WorkspaceTabsTests.Find<Border>(game, "StartupErrorOverlay").Visibility = Visibility.Visible;
                var error = WorkspaceTabsTests.Find<TextBox>(game, "StartupErrorMessage"); error.Text = content;
                Layout((FrameworkElement)game.Content, new Size(800, 600)); error.ScrollToEnd(); error.UpdateLayout();
                Assert.True(error.VerticalOffset > 0); Assert.Equal(content, error.Text);
                var gameBars = Descendants(error).OfType<ScrollBar>().ToArray(); Assert.NotEmpty(gameBars);
                Assert.All(gameBars, bar => { Assert.Equal(0, bar.ActualWidth); Assert.Equal(0, bar.ActualHeight); });
            }
            finally { owner.Close(); game.Close(); }
            return Task.CompletedTask;
        });
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root) => AccessibilityPresentationTests.Descendants(root);
    internal static void Layout(FrameworkElement view, Size? size = null) { var bounds = size ?? new Size(1460, 900); view.Measure(bounds); view.Arrange(new Rect(bounds)); view.UpdateLayout(); }
    internal static void Capture(FrameworkElement view, string name, Size size)
    {
        string? output = Environment.GetEnvironmentVariable("KNE_DESIGN_RENDER"); if (output == null) return;
        Directory.CreateDirectory(output); Layout(view, size);
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
    private static DataGrid Grid(params ProjectFile[] files)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = files, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Имя", Binding = new System.Windows.Data.Binding("FileName") }); return grid;
    }
}
