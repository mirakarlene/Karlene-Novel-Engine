using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class StudioConfigurationTests
{
    [Fact]
    public void ScreenNames_AreUniqueIgnoringCaseAndWhitespace_AndLegacyIdsStillResolve()
    {
        var layouts = ScreenLayouts.CreateDefaults();
        var gallery = layouts.AddScreen("  Галерея  ");
        Assert.Equal("Галерея", gallery.Name);
        Assert.Same(gallery, layouts.ResolveReference(" галерея "));
        Assert.Same(gallery, layouts.ResolveReference(gallery.Id));
        var namedLikeLegacyId = layouts.AddScreen("main_menu");
        Assert.Same(namedLikeLegacyId, layouts.ResolveReference("main_menu"));
        int count = layouts.Screens.Count;
        Assert.Throws<InvalidOperationException>(() => layouts.AddScreen("гАЛЕРЕЯ"));
        Assert.Equal(count, layouts.Screens.Count);
        layouts.Screens.Last().Name = layouts.Screens.First().Name.ToUpperInvariant() + " ";
        Assert.Contains("Названия", Assert.Throws<InvalidDataException>(layouts.Validate).Message);
    }

    [Fact]
    public async Task Appearance_DuplicateRenameRollsBack_AndNewScreensGetDistinctNames()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var editor = new AppearanceEditor(); await editor.OpenAsync(project);
            Click(editor, "AddScreenButton"); Click(editor, "AddScreenButton");
            Assert.Equal(new[] { "Новый экран", "Новый экран 2" }, editor.Document!.Layout.Screens.TakeLast(2).Select(item => item.Name));
            var last = editor.Document.Layout.Screens.Last(); string id = last.Id;
            Find<TextBox>(editor, "ScreenName").Text = " новый экран "; Click(editor, "ApplyElementButton");
            Assert.Equal("Новый экран 2", editor.Document.Layout.Screens.Single(item => item.Id == id).Name);
            Assert.Contains("уникальными", Find<TextBlock>(editor, "EditorStatus").Text);
            await editor.SaveAsync();
            var reloaded = await fixture.LoadAsync();
            Assert.Equal(7, reloaded.Engine.Screens.Screens.Count);
        });
    }

    [Fact]
    public async Task ShowScreen_UsesNameOpacityAndLayer_InGameAndPreview_AndKeepsLegacyReferences()
    {
        using var fixture = new TempProject(); var document = await AppearanceDocument.OpenAsync(await fixture.LoadAsync());
        var screen = document.Layout.AddScreen("Галерея"); screen.Elements.Clear(); screen.Elements.Add(new() { Id = "caption", Type = "text", Text = "Art", X = 30, Y = 40 });
        await document.SaveAsync();
        var graph = VisualScript.Create(); graph.Nodes[0].Next = "show-main";
        graph.Nodes.Add(new() { Id = "show-main", Kind = ScriptNodeKind.ShowScreen, Path = "Галерея", Opacity = .35, ScreenLayer = "main", Next = "dialogue" });
        graph.Nodes.Single(node => node.Id == "dialogue").Next = "show-overlay";
        graph.Nodes.Add(new() { Id = "show-overlay", Kind = ScriptNodeKind.ShowScreen, Path = screen.Id, Opacity = .8, ScreenLayer = "overlay", Next = "second" });
        graph.Nodes.Add(new() { Id = "second", Kind = ScriptNodeKind.Dialogue, Text = "second", Next = "end" });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true); await runtime.StartAsync();
        Assert.Equal(screen.Id, runtime.Visuals.OverlayScreen); Assert.Equal(.35, runtime.Visuals.ScreenOpacity);
        await Sta(async () =>
        {
            var game = new GameWindow(project); var preview = new ScenePreview();
            try
            {
                SetField(game, "runtime", runtime);
                foreach (string layer in new[] { "main", "overlay" })
                {
                    Invoke(game, "ShowGameplay"); Invoke(game, "RenderOutput");
                    preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals, runtime.Story, false, false);
                    foreach (FrameworkElement owner in new FrameworkElement[] { game, preview })
                    {
                        var main = Find<Viewbox>(owner, "MainScriptScreenViewbox"); var overlay = Find<Viewbox>(owner, "ScriptScreenViewbox");
                        Assert.Equal(layer == "main" ? Visibility.Visible : Visibility.Collapsed, main.Visibility);
                        Assert.Equal(layer == "overlay" ? Visibility.Visible : Visibility.Collapsed, overlay.Visibility);
                        Assert.Equal(layer == "main" ? .35 : .8, layer == "main" ? main.Opacity : overlay.Opacity);
                        var canvas = Find<Canvas>(owner, layer == "main" ? "MainScriptScreenCanvas" : "ScriptScreenCanvas");
                        Assert.Equal("Art", Assert.IsType<TextBlock>(Assert.Single(canvas.Children.Cast<UIElement>())).Text);
                        Assert.True(Panel.GetZIndex(overlay) > Panel.GetZIndex(Find<Grid>(owner, "VisualLayer")));
                        Assert.Same(Find<Grid>(owner, "VisualLayer"), main.Parent);
                        Assert.True(Panel.GetZIndex(overlay) > Panel.GetZIndex(Find<Border>(owner, "DialoguePanel")));
                    }
                    Capture(Find<Grid>(preview, "SceneArea"), "screen-" + layer + ".png");
                    if (layer == "main") await runtime.AdvanceAsync();
                }
                Assert.Equal("second", runtime.Story.Text);
            }
            finally { preview.Stop(); game.Close(); }
        });
    }

    [Theory]
    [InlineData("'Главное меню',-0.01,'main'")]
    [InlineData("'Главное меню',1.01,'overlay'")]
    [InlineData("'Главное меню',1,'unknown'")]
    [InlineData("'Нет такого экрана',1,'overlay'")]
    public async Task ShowScreen_InvalidArgumentsReportSource_WithoutReplacingPreviousScreen(string args)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n show_screen('main_menu',.5,'main')\n show_screen(" + args + ")\nend");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.krl", report.File); Assert.Equal(3, report.Line); Assert.Contains("show_screen", report.Message);
        Assert.Equal("main_menu", runtime.Visuals.OverlayScreen); Assert.Equal(.5, runtime.Visuals.ScreenOpacity); Assert.Equal("main", runtime.Visuals.ScreenLayer);
    }

    [Fact]
    public async Task ClearVisuals_RemoveImagesDefaultBackgroundAndTheirAnimations_AndBackgroundRestoresDefault()
    {
        using var fixture = new TempProject(); WritePng(Path.Combine(fixture.ProjectPath, "game", "asset.png"));
        fixture.Write("game/scenes/main.krl", """
            function main()
                background('game/asset.png'); show_character('game/asset.png','left')
                move_visual('background',30,40,2); animate_visual('background','pulse',2,1)
                move_visual('sprite',50,60,2); animate_visual('sprite','rotate',2,90)
                d(nil,'before')
                clear_backgrounds(); clear_sprites(); d(nil,'cleared')
                hide_background(); d(nil,'default restored')
            end
            """);
        var project = await fixture.LoadAsync(); await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.NotNull(runtime.Visuals.Background); Assert.NotNull(runtime.Visuals.Character);
        await Sta(async () =>
        {
            var game = new GameWindow(project); var preview = new ScenePreview();
            try
            {
                SetField(game, "runtime", runtime); Invoke(game, "ShowGameplay"); Invoke(game, "RenderOutput");
                preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals, runtime.Story, false, false);
                Assert.NotNull(Find<Image>(game, "BackgroundImage").Source); Assert.NotNull(Find<Image>(preview, "BackgroundImage").Source);
                Assert.NotNull(Find<Image>(game, "CharacterImage").Source); Assert.NotNull(Find<Image>(preview, "CharacterImage").Source);
                await runtime.AdvanceAsync(); var cleared = runtime.Visuals;
                Assert.True(cleared.BackgroundsCleared); Assert.Null(cleared.Background); Assert.Null(cleared.Character);
                Assert.Equal(new VisualMotion(), cleared.BackgroundMotion); Assert.Equal(new VisualMotion(), cleared.CharacterMotion);
                Assert.Equal(new VisualAnimation(), cleared.BackgroundAnimation); Assert.Equal(new VisualAnimation(), cleared.CharacterAnimation);
                Invoke(game, "RenderOutput");
                preview.DisplayScene(project, runtime.WindowSettings, cleared, runtime.Story, false, false);
                Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(game, "AnimatedBackdrop").Visibility);
                Assert.Equal(Visibility.Collapsed, Find<FrameworkElement>(preview, "DefaultBackground").Visibility);
                Assert.Null(Find<Image>(game, "CharacterImage").Source); Assert.Null(Find<Image>(preview, "CharacterImage").Source);
                Assert.Null(Find<Image>(game, "BackgroundImage").Source); Assert.Null(Find<Image>(preview, "BackgroundImage").Source);
                await runtime.AdvanceAsync(); Assert.False(runtime.Visuals.BackgroundsCleared); Assert.Null(runtime.Visuals.Background);
                Invoke(game, "RenderOutput"); preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals, runtime.Story, false, false);
                Assert.Equal(Visibility.Visible, Find<FrameworkElement>(game, "AnimatedBackdrop").Visibility);
                Assert.Equal(Visibility.Visible, Find<FrameworkElement>(preview, "DefaultBackground").Visibility);
            }
            finally { preview.Stop(); game.Close(); }
        });
    }

    [Fact]
    public async Task Comments_HaveNoPorts_AndLegacyChainsMigrateWithoutDroppingTheirContinuation()
    {
        using var fixture = new TempProject(); var legacy = VisualScript.Create();
        legacy.Nodes[0].Next = "note1";
        legacy.Nodes.Add(new() { Id = "note1", Kind = ScriptNodeKind.Comment, Text = "Keep this", Next = "note2" });
        legacy.Nodes.Add(new() { Id = "note2", Kind = ScriptNodeKind.Comment, Text = "And this", Next = "dialogue" });
        var graph = VisualScript.Parse(legacy.Serialize());
        Assert.Equal("dialogue", graph.Nodes[0].Next);
        foreach (var note in graph.Nodes.Where(node => node.Kind == ScriptNodeKind.Comment))
        {
            Assert.Null(note.Next); Assert.Empty(note.Outputs()); Assert.Throws<ArgumentOutOfRangeException>(() => note.SetOutput(0, "end"));
        }
        Assert.Equal(new[] { "Keep this", "And this" }, graph.Nodes.TakeLast(2).Select(node => node.Text));
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("Добро пожаловать!", runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "game/scenes/visual.knegraph"));
            var cards = (System.Collections.IDictionary)Field(editor, "cards")!;
            var noteCard = (Border)cards["note1"]!;
            Assert.Empty(((StackPanel)noteCard.Child).Children.OfType<Button>());
            editor.Clear();
        });
        legacy.Nodes.Single(node => node.Id == "note2").Next = "note1";
        Assert.Throws<InvalidDataException>(() => VisualScript.Parse(legacy.Serialize()));
        graph.Nodes[0].Next = "note1"; Assert.Throws<InvalidDataException>(() => graph.Compile());
    }

    [Theory]
    [InlineData("lua")]
    [InlineData("krl")]
    [InlineData("cs")]
    public async Task ProjectScripts_CreateExecutableTemplates_AndNeverOverwriteExistingWork(string extension)
    {
        using var fixture = new TempProject(); string file = Path.Combine(fixture.ProjectPath, "game/scripts/custom." + extension);
        string relative = await ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, file);
        Assert.Equal("game/scripts/custom." + extension, relative);
        var logs = new List<string>();
        fixture.Write("game/scenes/main.krl", "function main() run_script('" + relative + "'); d(nil,'after plugin') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), logs.Add, interactive: true); await runtime.StartAsync();
        Assert.Equal("after plugin", runtime.Story.Text);
        if (extension == "cs") Assert.Contains(logs, text => text.Contains("Плагин выполнен"));
        byte[] ownContent = [239, 187, 191, 45, 45, 32, 99, 117, 115, 116, 111, 109]; File.WriteAllBytes(file, ownContent);
        await ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, file); Assert.Equal(ownContent, File.ReadAllBytes(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public async Task ProjectScripts_RejectOutsideGameAndCancellation_AndImportTexturePreservesSource()
    {
        using var fixture = new TempProject();
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, Path.Combine(fixture.Root, "outside.lua")));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "engine/private.lua")));
        await Assert.ThrowsAsync<ArgumentException>(() => ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, Path.Combine(fixture.ProjectPath, "game/unknown.txt")));
        string canceled = Path.Combine(fixture.ProjectPath, "game/scripts/canceled.lua");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectScriptFiles.EnsureAsync(fixture.ProjectPath, canceled, new CancellationToken(true)));
        Assert.False(File.Exists(canceled));
        string external = Path.Combine(fixture.Root, "outside.png"); WritePng(external); byte[] source = File.ReadAllBytes(external);
        string relative = await ProjectScriptFiles.ImportTextureAsync(fixture.ProjectPath, external);
        Assert.StartsWith("game/ui/", relative); Assert.Equal(source, File.ReadAllBytes(PathSafety.ResolveRelative(fixture.ProjectPath, relative)));
        Assert.Equal(source, File.ReadAllBytes(external));
        Assert.Equal(relative, await ProjectScriptFiles.ImportTextureAsync(fixture.ProjectPath, PathSafety.ResolveRelative(fixture.ProjectPath, relative)));
    }

    [Fact]
    public async Task ConfigurationEditor_AppliesAllStyleFieldsAtomically_AndScriptEditEventCapturesSavedPath()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/map.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.ConfigureDialogue);
            var style = GameplayUiStyle.ForTarget("dialogue") with { X = 12.5, Y = 34, Width = 750, Height = 210, FontFamily = "Consolas", FontSize = 28,
                FontWeight = "Bold", TextColor = "#FFAABBCC", BackgroundColor = "#FF102030", Opacity = .6, CornerRadius = 13,
                PaddingLeft = 1, PaddingTop = 2, PaddingRight = 3, PaddingBottom = 4, HorizontalAlignment = "center", VerticalAlignment = "top", TextAlignment = "right" };
            foreach (var property in typeof(GameplayUiStyle).GetProperties().Where(property => property.Name != "EqualityContract"))
            {
                var control = Find<StackPanel>(editor, "ExtendedProperties").Children.OfType<FrameworkElement>().Single(element => element.Name == "Ui" + property.Name + "Input");
                if (control is TextBox text) text.Text = Convert.ToString(property.GetValue(style), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                else ((ComboBox)control).SelectedItem = property.GetValue(style);
            }
            var beforeInvalidEdit = editor.Document!.Graph!.Nodes.Last().Ui;
            Extended<TextBox>(editor, "UiOpacityInput").Text = "2"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(beforeInvalidEdit, editor.Document.Graph.Nodes.Last().Ui);
            Extended<TextBox>(editor, "UiOpacityInput").Text = ".6"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(style, editor.Document.Graph.Nodes.Last().Ui);
            editor.AddBlock(ScriptNodeKind.ConfigureButton); Extended<ComboBox>(editor, "UiTargetInput").SelectedItem = "pause"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal("pause", editor.Document.Graph.Nodes.Last().UiTarget);
            editor.AddBlock(ScriptNodeKind.Script); int requests = 0; (string NodeId, string Path)? captured = null;
            editor.ProjectScriptRequested += (_, _) => { requests++; captured = editor.CaptureProjectScript(); };
            Find<TextBox>(editor, "PathInput").Text = "game/scripts/custom.lua";
            Extended<Button>(editor, "EditProjectScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, requests); Assert.Equal("game/scripts/custom.lua", captured!.Value.Path);
            string scriptId = captured.Value.NodeId; editor.SetProjectScriptPath(scriptId, "game/scripts/edited.lua");
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal(style, editor.Document.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.ConfigureDialogue).Ui);
            Assert.Equal("game/scripts/edited.lua", editor.Document.Graph.Nodes.Single(node => node.Id == scriptId).Path); editor.Clear();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainWindow_CloseWaitsForPendingCatalogPreferences_BeforePublishingClosed(bool saveAll)
    {
        using var fixture = new TempProject(); var store = new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "catalog-close.json"));
        await Sta(async () =>
        {
            var window = new MainWindow(); var library = Find<BlockLibraryView>(window, "LibraryView"); library.ConfigurePreferenceStore(store);
            await library.LoadPreferencesAsync();
            var writes = (SemaphoreSlim)Field(store, "writes")!; await writes.WaitAsync();
            bool closed = false; string? atClose = null;
            window.Closed += (_, _) => { closed = true; atClose = File.Exists(store.SettingsPath) ? File.ReadAllText(store.SettingsPath) : null; };
            bool released = false;
            try
            {
                var pending = library.RecordUseAsync(BlockLibrary.Definitions.Single(entry => entry.Kind == ScriptNodeKind.Script));
                Assert.False(pending.IsCompleted);
                if (saveAll) Invoke(window, "BtnSaveAllExit_Click", window, new RoutedEventArgs()); else window.Close();
                Assert.False(closed); Assert.True(library.HasPendingPreferenceSave);
                writes.Release(); released = true; await pending; await Until(() => closed);
                Assert.NotNull(atClose); Assert.Contains("plugins/Script", atClose);
                Assert.Equal(new[] { "plugins/Script" }, (await store.LoadAsync()).Recent);
            }
            finally { if (!released) writes.Release(); window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainWindow_ScriptCatalogCreatesSelectedFileAndUsesConfiguredEditor_WhileCancelCreatesNoFile(bool cancel)
    {
        using var fixture = new TempProject(); string graphPath = fixture.Write("game/scenes/scripts.knegraph", VisualScript.Create().Serialize());
        string fakeEditor = Path.Combine(fixture.Root, "Editor with spaces.exe"); File.WriteAllText(fakeEditor, "test double: never executed");
        string selected = Path.Combine(fixture.ProjectPath, "game/scripts/custom.lua");
        await Sta(async () =>
        {
            int selections = 0; var launches = new List<System.Diagnostics.ProcessStartInfo>();
            Func<Window, string, string?> choose = (_, suggested) => { selections++; Assert.EndsWith(".lua", suggested); return cancel ? null : selected; };
            Action<System.Diagnostics.ProcessStartInfo> launch = launches.Add;
            var constructor = typeof(MainWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(info => info.GetParameters().Length == 4);
            var window = (MainWindow)constructor.Invoke([null, null, choose, launch]);
            var editor = Find<ScriptEditor>(window, "SceneEditor");
            try
            {
                await (Task)Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false)!;
                await (Task)Invoke(window, "OpenScriptAsync", graphPath)!;
                SetField(window, "settings", new UserSettings { EditorPath = fakeEditor });
                var library = Find<BlockLibraryView>(window, "LibraryView"); library.ConfigurePreferenceStore(new(Path.Combine(fixture.Root, "catalog-script.json")));
                Click(editor, "AddBlockButton"); var groups = Find<ListBox>(library, "BlockGroups"); groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "plugins");
                Find<WrapPanel>(library, "BlockCards").Children.Cast<Button>().Single(button => Equals(button.Tag, ScriptNodeKind.Script)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !(bool)Field(window, "busy")!); await library.FlushPendingPreferencesAsync();
                Assert.Equal(1, selections); Assert.Equal(cancel ? 0 : 1, launches.Count);
                var added = editor.Document!.Graph!.Nodes.Last(); Assert.Equal(ScriptNodeKind.Script, added.Kind);
                Assert.Equal(cancel ? "" : "game/scripts/custom.lua", added.Path); Assert.Equal(!cancel, File.Exists(selected));
                if (!cancel)
                {
                    Assert.Equal(fakeEditor, launches[0].FileName); Assert.False(launches[0].UseShellExecute); Assert.Equal(Path.GetFullPath(selected), Path.GetFullPath(Assert.Single(launches[0].ArgumentList)));
                    Assert.Equal(added.Path, VisualScript.Parse(File.ReadAllText(graphPath)).Nodes.Single(node => node.Id == added.Id).Path);
                    File.WriteAllText(selected, "-- user's existing code");
                    Extended<Button>(editor, "EditProjectScriptButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => launches.Count == 2 && !(bool)Field(window, "busy")!);
                    Assert.Equal(1, selections); Assert.Equal("-- user's existing code", File.ReadAllText(selected));
                }
            }
            finally { editor.Clear(); window.Close(); }
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) => (T)owner.FindName(name);
    private static T Extended<T>(ScriptEditor editor, string name) where T : FrameworkElement => Find<StackPanel>(editor, "ExtendedProperties").Children.OfType<T>().Single(control => control.Name == name);
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static object? Invoke(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, args);
    private static void Click(FrameworkElement owner, string name) => Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 50, 60, 90, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
    private static void Capture(FrameworkElement view, string file)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_BLOCK_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); view.Measure(new Size(1280, 720)); view.Arrange(new Rect(0, 0, 1280, 720)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
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
