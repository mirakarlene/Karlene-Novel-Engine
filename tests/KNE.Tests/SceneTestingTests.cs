using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class SceneTestingTests
{
    [Fact]
    public async Task Scene_EndBackAndRestart_ReplayChoiceLocalsAndRandomWithoutWritingGameSaves()
    {
        using var fixture = new TempProject();
        const string source = "function main() error('must not run main') end\nfunction scene() local n=math.random(100000); local a=choice('choose',{'left','right'}); d(nil,n..':'..a); d(nil,'last:'..n) end";
        fixture.Write("game/scenes/main.krl", source);
        var project = await fixture.LoadAsync();
        await using var session = new SceneTestSession(project, new(project.MainScenePath, source, 2, null, false, "scene", false));
        await session.RestartAsync(); Assert.Equal("choice", session.Story.Kind); Assert.False(session.CanGoBack);
        await session.ChooseAsync(2); string first = session.Story.Text; Assert.EndsWith(":2", first);
        await session.AdvanceAsync(); string last = session.Story.Text; Assert.StartsWith("last:", last);
        await session.AdvanceAsync(); Assert.True(session.Completed);
        await session.BackAsync(); Assert.Equal(last, session.Story.Text); Assert.False(session.Completed);
        await session.BackAsync(); Assert.Equal(first, session.Story.Text);
        await session.BackAsync(); Assert.Equal("choice", session.Story.Kind); Assert.False(session.CanGoBack);
        await session.ChooseAsync(1); Assert.Equal(first[..first.LastIndexOf(':')] + ":1", session.Story.Text);
        await session.RestartAsync(); Assert.Equal("choice", session.Story.Kind); Assert.False(session.CanGoBack);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "saves"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Scene_BackRejectsChangedDependencies_AndRestartUsesUpdatedCode()
    {
        using var fixture = new TempProject();
        const string source = "function main() end\nfunction scene() call(child) d(nil,'last') end";
        fixture.Write("game/scenes/main.krl", source);
        fixture.Write("game/scenes/child.krl", "function child() d(nil,'old') end");
        var project = await fixture.LoadAsync();
        await using var session = new SceneTestSession(project, new(project.MainScenePath, source, 2, null, false, "scene", false));
        await session.RestartAsync(); Assert.Equal("old", session.Story.Text); await session.AdvanceAsync();
        fixture.Write("game/scenes/child.krl", "function child() d(nil,'new') end");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.BackAsync());
        Assert.Contains("изменились", error.Message); Assert.Equal("last", session.Story.Text);
        await session.RestartAsync(); Assert.Equal("new", session.Story.Text);
    }

    [Fact]
    public async Task Scene_BackRejectsChangedInitialState_InsteadOfSilentlyReplayingDifferentBranch()
    {
        using var fixture = new TempProject();
        const string source = "function main() d(nil,tostring(state.score)) d(nil,'last') end";
        fixture.Write("game/scenes/main.krl", source); fixture.Write("engine/state.json", "{\"score\":10}");
        var project = await fixture.LoadAsync();
        await using var session = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await session.RestartAsync(); Assert.Equal("10", session.Story.Text); await session.AdvanceAsync();
        fixture.Write("engine/state.json", "{\"score\":20}");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.BackAsync());
        Assert.Contains("изменились", error.Message); Assert.Equal("last", session.Story.Text);
        await session.RestartAsync(); Assert.Equal("20", session.Story.Text);
    }

    [Fact]
    public async Task FrameWindow_SelectedVisualBlockBeforeChoice_BecomesNoninteractiveSnapshot()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var graph = new VisualScript { FunctionName = "snapshot", Nodes = [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "background" },
            new() { Id = "background", Kind = ScriptNodeKind.Background, Next = "choice" },
            new() { Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Choose once", Options = [new() { Text = "A", Target = "end" }, new() { Text = "B", Target = "end" }] },
            new() { Id = "end", Kind = ScriptNodeKind.End }] };
        string path = fixture.Write("game/scenes/snapshot.knegraph", graph.Serialize());
        await Sta(async () =>
        {
            var window = new SceneTestingWindow(project, new(path, graph.Compile(editorPreview: true), 1, "background", false, "snapshot", false), false);
            try
            {
                await window.InitializeAsync(); var surface = Find<ScenePreview>(window, "SceneSurface");
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "TestingErrorOverlay").Visibility);
                Assert.Equal("Choose once", Find<TextBlock>(surface, "DialogueText").Text);
                var choices = Find<StackPanel>(surface, "ChoicePanel"); Assert.Equal(2, choices.Children.Count); Assert.False(choices.IsHitTestVisible);
                var frame = Field<EditorPreview>(window, "frame"); Assert.False(frame.Reached); Assert.Equal("choice", frame.Story.Kind);
                Assert.NotNull(frame.Story.Identity); Assert.Contains("Снимок фрейма", Find<TextBlock>(window, "TestingStatus").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task FrameWindow_SeeksPastSeveralVisualCommands_AndShowsFrozenTextWithIdentity()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        const string source = "function main()\n local a=1\n background(nil)\n local b=2\n d(nil,'selected:'..(a+b))\n d(nil,'later')\nend";
        await Sta(async () =>
        {
            var window = new SceneTestingWindow(project, new(project.MainScenePath, source, 2, null, true, "main", false), false);
            try
            {
                Assert.Null(Field<EditorPreview?>(window, "frame"));
                await window.InitializeAsync();
                var surface = Find<ScenePreview>(window, "SceneSurface");
                Assert.Equal("selected:3", Find<TextBlock>(surface, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Button>(window, "NextFrameButton").Visibility);
                Assert.False(Find<AnimatedBackground>(surface, "AnimatedBackdrop").HasAnimatedProperties);
                var identity = Field<EditorPreview>(window, "frame").Story.Identity; Assert.NotNull(identity);
                Assert.Contains($"фрейм {identity.SceneFrameId} / в игре {identity.GlobalFrameId}", Find<TextBlock>(window, "TestingStatus").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "TestingErrorOverlay").Visibility);
                Capture(window, "frame-window.png");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SceneWindow_EndOverlayReturnsToLastFrame_AndRestartBeginsScene()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        const string source = "function main() error('main is outside selected scene') end\nfunction isolated() d(nil,'first') d(nil,'last') end";
        await Sta(async () =>
        {
            var window = new SceneTestingWindow(project, new(project.MainScenePath, source, 2, null, false, "isolated", false), true);
            try
            {
                await window.InitializeAsync(); var surface = Find<ScenePreview>(window, "SceneSurface");
                Assert.Equal("first", Find<TextBlock>(surface, "DialogueText").Text);
                Click(window, "NextFrameButton"); await Until(() => Find<TextBlock>(surface, "DialogueText").Text == "last");
                Click(window, "NextFrameButton"); await Until(() => Find<Border>(window, "SceneEndOverlay").Visibility == Visibility.Visible);
                Assert.True(Find<Button>(window, "BackFrameButton").IsEnabled); Capture(window, "scene-end.png");
                Click(window, "BackFrameButton"); await Until(() => Find<Border>(window, "SceneEndOverlay").Visibility == Visibility.Collapsed);
                Assert.Equal("last", Find<TextBlock>(surface, "DialogueText").Text);
                Click(window, "NextFrameButton"); await Until(() => Find<Border>(window, "SceneEndOverlay").Visibility == Visibility.Visible);
                Click(window, "RestartSceneButton"); await Until(() => Find<TextBlock>(surface, "DialogueText").Text == "first");
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "SceneEndOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Workspace_TestingOnlyStartsByButtons_UsesUnsavedCode_AndFooterGroupsKeepOrder()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var host = new StudioHost(); var window = host.Window;
            try
            {
                await InvokeAsync(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await InvokeAsync(window, "OpenScriptAsync", Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
                var editor = Find<ScriptEditor>(window, "SceneEditor");
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function main() d(nil,'unsaved') end";
                Assert.Empty(host.Tests); Assert.Null(window.FindName("EmbeddedPreview"));
                Click(editor, "RunButton"); Click(editor, "TestSceneButton"); Assert.Equal(2, host.Tests.Count);
                foreach (var testing in host.Tests)
                {
                    Assert.Null(Field<SceneTestSession?>(testing, "scene")); Assert.Null(Field<EditorPreview?>(testing, "frame"));
                    Assert.Contains("unsaved", Field<EditorSelection>(testing, "selection").Source);
                }
                Assert.Equal(new[] { "Выйти", "Сохранить всё и выйти" }, Labels(window, "ExitActionsGroup"));
                Assert.Equal(new[] { "Создать файл", "Импорт файла", "Новая блок-схема", "Импорт .rpy", "Импорт .docx" }, Labels(window, "ImportActionsGroup"));
                Assert.Equal(new[] { "Список файлов", "Обновить" }, Labels(window, "FileViewActionsGroup"));
                Assert.Equal(new[] { "Анализ проекта", "Запустить проект" }, Labels(window, "RunActionsGroup"));
                foreach (double width in new[] { 1100d, 1460d })
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(width, 900)); content.Arrange(new Rect(0, 0, width, 900)); content.UpdateLayout();
                    foreach (string group in new[] { "ExitActionsGroup", "ImportActionsGroup", "FileViewActionsGroup", "RunActionsGroup" })
                        foreach (Button button in Find<WrapPanel>(window, group).Children)
                        {
                            var point = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), content);
                            Assert.InRange(point.X, 0, width); Assert.InRange(point.Y, 0, 900);
                        }
                }
                Capture((FrameworkElement)window.Content, "studio-workspace.png");
                Assert.DoesNotContain("unsaved", File.ReadAllText(editor.Document!.FilePath)); editor.Clear();
            }
            finally { foreach (var testing in host.Tests) testing.Close(); window.Close(); }
        });
    }

    [Fact]
    public async Task Workspace_AnalyzeSavesCurrentEdits_WritesReportAndOpensItWithoutRunningProject()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var host = new StudioHost(); var window = host.Window;
            try
            {
                await InvokeAsync(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await InvokeAsync(window, "OpenScriptAsync", Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
                var editor = Find<ScriptEditor>(window, "SceneEditor");
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "error('never execute me')\nfunction main() background('missing.png') end";
                string? path = await window.AnalyzeCurrentProjectAsync();
                Assert.Equal(path, host.OpenedReport); Assert.True(File.Exists(path));
                Assert.Contains("missing.png", await File.ReadAllTextAsync(path!));
                Assert.Contains("never execute me", await File.ReadAllTextAsync(editor.Document!.FilePath));
                Assert.False(editor.Document.IsDirty); Assert.Empty(host.Tests); editor.Clear();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Workspace_SaveAllExitSavesAndCloses_ButConflictKeepsEditorOpen()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var host = new StudioHost(); var window = host.Window; bool closed = false; window.Closed += (_, _) => closed = true;
            string path = Path.Combine(fixture.ProjectPath, "game/scenes/main.krl");
            try
            {
                await InvokeAsync(window, "OpenProjectAsync", fixture.ProjectPath, false); await InvokeAsync(window, "OpenScriptAsync", path);
                var editor = Find<ScriptEditor>(window, "SceneEditor"); Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function main() d(nil,'keep edits') end";
                File.WriteAllText(path, "function main() d(nil,'external') end");
                Find<WrapPanel>(window, "ExitActionsGroup").Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Find<TextBlock>(window, "OperationStatus").Text.Contains("Операция не завершена"));
                Assert.False(closed); Assert.True(editor.Document!.IsDirty); Assert.Contains("external", File.ReadAllText(path));
                editor.Clear(); await InvokeAsync(window, "OpenScriptAsync", path);
                Find<KrlCodeEditor>(editor, "CodeEditor").Text = "function main() d(nil,'saved all') end";
                Find<WrapPanel>(window, "ExitActionsGroup").Children.OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => closed); Assert.Contains("saved all", File.ReadAllText(path));
            }
            finally { if (!closed) { Find<ScriptEditor>(window, "SceneEditor").Clear(); window.Close(); } }
        });
    }

    [Fact]
    public async Task Graph_RightButtonStartsAndEndsPan_ButPortRightClickStillDisconnectsOnlyItsLink()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            editor.Measure(new Size(1280, 800)); editor.Arrange(new Rect(0, 0, 1280, 800));
            var viewport = Find<Border>(editor, "GraphScroll");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
            viewport.RaiseEvent(down); Assert.True(down.Handled); Assert.True(Field<bool>(editor, "panning"));
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent };
            viewport.RaiseEvent(up); Assert.True(up.Handled); Assert.False(Field<bool>(editor, "panning"));
            Assert.False(editor.Document!.IsDirty);
            var start = Find<Canvas>(editor, "GraphCanvas").Children.OfType<Border>().Single(card => (string)card.Tag == "start");
            var output = ((StackPanel)start.Child).Children.OfType<Button>().Single();
            down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
            output.RaiseEvent(down); Assert.False(down.Handled); Assert.False(Field<bool>(editor, "panning"));
            output.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
            Assert.Null(editor.Document.Graph!.Nodes.Single(node => node.Id == "start").Next);
            Assert.Equal("end", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Next); Assert.True(editor.Document.IsDirty);
            editor.Clear();
        });
    }

    [Fact]
    public async Task Graph_PanAndZoomNeverChangeDocument_AndDistantSignedBlocksExpandCanvas()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create();
        graph.Nodes[1].X = 25000; graph.Nodes[1].Y = -18000;
        string path = fixture.Write("game/scenes/map.knegraph", graph.Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            string before = editor.Document!.Graph!.Serialize();
            var canvas = Find<Canvas>(editor, "GraphCanvas"); var viewport = editor.FindName("GraphScroll");
            Assert.IsType<Border>(viewport); Assert.True(canvas.Width > 25000); Assert.True(canvas.Height > 18000);
            editor.PanGraph(-24000, 18500);
            var pan = Find<TranslateTransform>(editor, "CanvasPan"); Assert.Equal(-24000, pan.X); Assert.Equal(18500, pan.Y);
            Click(editor, "ZoomInButton"); Assert.Equal(1.1, Find<Slider>(editor, "ZoomSlider").Value, 5);
            Click(editor, "ZoomOutButton"); Assert.Equal(1, Find<Slider>(editor, "ZoomSlider").Value, 5);
            editor.ChangeZoom(100); Assert.Equal(1.5, Find<Slider>(editor, "ZoomSlider").Value);
            editor.ChangeZoom(-100); Assert.Equal(.4, Find<Slider>(editor, "ZoomSlider").Value);
            Assert.Equal(before, editor.Document.Graph.Serialize()); Assert.False(editor.Document.IsDirty);
            editor.PanGraph(-9920 - pan.X, 7280 - pan.Y);
            Capture(editor, "graph-pan.png");
            editor.Clear();
        });
    }

    private sealed class StudioHost
    {
        public MainWindow Window { get; }
        public List<SceneTestingWindow> Tests { get; } = [];
        public string? OpenedReport { get; private set; }
        public StudioHost()
        {
            Action<Window> show = window => Tests.Add((SceneTestingWindow)window);
            Action<string> open = path => { Assert.True(File.Exists(path)); OpenedReport = path; };
            Window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), BindingFlags.Instance | BindingFlags.NonPublic, null, [show, open], null)!;
        }
    }
    private static string[] Labels(FrameworkElement owner, string group) => Find<WrapPanel>(owner, group).Children.Cast<Button>().Select(button => (string)button.Content).ToArray();
    private static T Find<T>(FrameworkElement owner, string name) => (T)owner.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static Task InvokeAsync(object target, string name, params object[] arguments) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
    private static void Click(FrameworkElement owner, string name) => Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Capture(FrameworkElement view, string file)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER"); if (directory == null) return;
        if (view is Window window) view = (FrameworkElement)window.Content;
        view.Measure(new Size(1280, 800)); view.Arrange(new Rect(0, 0, 1280, 800)); view.UpdateLayout();
        Directory.CreateDirectory(directory); var image = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
        image.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
