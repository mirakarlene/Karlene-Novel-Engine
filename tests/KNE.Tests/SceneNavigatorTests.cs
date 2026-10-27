using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class SceneNavigatorTests
{
    [Fact]
    public async Task Index_LinksCodeAndGraphs_ColorsUnusedAndBrokenFiles_AndNeverExecutesProjectCode()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene('chapter') end");
        var graph = VisualScript.Create(); graph.FunctionName = "chapter";
        graph.Nodes.Single(node => node.Id == "dialogue").Kind = ScriptNodeKind.ConditionalGoto;
        graph.Nodes.Single(node => node.Id == "dialogue").Path = "finale";
        fixture.Write("chapter.knegraph", graph.Serialize());
        fixture.Write("finale.krl", "function finale() end");
        fixture.Write("unused.krl", "function unused() end");
        fixture.Write("bad.krl", "function broken() local value = ) end");
        fixture.Write("bad.knegraph", "{ broken JSON");
        fixture.Write("missing.krl", "function missing_target() goto_scene('absent') end");
        string marker = Path.Combine(fixture.ProjectPath, "must-not-exist.txt");
        fixture.Write("danger.krl", "local f=io.open('" + marker.Replace('\\', '/') + "','w'); f:write('executed'); f:close(); function dangerous() end");
        fixture.Write("library.lua", "function lua_library() end");
        var index = await SceneNavigationIndex.BuildAsync(await fixture.LoadAsync());
        Assert.False(File.Exists(marker)); Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "engine/frame-ids.json")));
        Assert.DoesNotContain(index.Nodes, node => node.File.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));
        Assert.All(index.Nodes.Where(node => node.Function is "main" or "chapter" or "finale"), node => { Assert.True(node.Reachable); Assert.Empty(node.Errors); });
        var main = Node("main"); var chapter = Node("chapter"); var finale = Node("finale");
        Assert.Contains(index.Links, link => link.Source == main.Id && link.Target == chapter.Id);
        Assert.Contains(index.Links, link => link.Source == chapter.Id && link.Target == finale.Id);
        Assert.False(Node("unused").Reachable); Assert.Empty(Node("unused").Errors);
        Assert.NotEmpty(Node("broken").Errors); Assert.NotEmpty(Assert.Single(index.Nodes, node => node.File == "bad.knegraph").Errors);
        Assert.Contains(Node("missing_target").Errors, error => error.Contains("absent", StringComparison.Ordinal));
        SceneNavigationNode Node(string name) => Assert.Single(index.Nodes, node => node.Function == name);
    }

    [Fact]
    public async Task Index_UsesUnsavedSources_HandlesCyclesAndSubsystemPaths_AndReportsDuplicatesPerFile()
    {
        using var fixture = new TempProject();
        string mainPath = fixture.Write("game/scenes/main.krl", "function main() call_scene('old_scene') end");
        fixture.Write("old.krl", "function old_scene() end");
        var graph = VisualScript.Create(); graph.FunctionName = "child";
        graph.Nodes.Single(node => node.Id == "dialogue").Kind = ScriptNodeKind.Subsystem;
        graph.Nodes.Single(node => node.Id == "dialogue").Path = "nested.knegraph";
        fixture.Write("child.knegraph", graph.Serialize()); graph.FunctionName = "nested";
        var transfer = graph.Nodes.Single(node => node.Id == "dialogue"); transfer.Kind = ScriptNodeKind.CallScene; transfer.Path = "main";
        fixture.Write("nested.knegraph", graph.Serialize());
        fixture.Write("a.krl", "function duplicate() end"); fixture.Write("b.krl", "function duplicate() end");
        var index = await SceneNavigationIndex.BuildAsync(await fixture.LoadAsync(), new Dictionary<string, string> { [mainPath] = "function main() goto_scene('child') end" });
        Assert.Contains("old_scene", File.ReadAllText(mainPath));
        Assert.False(Assert.Single(index.Nodes, node => node.Function == "old_scene").Reachable);
        Assert.True(Assert.Single(index.Nodes, node => node.Function == "nested").Reachable);
        Assert.Contains(index.Links, link => link.Kind == "Подсистема");
        var duplicates = index.Nodes.Where(node => node.Function == "duplicate").ToArray(); Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, node => Assert.Contains(node.Errors, message => message.Contains("Повторное имя", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Index_IgnoresCommentsStringsMethodsAndShadowedCalls_AndCountsComputedDestinations()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            -- call_scene('decoy')
            local text="goto_scene('decoy')"
            function shadow(call_scene) call_scene('decoy') end
            function main()
                object:call_scene('decoy')
                goto_scene(state.destination .. '_scene')
                call_scene([=[real]=])
            end
            """);
        fixture.Write("real.krl", "function real() end"); fixture.Write("decoy.krl", "function decoy() end");
        var index = await SceneNavigationIndex.BuildAsync(await fixture.LoadAsync());
        Assert.True(Assert.Single(index.Nodes, node => node.Function == "real").Reachable);
        Assert.False(Assert.Single(index.Nodes, node => node.Function == "decoy").Reachable);
        Assert.Single(index.Links); Assert.Equal(1, index.DynamicReferences);
    }

    [Theory]
    [InlineData(1500, 950)]
    [InlineData(1100, 700)]
    public async Task Workspace_NavigatorFollowsTranslationsAtSameHeight_OpensSourceAtFunction_AndPreservesTabs(int width, int height)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() call_scene('second');goto_scene('visual') end");
        string source = fixture.Write("second.krl", "function earlier() end\n\nfunction second() end");
        fixture.Write("broken.krl", "function broken() local value=) end");
        var graph = VisualScript.Create(); graph.FunctionName = "visual";
        string graphPath = fixture.Write("visual.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                Layout();
                var toolbar = Find<WrapPanel>("PluginToolbar");
                var buttons = toolbar.Children.OfType<Button>().ToArray();
                int translation = Array.FindIndex(buttons, button => Equals(button.Content, "Диалоги / переводы"));
                Assert.True(translation >= 0);
                var navigation = buttons[translation + 1]; Assert.Equal("Навигатор сцен", navigation.Content);
                Assert.Equal(buttons[translation].ActualHeight, navigation.ActualHeight, 2);
                var before = buttons[translation].TranslatePoint(new Point(), toolbar); var after = navigation.TranslatePoint(new Point(), toolbar);
                Assert.Equal(before.Y, after.Y, 2); Assert.True(after.X >= before.X + buttons[translation].ActualWidth);
                await Action(() => navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))); Layout();
                var workspace = Find<Grid>("EditorWorkspace"); var view = Assert.Single(workspace.Children.OfType<SceneNavigatorView>());
                Assert.Equal(Visibility.Visible, view.Visibility); Assert.NotNull(view.Index);
                Assert.True(view.ZoomWithWheel(120, ModifierKeys.Control, new(180, 120))); Layout();
                Capture((FrameworkElement)window.Content, "navigator-" + width + ".png", width, height);
                var second = Assert.Single(view.Index!.Nodes, node => node.Function == "second");
                var sceneCanvas = Assert.IsType<Canvas>(((DockPanel)view.Content).Children.OfType<ScrollViewer>().Single().Content);
                var card = Assert.Single(sceneCanvas.Children.OfType<Border>(), border => Equals(border.Tag, second));
                var doubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(doubleClick, 2);
                await Action(() => card.RaiseEvent(doubleClick));
                var tabs = Find<TabControl>("DocumentTabs");
                var editor = Assert.IsType<ScriptEditor>(((TabItem)tabs.SelectedItem).Tag);
                Assert.Equal(source, editor.Document!.FilePath); Assert.Equal(3, FindEditor<KrlCodeEditor>(editor, "CodeEditor").TextArea.Caret.Line);
                Assert.Equal(Visibility.Collapsed, view.Visibility);
                await Action(() => navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))); Assert.Equal(2, tabs.Items.Count);
                await WorkspaceTabsTests.Invoke(window, "OpenNavigatedSceneAsync", Assert.Single(view.Index.Nodes, node => node.Function == "visual"));
                Assert.Equal(graphPath, Assert.IsType<ScriptEditor>(((TabItem)tabs.SelectedItem).Tag).Document!.FilePath);
                Assert.Equal(3, tabs.Items.Count);
                T Find<T>(string name) where T : class => WorkspaceTabsTests.Find<T>(window, name);
                void Layout() { var content = (FrameworkElement)window.Content; content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout(); }
                async Task Action(Action action)
                {
                    var panel = Find<Grid>("ProjectPanel");
                    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    DependencyPropertyChangedEventHandler handler = (_, args) => { if (args.NewValue is true) completed.TrySetResult(); };
                    panel.IsEnabledChanged += handler;
                    try { action(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                    finally { panel.IsEnabledChanged -= handler; }
                    Assert.Equal("", Find<TextBlock>("OperationStatus").Text);
                }
            }
            finally
            {
                foreach (var editor in FindEditors(window)) editor.ConfirmSave = _ => MessageBoxResult.No;
                window.GetType().GetMethod("ClearEditorTabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null);
                window.Close();
            }
        });
    }

    private static IEnumerable<ScriptEditor> FindEditors(MainWindow window) => WorkspaceTabsTests.Find<Grid>(window, "EditorWorkspace").Children.OfType<ScriptEditor>();
    private static T FindEditor<T>(FrameworkElement editor, string name) where T : class => WorkspaceTabsTests.Find<T>(editor, name);
    private static void Capture(FrameworkElement content, string name, int width, int height)
    {
        string? output = Environment.GetEnvironmentVariable("KNE_INLINE_NAV_RENDER"); if (output == null) return;
        Directory.CreateDirectory(output); var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
}
