using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class FlowAuthoringUiTests
{
    [Fact]
    public Task GameInput_EditingKeepsKeys_AndHotspotFallbackPreservesScreenButtonMetadata() => WorkspaceTabsTests.Sta(() =>
    {
        var input = new GameTriggerInput();
        var typing = Args(Key.E); typing.Source = new TextBox();
        Assert.Null(input.KeyDown(typing)); input.KeyUp(Args(Key.E, up: true));
        var metadata = new ScreenElement { Id = "go", Type = "button" };
        var button = new Button { Tag = metadata };
        GameTriggerInput.SetButtonId(button, "screen:Panel/go");
        Assert.Same(metadata, button.Tag);
        var story = StoryFrame.Empty with { Triggers = [new TriggerSettings { Kind = "hotspot", X = 0, Y = 0, Width = 600, Height = 400 }] };
        var signal = GameTriggerInput.PointerSignal(story, button, new ScreenLayouts(), new Point(100, 50), new Size(640, 360));
        Assert.Equal(new TriggerSignal("hotspot", X: 200, Y: 100), signal);
        story = story with { Triggers = [.. story.Triggers, new TriggerSettings { Kind = "button", Button = "screen:Panel/go" }] };
        Assert.Equal(new TriggerSignal("button", Button: "screen:Panel/go"), GameTriggerInput.PointerSignal(story, button, new ScreenLayouts(), new Point(100, 50), new Size(640, 360)));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Welcome_UnavailableProjectsStayClickableButDim_AndStorageSettingIsBelowEditorPath()
    {
        using var fixture = new FlowUiFixture();
        await WorkspaceTabsTests.Sta(() =>
        {
            var window = new MainWindow();
            try
            {
                var recent = Find<ItemsControl>(window, "RecentProjectsList");
                recent.ItemsSource = new[] { new StoredProject(fixture.ProjectPath, true), new StoredProject(Path.Combine(fixture.Root, "missing"), false) };
                var content = (FrameworkElement)window.Content; Layout(content);
                var buttons = Descendants(recent).OfType<Button>().ToArray();
                Assert.Equal(2, buttons.Length); Assert.Equal(1, buttons[0].Opacity); Assert.Equal(.4, buttons[1].Opacity);
                Assert.True(buttons[1].IsEnabled); Assert.Equal(Path.Combine(fixture.Root, "missing"), buttons[1].Tag);
                Capture(content, "projects-availability.png");
                Find<StackPanel>(window, "WelcomePanel").Visibility = Visibility.Collapsed;
                var settings = Find<StackPanel>(window, "SettingsPanel"); settings.Visibility = Visibility.Visible;
                var editor = Find<TextBox>(window, "TxtEditorPath"); var storage = Find<TextBox>(window, "TxtProjectsStorage");
                storage.Text = Path.Combine(fixture.Root, "projects"); Layout(content);
                Assert.True(editor.TranslatePoint(new Point(), settings).Y < storage.TranslatePoint(new Point(), settings).Y);
                var save = Descendants(settings).OfType<Button>().Single(button => Equals(button.Content, "Сохранить"));
                Assert.True(storage.TranslatePoint(new Point(), settings).Y < save.TranslatePoint(new Point(), settings).Y);
                Capture(content, "projects-storage-settings.png");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task GameWindow_ButtonTriggerRemainsClickableWhileMainIsWaitingForATimer()
    {
        using var fixture = new FlowUiFixture(); fixture.Write("engine/game.json", "{\"main_menu\":false}");
        fixture.Scene("kne_on_event('{\"kind\":\"button\",\"button\":\"next\",\"once\":true}',function() d(nil,'handler') end); kne_pause(20); d(nil,'after')");
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves")));
            try
            {
                await GameAction(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.True(Find<Button>(window, "NextButton").IsEnabled);
                await GameAction(window, () => Click(window, "NextButton"));
                Assert.Equal("handler", Find<TextBlock>(window, "DialogueText").Text);
                await GameAction(window, () => Click(window, "NextButton"));
                Assert.False(Find<Button>(window, "NextButton").IsEnabled);
                Assert.DoesNotContain("after", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SubsystemFiles_CreateNestedReturnGraph_AndKeepExistingSourceUnchangedWithinProject()
    {
        using var fixture = new FlowUiFixture();
        string path = Path.Combine(fixture.ProjectPath, "game", "scenes", "nested", "child.knegraph");
        Assert.Equal(path, await SubsystemFiles.EnsureAsync(fixture.ProjectPath, path));
        var graph = VisualScript.Parse(File.ReadAllText(path));
        Assert.All(graph.Nodes.Where(node => node.Kind == ScriptNodeKind.End), node => Assert.Equal(ScriptEndAction.Return, node.EndAction));
        Assert.Contains("function ", graph.Compile());
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Dialogue).Text = "existing draft";
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.End).EndAction = ScriptEndAction.Menu;
        string original = graph.Serialize();
        File.WriteAllText(path, original);
        await SubsystemFiles.EnsureAsync(fixture.ProjectPath, path);
        Assert.Equal(original, File.ReadAllText(path));
        string outside = Path.Combine(fixture.Root, "outside.knegraph");
        await Assert.ThrowsAsync<ArgumentException>(() => SubsystemFiles.EnsureAsync(fixture.ProjectPath, outside));
        Assert.False(File.Exists(outside));
        string wrongExtension = Path.Combine(fixture.ProjectPath, "child.lua");
        await Assert.ThrowsAsync<ArgumentException>(() => SubsystemFiles.EnsureAsync(fixture.ProjectPath, wrongExtension));
        Assert.False(File.Exists(wrongExtension));
    }

    [Fact]
    public async Task Editor_SubsystemPropertiesApplyUndoRedoAndSave_AndOpenRequestsTheSelectedNode()
    {
        using var fixture = new FlowUiFixture();
        string path = fixture.Write("game/scenes/parent.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path);
                editor.AddBlock(ScriptNodeKind.Subsystem);
                string id = editor.Document!.Graph!.Nodes.Last().Id;
                Extended<TextBox>(editor, "SubsystemPathInput").Text = " game/scenes/nested/child.knegraph ";
                Extended<CheckBox>(editor, "WaitForSubsystemInput").IsChecked = false;
                Click(editor, "ApplyPropertiesButton");
                Assert.Equal("game/scenes/nested/child.knegraph", Node().Path);
                Assert.False(Node().WaitForSubsystem);
                Click(editor, "UndoButton");
                Assert.Equal("", Node().Path);
                Assert.True(Node().WaitForSubsystem);
                Click(editor, "RedoButton");
                Assert.Equal("game/scenes/nested/child.knegraph", Node().Path);
                Assert.False(Node().WaitForSubsystem);
                string? requested = null;
                editor.SubsystemRequested += (_, node) => requested = node;
                Extended<Button>(editor, "OpenSubsystemButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(id, requested);
                await editor.SaveAsync();
                Assert.False(editor.HasUnsavedChanges);
                await editor.OpenAsync(fixture.ProjectPath, path);
                Assert.Equal("game/scenes/nested/child.knegraph", Node().Path);
                Assert.False(Node().WaitForSubsystem);
                ScriptNode Node() => editor.Document!.Graph!.Nodes.Single(node => node.Id == id);
            }
            finally { editor.Clear(); }
        });
    }

    [Theory]
    [InlineData(ScriptNodeKind.TriggerKey)]
    [InlineData(ScriptNodeKind.TriggerButton)]
    [InlineData(ScriptNodeKind.TriggerHotspot)]
    [InlineData(ScriptNodeKind.TriggerTextClosed)]
    [InlineData(ScriptNodeKind.TriggerCombination)]
    public async Task Editor_TriggerCardsHaveOnlyAnOutput_AndEventPropertiesSurviveSave(ScriptNodeKind kind)
    {
        using var fixture = new FlowUiFixture();
        string path = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path);
                Layout(editor);
                editor.AddBlock(kind);
                string id = editor.Document!.Graph!.Nodes.Last().Id;
                var canvas = Find<Canvas>(editor, "GraphCanvas");
                var card = canvas.Children.OfType<Border>().Single(card => Equals(card.Tag, id));
                var port = Assert.Single(Descendants(card).OfType<GraphPort>());
                Assert.True(port.IsOutput);
                Assert.Equal(0, port.OutputIndex);
                Assert.Equal(id, port.NodeId);
                Extended<CheckBox>(editor, "TriggerOnceInput").IsChecked = true;
                if (kind == ScriptNodeKind.TriggerKey) Extended<TextBox>(editor, "TriggerKeysInput").Text = "F8";
                if (kind == ScriptNodeKind.TriggerCombination) Extended<TextBox>(editor, "TriggerKeysInput").Text = "e+control+shift";
                if (kind == ScriptNodeKind.TriggerButton) Extended<TextBox>(editor, "TriggerButtonInput").Text = "screen:custom/go";
                if (kind == ScriptNodeKind.TriggerHotspot)
                {
                    Extended<TextBox>(editor, "TriggerXInput").Text = "12.5";
                    Extended<TextBox>(editor, "TriggerYInput").Text = "22";
                    Extended<TextBox>(editor, "TriggerWidthInput").Text = "80";
                    Extended<TextBox>(editor, "TriggerHeightInput").Text = "45";
                }
                await editor.SaveAsync();
                Capture(editor, "trigger-" + kind + ".png");
                await editor.OpenAsync(fixture.ProjectPath, path);
                var trigger = editor.Document!.Graph!.Nodes.Single(node => node.Id == id).Trigger!;
                Assert.True(trigger.Once);
                Assert.Equal(kind switch { ScriptNodeKind.TriggerButton => "button", ScriptNodeKind.TriggerHotspot => "hotspot", ScriptNodeKind.TriggerTextClosed => "text_closed", _ => "key" }, trigger.Kind);
                if (kind == ScriptNodeKind.TriggerKey) Assert.Equal("F8", trigger.Keys);
                if (kind == ScriptNodeKind.TriggerCombination) Assert.Equal("Ctrl+E+Shift", trigger.Keys);
                if (kind == ScriptNodeKind.TriggerButton) Assert.Equal("screen:custom/go", trigger.Button);
                if (kind == ScriptNodeKind.TriggerHotspot) Assert.Equal((12.5, 22d, 80d, 45d), (trigger.X, trigger.Y, trigger.Width, trigger.Height));
            }
            finally { editor.Clear(); }
        });
    }

    [Fact]
    public async Task Editor_KeyCaptureCollectsChordUntilRelease_AndEscapeCanBeCapturedWhileCancelDiscards()
    {
        using var fixture = new FlowUiFixture();
        string path = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path);
                editor.AddBlock(ScriptNodeKind.TriggerCombination);
                Extended<Button>(editor, "CaptureTriggerKeysButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var overlay = Find<Border>(editor, "KeyCaptureOverlay");
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Press(editor, Key.LeftCtrl); Press(editor, Key.K);
                Release(editor, Key.K);
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                Release(editor, Key.LeftCtrl);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Equal("Ctrl+K", Extended<TextBox>(editor, "TriggerKeysInput").Text);
                editor.AddBlock(ScriptNodeKind.TriggerKey);
                Extended<Button>(editor, "CaptureTriggerKeysButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var escape = Press(editor, Key.Escape);
                Assert.True(escape.Handled);
                Assert.Equal("Escape", Extended<TextBox>(editor, "TriggerKeysInput").Text);
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Extended<Button>(editor, "CaptureTriggerKeysButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Descendants(overlay).OfType<Button>().Single(button => Equals(button.Content, "Отмена")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Press(editor, Key.F7);
                Assert.Equal("Escape", Extended<TextBox>(editor, "TriggerKeysInput").Text);
                await editor.SaveAsync();
                Assert.Equal("Escape", editor.Document!.Graph!.Nodes.Last().Trigger!.Keys);
            }
            finally { editor.Clear(); }
        });
    }

    [Fact]
    public async Task Workspace_OpenSubsystemCreatesNestedGraphInNewTab_AndRetainsTheParentDraft()
    {
        using var fixture = new FlowUiFixture();
        var graph = VisualScript.Create(); graph.FunctionName = "parent";
        graph.Nodes.Add(new() { Id = "child", Kind = ScriptNodeKind.Subsystem, Path = "game/scenes/nested/child.knegraph", Next = "end" });
        graph.Nodes.Single(node => node.Id == "dialogue").Next = "child";
        string parentPath = fixture.Write("game/scenes/parent.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow();
            var tabs = Find<TabControl>(window, "DocumentTabs");
            NotifyCollectionChangedEventHandler? handler = null;
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", parentPath);
                var parent = (ScriptEditor)((TabItem)tabs.Items[0]).Tag;
                parent.SelectGraphBlocks(["dialogue"]);
                Find<TextBox>(parent, "SceneTextInput").Text = "unsaved parent draft";
                Click(parent, "ApplyPropertiesButton");
                Assert.True(parent.HasUnsavedChanges);
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                handler = (_, _) => { if (tabs.Items.Count == 2) opened.TrySetResult(); };
                ((INotifyCollectionChanged)tabs.Items).CollectionChanged += handler;
                parent.OpenSubsystem("child");
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var nested = (ScriptEditor)((TabItem)tabs.SelectedItem).Tag;
                string childPath = Path.GetFullPath(Path.Combine(fixture.ProjectPath, "game/scenes/nested/child.knegraph"));
                Assert.Equal(childPath, nested.Document!.FilePath);
                Assert.NotSame(parent, nested);
                Assert.True(File.Exists(childPath));
                Assert.All(nested.Document.Graph!.Nodes.Where(node => node.Kind == ScriptNodeKind.End), node => Assert.Equal(ScriptEndAction.Return, node.EndAction));
                Assert.Same(parent, ((TabItem)tabs.Items[0]).Tag);
                Assert.Equal("unsaved parent draft", parent.Document!.Graph!.Nodes.Single(node => node.Id == "dialogue").Text);
                Assert.True(parent.HasUnsavedChanges);
                Assert.Equal("Добро пожаловать!", VisualScript.Parse(File.ReadAllText(parentPath)).Nodes.Single(node => node.Id == "dialogue").Text);
                tabs.SelectedIndex = 0;
                parent.SelectGraphBlocks(["dialogue"]);
                Assert.Equal("unsaved parent draft", Find<TextBox>(parent, "SceneTextInput").Text);
            }
            finally
            {
                if (handler != null) ((INotifyCollectionChanged)tabs.Items).CollectionChanged -= handler;
                foreach (var tab in tabs.Items.Cast<TabItem>().ToArray())
                {
                    var editor = (ScriptEditor)tab.Tag; editor.ConfirmSave = _ => MessageBoxResult.No;
                    await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", editor);
                }
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameWindow_NextButtonAndSpaceTriggers_RunHandlerWithoutConsumingMain(bool key)
    {
        using var fixture = new FlowUiFixture();
        fixture.Write("engine/game.json", "{\"main_menu\":false}");
        string specification = JsonSerializer.Serialize(new TriggerSettings { Kind = key ? "key" : "button", Keys = "Space", Button = "next", Once = true }, EngineSettings.JsonOptions);
        fixture.Scene($$"""
            kne_on_event([=[{{specification}}]=], function() d(nil,"handler") end)
            d(nil,"main")
            d(nil,"after")
            """);
        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves")));
            try
            {
                await GameAction(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.Equal("main", Find<TextBlock>(window, "DialogueText").Text);
                await GameAction(window, () => { if (key) Press(window, Key.Space); else Click(window, "NextButton"); });
                Assert.Equal("handler", Find<TextBlock>(window, "DialogueText").Text);
                await GameAction(window, () => Click(window, "NextButton"));
                Assert.Equal("main", Find<TextBlock>(window, "DialogueText").Text);
                await GameAction(window, () => Click(window, "NextButton"));
                Assert.Equal("after", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task SceneTest_TriggerAndBackReplay_RestoreHandlerThenOriginalMainWithoutConsumingOnceTwice()
    {
        using var fixture = new FlowUiFixture();
        const string source = """
            function main()
                state.hits = 0
                kne_on_event('{"kind":"key","keys":"E","once":true}', function()
                    state.hits=state.hits+1
                    d(nil,'handler:'..state.hits)
                end)
                d(nil,'main')
                d(nil,'after')
            end
            """;
        fixture.Write("game/scenes/main.krl", source);
        var project = await fixture.LoadAsync();
        await using var session = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await session.RestartAsync();
        Assert.Equal("main", session.Story.Text);
        Assert.False(session.CanGoBack);
        await session.TriggerAsync(new TriggerSignal("key", "E"));
        Assert.Equal("handler:1", session.Story.Text);
        Assert.True(session.CanGoBack);
        Assert.Empty(session.Story.Triggers);
        await session.AdvanceAsync();
        Assert.Equal("main", session.Story.Text);
        await session.BackAsync();
        Assert.Equal("handler:1", session.Story.Text);
        Assert.Empty(session.Story.Triggers);
        await session.BackAsync();
        Assert.Equal("main", session.Story.Text);
        Assert.True(Assert.Single(session.Story.Triggers).Once);
        Assert.False(session.CanGoBack);
        await session.TriggerAsync(new TriggerSignal("key", "E"));
        Assert.Equal("handler:1", session.Story.Text);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "saves"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GameInput_NormalizesHeldChordAndRelease_AndScalesHotspotsToLogicalStage()
    {
        await WorkspaceTabsTests.Sta(() =>
        {
            var input = new GameTriggerInput();
            Assert.Equal("Ctrl", input.KeyDown(Args(Key.LeftCtrl))!.Keys);
            Assert.Equal("Ctrl+E", input.KeyDown(Args(Key.E))!.Keys);
            input.KeyUp(Args(Key.E)); input.KeyUp(Args(Key.LeftCtrl));
            Assert.Equal("E", input.KeyDown(Args(Key.E))!.Keys);
            input.Reset();
            Assert.Equal("Space", input.KeyDown(Args(Key.Space))!.Keys);
            Assert.True(GameTriggerInput.IsEditing(new TextBox()));
            Assert.True(GameTriggerInput.IsEditing(new ComboBox { IsEditable = true }));
            Assert.False(GameTriggerInput.IsEditing(new Button()));
            Assert.Equal(new TriggerSignal("hotspot", X: 640, Y: 360), GameTriggerInput.Hotspot(new Point(320, 180), new Size(640, 360)));
            Assert.Equal(new TriggerSignal("hotspot", X: 640, Y: 360), GameTriggerInput.Hotspot(new Point(1280, 720), new Size(2560, 1440)));
            Assert.Equal(new TriggerSignal("hotspot", X: 1280, Y: 720), GameTriggerInput.Hotspot(new Point(640, 360), new Size(640, 360)));
            Assert.Equal("next", GameTriggerInput.ButtonId(new Button { Name = "NextButton" }, ScreenLayouts.CreateDefaults()));
            return Task.CompletedTask;
        });
    }

    private static T Find<T>(FrameworkElement element, string name) where T : class => WorkspaceTabsTests.Find<T>(element, name);
    private static void Layout(FrameworkElement content) { content.Measure(new Size(1460, 900)); content.Arrange(new Rect(0, 0, 1460, 900)); content.UpdateLayout(); }
    private static void Capture(FrameworkElement content, string file)
    {
        string? path = Environment.GetEnvironmentVariable("KNE_FLOW_RENDER"); if (path == null) return;
        Directory.CreateDirectory(path); Layout(content);
        var bitmap = new RenderTargetBitmap(1460, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(path, file)); encoder.Save(output);
    }
    private static T Extended<T>(ScriptEditor editor, string name) where T : FrameworkElement => Find<StackPanel>(editor, "ExtendedProperties").Children.OfType<T>().Single(input => input.Name == name);
    private static void Click(FrameworkElement element, string name) => Find<Button>(element, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static KeyEventArgs Args(Key key, bool up = false) => new(Keyboard.PrimaryDevice, new InputSource(), 0, key) { RoutedEvent = up ? Keyboard.PreviewKeyUpEvent : Keyboard.PreviewKeyDownEvent };
    private static KeyEventArgs Press(UIElement element, Key key) { var args = Args(key); element.RaiseEvent(args); return args; }
    private static void Release(UIElement element, Key key) => element.RaiseEvent(Args(key, up: true));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static async Task GameAction(GameWindow window, Action action)
    {
        var input = Find<TextBox>(window, "CommandInput");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DependencyPropertyChangedEventHandler changed = (_, args) => { if (args.NewValue is true) ready.TrySetResult(); };
        input.IsEnabledChanged += changed;
        try { action(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { input.IsEnabledChanged -= changed; }
    }
    private sealed class InputSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private sealed class FlowUiFixture : IDisposable
    {
        private static readonly string Scratch = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "kne-flows-triggers-tests"));
        public string Root { get; } = Path.Combine(Scratch, "ui-" + Guid.NewGuid().ToString("N"));
        public string ProjectPath { get; }
        public FlowUiFixture() { Directory.CreateDirectory(Root); ProjectPath = ProjectCreator.CreateProject("Flow UI", Root); }
        public string Write(string relative, string content)
        {
            string path = PathSafety.ResolveRelative(ProjectPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, content); return path;
        }
        public void Scene(string body) => Write("game/scenes/main.krl", "function main()\n" + body + "\nend");
        public Task<ProjectInfo> LoadAsync() => ProjectLoader.LoadProjectAsync(ProjectPath);
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("ui-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(target, recursive: true);
        }
    }
}
