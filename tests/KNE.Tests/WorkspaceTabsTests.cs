using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class WorkspaceTabsTests
{
    [Fact]
    public async Task Tabs_RetainIndependentDraftsUndoAndViewport_AndFocusAlreadyOpenFile()
    {
        using var fixture = new TempProject();
        string codePath = fixture.Write("other.krl", "function other() end");
        var graph = VisualScript.Create(); graph.FunctionName = "chapter";
        string graphPath = fixture.Write("chapter.knegraph", graph.Serialize());
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await Invoke(window, "OpenScriptAsync", codePath);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var codeEditor = Editor(tabs, 0);
                var code = Find<KrlCodeEditor>(codeEditor, "CodeEditor");
                code.CaretIndex = code.Text.Length; code.SelectedText = " -- draft";
                Assert.Contains("other.krl *", Header((TabItem)tabs.Items[0]));
                await Invoke(window, "OpenScriptAsync", graphPath);
                var visual = Editor(tabs, 1);
                visual.SelectGraphBlocks(["dialogue"]);
                Find<TextBox>(visual, "SceneTextInput").Text = "Draft scene";
                visual.PanGraph(-257, 136); visual.ChangeZoom(-2);
                await Invoke(window, "OpenScriptAsync", codePath.ToUpperInvariant());
                Assert.Equal(2, tabs.Items.Count); Assert.Same(codeEditor, ((TabItem)tabs.SelectedItem).Tag);
                Assert.Equal("function other() end -- draft", code.Text);
                Find<Button>(codeEditor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("function other() end", code.Text);
                code.CaretIndex = code.Text.Length; code.SelectedText = " -- second edit";
                tabs.SelectedIndex = 1;
                Assert.Equal("Draft scene", Find<TextBox>(visual, "SceneTextInput").Text);
                Assert.Equal(.8, Find<Slider>(visual, "ZoomSlider").Value, 3);
                var transform = (TransformGroup)Find<Canvas>(visual, "GraphCanvas").RenderTransform;
                Assert.Equal(-257, transform.Children.OfType<TranslateTransform>().Single().X);
                Assert.Equal(136, transform.Children.OfType<TranslateTransform>().Single().Y);
                Assert.Equal("dialogue", Assert.Single(visual.SelectedBlockIds));
                Assert.Contains("chapter.knegraph *", Header((TabItem)tabs.Items[1]));
                await Invoke(window, "SaveAllEditorsAsync");
                Assert.Equal("function other() end -- second edit", File.ReadAllText(codePath));
                Assert.Equal("Draft scene", VisualScript.Parse(File.ReadAllText(graphPath)).Nodes.Single(node => node.Id == "dialogue").Text);
                Assert.False(visual.HasUnsavedChanges);
                Assert.DoesNotContain("*", Header((TabItem)tabs.Items[1]));
                var search = Find<TextBox>(window, "FileSearch");
                Assert.Equal(0, Grid.GetColumn(search)); Assert.Equal(1, Grid.GetColumnSpan(search));
                Assert.Equal(2, Grid.GetColumn(tabs));
                Capture(window, "workspace-tabs.png");
            }
            finally { await Cleanup(window); }
        });
    }

    [Theory]
    [InlineData(MessageBoxResult.Cancel, 2, false)]
    [InlineData(MessageBoxResult.No, 1, false)]
    [InlineData(MessageBoxResult.Yes, 1, true)]
    public async Task Tabs_ClosePromptsForTheCorrectFile_AndHonorsSaveDiscardCancel(MessageBoxResult answer, int count, bool saved)
    {
        using var fixture = new TempProject();
        string first = fixture.Write("first.krl", "function first() end"), second = fixture.Write("second.lua", "function second() end");
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await Invoke(window, "OpenScriptAsync", first); await Invoke(window, "OpenScriptAsync", second);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var editor = Editor(tabs, 0); string? prompted = null;
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function first() -- saved\nend";
                editor.ConfirmSave = path => { prompted = path; return answer; };
                await Invoke(window, "CloseEditorTabAsync", editor);
                Assert.Equal(first, prompted); Assert.Equal(count, tabs.Items.Count);
                Assert.Equal(saved ? "function first() -- saved\nend" : "function first() end", File.ReadAllText(first));
                Assert.Equal(second, ((ScriptEditor)((TabItem)tabs.SelectedItem).Tag).Document!.FilePath);
                if (answer == MessageBoxResult.Cancel) Assert.True(editor.HasUnsavedChanges);
            }
            finally { await Cleanup(window); }
        });
    }

    [Fact]
    public async Task Tabs_FailedSaveKeepsDraftAndTab_AndAllDocumentsParticipateInLeaving()
    {
        using var fixture = new TempProject(); string path = fixture.Write("one.krl", "function one() end");
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await Invoke(window, "OpenScriptAsync", path);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var editor = Editor(tabs, 0);
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "draft";
                await Invoke(window, "OpenScriptAsync", fixture.Write("two.krl", "function two() end"));
                editor.ConfirmSave = _ => MessageBoxResult.Cancel;
                Assert.False(await (Task<bool>)typeof(MainWindow).GetMethod("CanLeaveAllAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!);
                editor.ConfirmSave = _ => MessageBoxResult.Yes; File.WriteAllText(path, "external");
                await Assert.ThrowsAsync<IOException>(() => Invoke(window, "CloseEditorTabAsync", editor));
                Assert.Equal(2, tabs.Items.Count); Assert.Equal("draft", editor.Document!.Code); Assert.True(editor.HasUnsavedChanges);
                Assert.Equal("external", File.ReadAllText(path));
            }
            finally { await Cleanup(window); }
        });
    }

    [Fact]
    public async Task Tabs_ProjectSwitchHonorsCancelThenClearsDrafts_AndFinalCloseRestoresFileList()
    {
        using var first = new TempProject(); using var second = new TempProject();
        string path = first.Write("other.krl", "function other() end");
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", first.ProjectPath, false);
                await Invoke(window, "OpenScriptAsync", path);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var editor = Editor(tabs, 0);
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "draft";
                await Invoke(window, "OpenScriptAsync", Path.Combine(first.ProjectPath, "game", "scenes", "main.krl"));
                editor.ConfirmSave = _ => MessageBoxResult.Cancel;
                await Invoke(window, "OpenProjectAsync", second.ProjectPath, false);
                Assert.Equal(2, tabs.Items.Count); Assert.Equal("draft", editor.Document!.Code);
                editor.ConfirmSave = _ => MessageBoxResult.No;
                await Invoke(window, "OpenProjectAsync", second.ProjectPath, false);
                Assert.Empty(tabs.Items); Assert.Null(editor.Document);
                Assert.Equal("function other() end", File.ReadAllText(path));
                await Invoke(window, "OpenScriptAsync", Path.Combine(second.ProjectPath, "game", "scenes", "main.krl"));
                await Invoke(window, "CloseEditorTabAsync", Editor(tabs, 0));
                Assert.Empty(tabs.Items);
                Assert.Equal(Visibility.Collapsed, tabs.Visibility);
                Assert.Equal(Visibility.Collapsed, Find<Grid>(window, "EditorWorkspace").Visibility);
                Assert.Equal(Visibility.Visible, Find<Grid>(window, "FileBrowserPanel").Visibility);
                Assert.Equal(Visibility.Visible, Find<ListBox>(window, "FileTiles").Visibility);
            }
            finally { await Cleanup(window); }
        });
    }

    [Fact]
    public async Task Tree_SaveAndRefreshRetainExpansionSelectionAndCollapsedFolders()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/side.krl", "function side() end");
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                var tree = Find<TreeView>(window, "ProjectFilesTree");
                Layout(window);
                var game = Container(tree, "game"); game.IsExpanded = true; Layout(window);
                var scenes = Container(game, "scenes"); scenes.IsExpanded = true; Layout(window);
                var scripts = Container(game, "scripts"); scripts.IsExpanded = true; scripts.IsExpanded = false;
                Container(scenes, "side.krl").IsSelected = true;
                await Invoke(window, "OpenScriptAsync", path);
                var editor = Editor(Find<TabControl>(window, "DocumentTabs"), 0);
                Find<KrlCodeEditor>(editor, "CodeEditor").Text += " -- edit";
                await editor.SaveAsync();
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                var after = Flatten(tree.ItemsSource.Cast<ProjectTreeNode>()).ToArray();
                Assert.True(after.Single(node => node.RelativePath == "game").IsExpanded);
                Assert.True(after.Single(node => node.RelativePath == Path.Combine("game", "scenes")).IsExpanded);
                Assert.False(after.Single(node => node.RelativePath == Path.Combine("game", "scripts")).IsExpanded);
                Assert.True(after.Single(node => node.File?.FilePath == path).IsSelected);
                Layout(window);
                Assert.True(Container(tree, "game").IsExpanded);
                Assert.True(Container(Container(tree, "game"), "scenes").IsExpanded);
                Assert.Equal(path, Assert.IsType<ProjectTreeNode>(tree.SelectedItem).File!.FilePath);
                Assert.Same(editor, Editor(Find<TabControl>(window, "DocumentTabs"), 0));
            }
            finally { await Cleanup(window); }
        });
    }

    private static string Header(TabItem tab) => ((StackPanel)tab.Header).Children.OfType<TextBlock>().Single().Text;
    private static ScriptEditor Editor(TabControl tabs, int index) => (ScriptEditor)((TabItem)tabs.Items[index]).Tag;
    internal static T Find<T>(FrameworkElement element, string name) where T : class => (T)element.FindName(name);
    private static IEnumerable<ProjectTreeNode> Flatten(IEnumerable<ProjectTreeNode> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
    private static TreeViewItem Container(ItemsControl parent, string name) => Assert.IsType<TreeViewItem>(
        parent.ItemContainerGenerator.ContainerFromItem(parent.Items.Cast<ProjectTreeNode>().Single(node => node.Name == name)));
    private static void Layout(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1460, 900)); content.Arrange(new Rect(0, 0, 1460, 900)); content.UpdateLayout();
    }
    private static void Capture(MainWindow window, string name)
    {
        string? output = Environment.GetEnvironmentVariable("KNE_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output); Layout(window);
        var bitmap = new RenderTargetBitmap(1460, 900, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
    internal static Task Invoke(object target, string method, params object[] args) => (Task)target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args)!;
    private static async Task Cleanup(MainWindow window)
    {
        var tabs = Find<TabControl>(window, "DocumentTabs");
        foreach (var tab in tabs.Items.Cast<TabItem>().ToArray())
        {
            var editor = (ScriptEditor)tab.Tag; editor.ConfirmSave = _ => MessageBoxResult.No;
            await Invoke(window, "CloseEditorTabAsync", editor);
        }
        window.Close();
    }
    internal static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
