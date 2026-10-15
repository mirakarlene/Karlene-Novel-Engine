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

public sealed class BlockLibraryTests
{
    [Fact]
    public void Catalog_ContainsAllOldAndNewCommandsInOrderedGroups_AndPreservesConditionCategoryColors()
    {
        Assert.Equal(new[] { "Начало и конец", "Нарратив", "Логика", "Визуал", "Звук", "Асинхронность", "Конфигурации", "Плагины" }, BlockLibrary.Groups.Select(group => group.Title));
        Assert.Equal(Enum.GetValues<ScriptNodeKind>().Order(), BlockLibrary.Definitions.Select(entry => entry.Kind).Distinct().Order());
        Assert.Equal(2, BlockLibrary.Definitions.Count(entry => entry.Kind == ScriptNodeKind.Condition));
        Assert.Equal("#FFFFFF", BlockLibrary.ColorFor(BlockLibrary.Create(ScriptNodeKind.Start)));
        Assert.Equal("#000000", BlockLibrary.ColorFor(BlockLibrary.Create(ScriptNodeKind.End)));
        var narrative = BlockLibrary.Create(ScriptNodeKind.Condition, "narrative"); var logic = BlockLibrary.Create(ScriptNodeKind.Condition, "logic");
        Assert.Equal("#C65C16", BlockLibrary.ColorFor(narrative)); Assert.Equal("#2563A6", BlockLibrary.ColorFor(logic));
        var graph = VisualScript.Create(); graph.Nodes.Add(logic);
        Assert.Equal("#2563A6", BlockLibrary.ColorFor(VisualScript.Parse(graph.Serialize()).Nodes.Last()));
        Assert.Equal(ScriptEndAction.Menu, BlockLibrary.Create(ScriptNodeKind.End).EndAction);
        Assert.Equal(ScriptEndAction.Return, VisualScript.Parse("{\"Version\":1,\"Nodes\":[{\"Id\":\"end\",\"Kind\":\"End\"}]}").Nodes.Single().EndAction);
        Assert.Throws<ArgumentException>(() => BlockLibrary.Create(ScriptNodeKind.Random, "audio"));
    }

    [Fact]
    public async Task Picker_OpensInsideMainWindow_CancelLeavesGraphUntouched_AndSelectionSpawnsAtViewportCenterWithUndo()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "map";
        string path = fixture.Write("game/scenes/map.knegraph", graph.Serialize());
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await Invoke(window, "OpenScriptAsync", path);
                var editor = Find<ScriptEditor>(window, "SceneEditor"); Assert.Null(editor.FindName("BlockType"));
                Find<BlockLibraryView>(window, "LibraryView").ConfigurePreferenceStore(new(Path.Combine(fixture.Root, "catalog.json")));
                var content = (FrameworkElement)window.Content; Layout(content); editor.PanGraph(-280, 170); editor.ChangeZoom(-2);
                string original = editor.Document!.Graph!.Serialize(); int count = editor.Document.Graph.Nodes.Count;
                Click(editor, "AddBlockButton"); var overlay = Find<Border>(window, "BlockLibraryOverlay");
                var library = Find<BlockLibraryView>(window, "LibraryView");
                Assert.Equal(Visibility.Visible, overlay.Visibility); Assert.InRange(((SolidColorBrush)overlay.Background).Color.A, (byte)1, (byte)254);
                Assert.False(Find<Grid>(window, "ProjectPanel").IsEnabled); Assert.Equal(count, editor.Document.Graph.Nodes.Count);
                Click(library, "CloseLibraryButton"); Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Equal(original, editor.Document.Graph.Serialize()); Assert.False(editor.Document.IsDirty);
                Click(editor, "AddBlockButton"); SelectGroup(library, "logic");
                Layout(content); Capture(content, "block-library-logic.png");
                var viewport = Find<Border>(editor, "GraphScroll"); double zoom = Find<Slider>(editor, "ZoomSlider").Value;
                double expectedX = (viewport.ActualWidth / 2 + 280) / zoom, expectedY = (viewport.ActualHeight / 2 - 170) / zoom;
                Card(library, ScriptNodeKind.Condition).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Layout(content);
                var added = editor.Document.Graph.Nodes.Last(); Assert.Equal(ScriptNodeKind.Condition, added.Kind); Assert.Equal("logic", added.Category);
                await library.SavePreferencesAsync(); SelectGroup(library, "recent");
                Assert.Equal(ScriptNodeKind.Condition, Assert.Single(Find<WrapPanel>(library, "BlockCards").Children.Cast<Button>()).Tag);
                var card = Find<Canvas>(editor, "GraphCanvas").Children.OfType<Border>().Single(card => Equals(card.Tag, added.Id));
                Assert.Equal(expectedX, added.X + card.ActualWidth / 2, 3); Assert.Equal(expectedY, added.Y + card.ActualHeight / 2, 3);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility); Assert.True(Find<Grid>(window, "ProjectPanel").IsEnabled);
                Assert.True(editor.Document.IsDirty); Assert.Equal(count + 1, editor.Document.Graph.Nodes.Count);
                Click(editor, "UndoButton"); Assert.Equal(original, editor.Document.Graph.Serialize());
                Click(editor, "RedoButton"); Assert.Equal(added.Id, editor.Document.Graph.Nodes.Last().Id);
                editor.Clear();
            }
            finally { Find<ScriptEditor>(window, "SceneEditor").Clear(); window.Close(); }
        });
    }

    [Fact]
    public async Task Picker_SearchFiltersOnlySelectedGroup_AndDuplicateStartIsUnavailable()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize()));
            var view = new BlockLibraryView(); int chosen = 0; view.BlockSelected += (_, _) => chosen++;
            view.Open(editor.CanAddBlock); SelectGroup(view, "terminals");
            Card(view, ScriptNodeKind.Start).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, chosen); Assert.True(Card(view, ScriptNodeKind.End).IsEnabled);
            SelectGroup(view, "audio"); Find<TextBox>(view, "BlockSearch").Text = "ambient";
            Assert.Single(Find<WrapPanel>(view, "BlockCards").Children.Cast<Button>());
            Assert.Equal(ScriptNodeKind.AudioChannel, Card(view, ScriptNodeKind.AudioChannel).Tag);
            Find<TextBox>(view, "BlockSearch").Text = "missing-type";
            Assert.Empty(Find<WrapPanel>(view, "BlockCards").Children.Cast<Button>());
            Assert.Equal(Visibility.Visible, Find<TextBlock>(view, "EmptyLibraryText").Visibility);
            Assert.Equal(0, chosen); Assert.False(editor.Document!.IsDirty); editor.Clear();
        });
    }

    [Fact]
    public async Task Editor_NewPropertiesApplyAtomically_KeepPortsAndSurviveSaveReopen()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            editor.AddBlock(ScriptNodeKind.MoveCharacter); var node = editor.Document!.Graph!.Nodes.Last(); string id = node.Id;
            Extended<TextBox>(editor, "OffsetXInput").Text = "185.5"; Extended<TextBox>(editor, "OffsetYInput").Text = "-75";
            Extended<TextBox>(editor, "DurationInput").Text = "invalid"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(0, editor.Document.Graph.Nodes.Last().OffsetX); Assert.Contains("конечное число", Find<TextBlock>(editor, "EditorStatus").Text);
            Extended<TextBox>(editor, "DurationInput").Text = "2.5"; Click(editor, "ApplyPropertiesButton");
            node = editor.Document.Graph.Nodes.Last(); Assert.Equal(185.5, node.OffsetX); Assert.Equal(-75, node.OffsetY); Assert.Equal(2.5, node.Duration);
            editor.AddBlock(ScriptNodeKind.Parallel); Find<TextBox>(editor, "OptionsInput").Text = "Визуал\nЛогика\nМузыка"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(new[] { "Визуал", "Логика", "Музыка" }, editor.Document.Graph.Nodes.Last().Options.Select(option => option.Text));
            Assert.Equal(4, editor.Document.Graph.Nodes.Last().Outputs().Count());
            editor.AddBlock(ScriptNodeKind.End); Extended<ComboBox>(editor, "EndActionInput").SelectedIndex = 2; Extended<TextBox>(editor, "EndScreenInput").Text = "demo_end";
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            node = editor.Document.Graph!.Nodes.Single(node => node.Id == id); Assert.Equal(185.5, node.OffsetX); Assert.Equal(2.5, node.Duration);
            var ending = editor.Document.Graph.Nodes.Last(); Assert.Equal(ScriptEndAction.Screen, ending.EndAction); Assert.Equal("demo_end", ending.Path); editor.Clear();
        });
    }

    [Fact]
    public async Task Analyzer_DetachedCommentsNeedNoConnection_AndChannelResourcesAreCheckedWithoutExecution()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "notes";
        graph.Nodes.Add(new() { Id = "detached-note", Kind = ScriptNodeKind.Comment, Text = "Заметка" });
        fixture.Write("game/scenes/notes.knegraph", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "error('never execute')\nfunction main() play_audio('voice','missing-voice.wav',false); set_master_volume(nil); d(nil,'text') end");
        var report = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.DoesNotContain(report.Findings, finding => finding.BlockId == "detached-note");
        Assert.Contains(report.Findings, finding => finding.Message.Contains("missing-voice.wav"));
        Assert.Contains(report.Findings, finding => finding.Code == "KNE303" && finding.Message.Contains("set_master_volume"));
        Assert.DoesNotContain(report.Findings, finding => finding.Message.Contains("never execute"));
    }

    [Fact]
    public async Task NewGraphsUseMenuEnd_WhileLegacyReturnBlocksKeepTheirMeaning()
    {
        using var fixture = new TempProject();
        var document = await ScriptDocument.CreateVisualAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "game/scenes/new.knegraph"));
        Assert.Equal(ScriptEndAction.Menu, document.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.End).EndAction);
        string newRoot = await ProjectCreator.CreateProjectAsync("Visual", fixture.Root, ProjectAuthoringMode.Visual);
        var project = await ProjectLoader.LoadProjectAsync(newRoot);
        var graph = VisualScript.Parse(await File.ReadAllTextAsync(project.MainScenePath));
        Assert.Equal(ScriptEndAction.Menu, graph.Nodes.Single(node => node.Kind == ScriptNodeKind.End).EndAction);
    }

    [Fact]
    public async Task SceneTesting_TimedEndReturnsToPreviousTextFrame_AndSnapshotDrainsWaits()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        const string source = "function main() d(nil,'before wait'); kne_pause(0.3); kne_end('menu') end";
        await using (var test = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false)))
        {
            await test.RestartAsync(); await test.AdvanceAsync(); Assert.Equal("wait", test.Story.Kind);
            await test.TickAsync(.2); Assert.False(test.Completed); await test.TickAsync(.1); Assert.True(test.Completed);
            await test.BackAsync(); Assert.Equal("before wait", test.Story.Text); Assert.False(test.Completed);
        }
        await Sta(async () =>
        {
            const string paused = "function main()\n kne_pause(2)\n d(nil,'snapshot after timer')\nend";
            var window = new SceneTestingWindow(project, new(project.MainScenePath, paused, 2, null, false, "main", false), false);
            try
            {
                await window.InitializeAsync(); var surface = Find<ScenePreview>(window, "SceneSurface");
                Assert.Equal("snapshot after timer", Find<TextBlock>(surface, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "TestingErrorOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PlayerClock_StopCancelsHungBackgroundWorker_AndKeepsCurrentFrameWithoutAutomaticRestart()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() kne_async({function() kne_pause(.1); print('tick-loop'); debug.sethook(); while true do end end}); d(nil,'safe frame') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await (Task)typeof(GameWindow).GetMethod("StartGameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                Task pump = (Task)typeof(GameWindow).GetMethod("PumpSceneTimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [.1])!;
                await Until(() => Find<TextBlock>(window, "ConsoleOutput").Text.Contains("tick-loop"));
                Assert.True(Find<Button>(window, "StopButton").IsEnabled); Click(window, "StopButton");
                await pump.WaitAsync(TimeSpan.FromSeconds(3));
                var session = (ProcessGameSession)typeof(GameWindow).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Null(session.WorkerProcessId); Assert.Equal("safe frame", Find<TextBlock>(window, "DialogueText").Text);
                await (Task)typeof(GameWindow).GetMethod("PumpSceneTimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [.1])!;
                Assert.Null(session.WorkerProcessId); Assert.Equal(Visibility.Collapsed, Find<Border>(window, "StartupErrorOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SceneWindow_ClockRunsTimerWithoutClicking_AndCloseStopsItsWorker()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            const string source = "function main() kne_pause(.2); d(nil,'timer finished') end";
            var window = new SceneTestingWindow(project, new(project.MainScenePath, source, 1, null, false, "main", false), true);
            try
            {
                await window.InitializeAsync(); var surface = Find<ScenePreview>(window, "SceneSurface");
                await Until(() => Find<TextBlock>(surface, "DialogueText").Text == "timer finished");
                Assert.True(Find<Button>(window, "NextFrameButton").IsEnabled);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "TestingErrorOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PlayerClock_AdvancesBackgroundWhileReading_AndDoesNotAdvanceWhilePaused()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() kne_async({function() kne_pause(.1); show_title('background finished') end}); d(nil,'read me'); d(nil,'next') end");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project);
            try
            {
                await (Task)typeof(GameWindow).GetMethod("StartGameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                Assert.Equal("read me", Find<TextBlock>(window, "DialogueText").Text);
                typeof(GameWindow).GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                await (Task)typeof(GameWindow).GetMethod("PumpSceneTimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [.2])!;
                Assert.Equal("", Find<TextBlock>(window, "SceneTitle").Text);
                typeof(GameWindow).GetMethod("ShowGameplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                await (Task)typeof(GameWindow).GetMethod("PumpSceneTimeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [.1])!;
                Assert.Equal("background finished", Find<TextBlock>(window, "SceneTitle").Text); Assert.Equal("read me", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) => (T)owner.FindName(name);
    private static void SelectGroup(BlockLibraryView view, string id)
    {
        var groups = Find<ListBox>(view, "BlockGroups"); groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == id);
    }
    private static Button Card(BlockLibraryView view, ScriptNodeKind kind) => Find<WrapPanel>(view, "BlockCards").Children.Cast<Button>().Single(button => Equals(button.Tag, kind));
    private static T Extended<T>(ScriptEditor view, string name) where T : FrameworkElement => Find<StackPanel>(view, "ExtendedProperties").Children.OfType<T>().Single(control => control.Name == name);
    private static void Click(FrameworkElement owner, string name) => Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task Invoke(MainWindow window, string name, params object[] arguments) => (Task)typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, arguments)!;
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static void Layout(FrameworkElement content) { content.Measure(new Size(1460, 900)); content.Arrange(new Rect(0, 0, 1460, 900)); content.UpdateLayout(); }
    private static void Capture(FrameworkElement view, string file)
    {
        string? path = Environment.GetEnvironmentVariable("KNE_BLOCK_RENDER"); if (path == null) return;
        Directory.CreateDirectory(path); var bitmap = new RenderTargetBitmap(1460, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(path, file)); encoder.Save(output);
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
