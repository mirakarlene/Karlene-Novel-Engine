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

[Trait("Category", "Integration")]
public sealed class AppearanceUiTests
{
    [Theory]
    [InlineData("button")]
    [InlineData("toggle")]
    [InlineData("slider")]
    public async Task AddingElement_SelectsNewElementWithNoAction_AndPreservesPreviousButton(string type)
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var editor = new AppearanceEditor(); var owner = new Window { Content = editor };
            try
            {
                await editor.OpenAsync(project);
                var main = editor.Document!.Layout.Find("main_menu")!;
                var original = main.Elements.Single(e => e.Action == "start");
                var elements = Find<ListBox>(editor, "ElementsList"); elements.SelectedItem = original;
                Find<TextBox>(editor, "ElementText").Text = "Старый элемент";
                var types = Find<ComboBox>(editor, "NewElementType");
                types.SelectedItem = types.Items.Cast<ComboBoxItem>().Single(i => Equals(i.Tag, type));
                Click(editor, "AddAppearanceElementButton");
                var added = Assert.IsType<ScreenElement>(elements.SelectedItem);
                Assert.NotEqual(original.Id, added.Id);
                Assert.Same(main.Elements.Last(), added);
                Assert.Equal(type, added.Type); Assert.Equal("none", added.Action);
                Assert.Equal("none", Find<ComboBox>(editor, "ElementAction").SelectedValue);
                Assert.Equal("Старый элемент", original.Text); Assert.Equal("start", original.Action);
                Find<TextBox>(editor, "ElementText").Text = "Новый элемент";
                Find<ComboBox>(editor, "ElementAction").SelectedValue = type switch { "button" => "settings", "toggle" => "fullscreen", _ => "sound_volume" };
                // Selection changes commit the previous fields and keep the UI and edited model in sync.
                elements.SelectedItem = original;
                Assert.Same(original, elements.SelectedItem);
                Assert.Equal("start", Find<ComboBox>(editor, "ElementAction").SelectedValue);
                Assert.Equal("Новый элемент", added.Text);
                Assert.Equal(type switch { "button" => "settings", "toggle" => "fullscreen", _ => "sound_volume" }, added.Action);
                await editor.SaveAsync();
                var saved = (await fixture.LoadAsync()).Engine.Screens.Find("main_menu")!;
                Assert.Equal("start", saved.Elements.Single(e => e.Id == original.Id).Action);
                Assert.Equal(added.Action, saved.Elements.Single(e => e.Id == added.Id).Action);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public async Task Runtime_IndependentNavigationTargetsAndUnassignedControls_SurviveRepeatedScreenChanges()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var document = await AppearanceDocument.OpenAsync(project);
        var first = document.Layout.AddScreen("Первый экран");
        var second = document.Layout.AddScreen("Второй экран");
        var main = document.Layout.Find("main_menu")!;
        main.Elements.Add(new() { Id = "first-link", Action = "navigate", Target = first.Id, Text = "Первый" });
        main.Elements.Add(new() { Id = "second-link", Action = "navigate", Target = second.Id, Text = "Второй" });
        main.Elements.Add(new() { Id = "inert-button", Action = "none", Text = "Без действия" });
        var settings = document.Layout.Find("settings")!;
        settings.Elements.Add(new() { Id = "inert-toggle", Type = "toggle", Action = "none" });
        settings.Elements.Add(new() { Id = "inert-slider", Type = "slider", Action = "none" });
        await document.SaveAsync(); project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var store = new GameSaveStore(project.Path); var window = new GameWindow(project, store);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (var (id, title) in new[] { ("first-link", "Первый экран"), ("second-link", "Второй экран") })
                    {
                        var button = ScreenCanvas(window).Children.OfType<Button>().Single(b => ((ScreenElement)b.Tag).Id == id);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Contains(ScreenCanvas(window).Children.OfType<TextBlock>(), t => t.Text == title);
                        ScreenButton(window, "back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    ScreenButton(window, "none").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                    Assert.Contains(ScreenCanvas(window).Children.OfType<Button>(), b => ((ScreenElement)b.Tag).Action == "start");
                    ScreenButton(window, "settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var toggle = ScreenCanvas(window).Children.OfType<CheckBox>().Single(t => ((ScreenElement)t.Tag).Id == "inert-toggle");
                    toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var slider = ScreenCanvas(window).Children.OfType<StackPanel>().Single(p => ((ScreenElement)p.Tag).Id == "inert-slider").Children.OfType<Slider>().Single();
                    slider.Value = 0.1;
                    Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                    var backdrop = Find<AnimatedBackground>(window, "AnimatedBackdrop");
                    double before = backdrop.Phase; backdrop.Step(.05); Assert.NotEqual(before, backdrop.Phase);
                    Invoke(window, "RenderOutput");
                    Assert.False(File.Exists(Path.Combine(store.DirectoryPath, "preferences.json")));
                    ScreenButton(window, "back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task MainWindow_OffersAppearanceAndKeepsScriptEditing()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                Assert.Equal("Оформление", Find<Button>(window, "AppearanceButton").Content);
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                await (Task)typeof(MainWindow).GetMethod("OpenProjectAsync", flags)!.Invoke(window, new object[] { fixture.ProjectPath, false })!;
                string path = Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl");
                await (Task)typeof(MainWindow).GetMethod("OpenScriptAsync", flags)!.Invoke(window, new object[] { path })!;
                Assert.Equal(path, Find<ScriptEditor>(window, "SceneEditor").Document!.FilePath);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task AppearanceEditor_EditsPositionsVectorsLayersAndPalette_AndSavesToRuntime()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var editor = new AppearanceEditor(); var owner = new Window { Content = editor };
            try
            {
                await editor.OpenAsync(project);
                var screens = Find<ListBox>(editor, "ScreensList");
                Assert.Equal(5, screens.Items.Count);
                var main = editor.Document!.Layout.Find("main_menu")!;
                Find<ListBox>(editor, "ElementsList").SelectedItem = main.Elements.Single(e => e.Action == "start");
                Find<TextBox>(editor, "ElementX").Text = "350";
                Find<TextBox>(editor, "ElementY").Text = "280";
                Find<TextBox>(editor, "MotionX").Text = "-80";
                Find<TextBox>(editor, "MotionY").Text = "30";
                Find<TextBox>(editor, "MotionOrder").Text = "3";
                Find<TextBox>(editor, "MotionDelay").Text = "0.5";
                Find<TextBox>(editor, "MotionDuration").Text = "2";
                Find<CheckBox>(editor, "MotionLoop").IsChecked = true;
                Click(editor, "ApplyElementButton");
                var element = editor.Document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "start");
                Assert.Equal(350, element.X); Assert.Equal(-80, element.MotionX); Assert.Equal(30, element.MotionY);
                Assert.Equal(3, element.MotionOrder); Assert.Equal(0.5, element.Delay); Assert.Equal(2, element.Duration); Assert.True(element.Loop);
                Click(editor, "RaiseLayerButton");
                Assert.Equal("start", editor.Document.Layout.Find("main_menu")!.Elements[3].Action);
                Find<TextBox>(editor, "ElementX").Text = "-1"; Click(editor, "ApplyElementButton");
                Assert.Equal(350, editor.Document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "start").X);
                Assert.Contains("X", Find<TextBlock>(editor, "EditorStatus").Text);
                Find<ListBox>(editor, "ElementsList").SelectedItem = editor.Document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "start");
                element = editor.Document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "start");
                editor.MoveSelected(5000, -500);
                Assert.Equal(980, element.X); Assert.Equal(0, element.Y);
                Find<ComboBox>(editor, "PaletteChoice").SelectedValue = "ocean";
                Find<ComboBox>(editor, "PatternChoice").SelectedValue = "dots";
                Click(editor, "ApplyThemeButton");
                Assert.Equal("#FF0D2435", editor.Document.Layout.Theme!.BackgroundColor);
                Assert.Equal("dots", editor.Document.Layout.Theme.PatternStyle);
                Assert.True(editor.IsDirty);
                Capture(editor, "appearance-editor.png", 1240, 800);
                await editor.SaveAsync(); Assert.False(editor.IsDirty);
                var loaded = await fixture.LoadAsync();
                Assert.Equal("ocean", loaded.Engine.Theme.Palette);
                var saved = loaded.Engine.Screens.Find("main_menu")!.Elements.Single(e => e.Action == "start");
                Assert.Equal(980, saved.X); Assert.Equal(0, saved.Y); Assert.True(saved.Loop);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public async Task AppearanceEditor_ScreenCrudAndNavigationTargets_ProtectsGameplay()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var editor = new AppearanceEditor(); var owner = new Window { Content = editor };
            try
            {
                await editor.OpenAsync(project);
                Click(editor, "AddScreenButton");
                Assert.Equal(6, editor.Document!.Layout.Screens.Count);
                var custom = (GameScreen)Find<ListBox>(editor, "ScreensList").SelectedItem;
                Find<TextBox>(editor, "ScreenName").Text = "Галерея";
                await editor.SaveAsync(); Assert.Equal("Галерея", custom.Name);
                Click(editor, "DeleteScreenButton");
                Assert.Equal(5, editor.Document.Layout.Screens.Count);
                Click(editor, "UndoAppearanceButton"); Assert.Equal(6, editor.Document.Layout.Screens.Count);
                Click(editor, "RedoAppearanceButton"); Assert.Equal(5, editor.Document.Layout.Screens.Count);
                Find<ListBox>(editor, "ScreensList").SelectedItem = editor.Document.Layout.Find("gameplay");
                Click(editor, "DeleteScreenButton");
                Assert.NotNull(editor.Document.Layout.Find("gameplay"));
                Assert.Contains("нельзя удалить", Find<TextBlock>(editor, "EditorStatus").Text);
                Assert.False(Find<ComboBox>(editor, "ScreenRole").IsEnabled);
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public async Task Runtime_SettingsAndCustomNavigation_UseEditedLayoutAndPersistPreferences()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var document = await AppearanceDocument.OpenAsync(project);
        var gallery = document.Layout.AddScreen("Галерея");
        document.Layout.Find("main_menu")!.Elements.Add(new() { Text = "Галерея", Action = "navigate", Target = gallery.Id, X = 40, Y = 580 });
        document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "settings").X = 300;
        await document.SaveAsync(); project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var saves = new GameSaveStore(project.Path);
            var window = new GameWindow(project, saves);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(300, Canvas.GetLeft(ScreenButton(window, "settings")));
                ScreenButton(window, "navigate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(ScreenCanvas(window).Children.OfType<TextBlock>(), t => t.Text == "Галерея");
                ScreenButton(window, "back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ScreenButton(window, "settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var toggles = ScreenCanvas(window).Children.OfType<CheckBox>().ToArray();
                var animation = toggles.Single(t => ((ScreenElement)t.Tag).Action == "animation");
                animation.IsChecked = false; animation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var backdrop = Find<AnimatedBackground>(window, "AnimatedBackdrop"); double phase = backdrop.Phase; backdrop.Step(.05); Assert.Equal(phase, backdrop.Phase);
                Assert.False(Find<Border>(window, "MenuOverlay").HasAnimatedProperties);
                var full = toggles.Single(t => ((ScreenElement)t.Tag).Action == "fullscreen");
                full.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                full.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var music = ScreenCanvas(window).Children.OfType<StackPanel>().Single(p => ((ScreenElement)p.Tag).Action == "music_volume").Children.OfType<Slider>().Single();
                music.Value = 0.25; Invoke(window, "RenderOutput");
                using var prefs = JsonDocument.Parse(File.ReadAllText(Path.Combine(saves.DirectoryPath, "preferences.json")));
                Assert.Equal(0.25, prefs.RootElement.GetProperty("MusicVolume").GetDouble());
                Assert.False(prefs.RootElement.GetProperty("Animate").GetBoolean());
                CaptureWindow(window, "settings.png");
                ScreenButton(window, "back").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                CaptureWindow(window, "main-menu.png");
                await Act(window, () => Click(window, "StartButton"));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
            var reopened = new GameWindow(project, saves);
            try
            {
                reopened.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var backdrop = Find<AnimatedBackground>(reopened, "AnimatedBackdrop"); double phase = backdrop.Phase; backdrop.Step(.05); Assert.Equal(phase, backdrop.Phase);
                ScreenButton(reopened, "settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var volume = ScreenCanvas(reopened).Children.OfType<StackPanel>().Single(p => ((ScreenElement)p.Tag).Action == "music_volume").Children.OfType<Slider>().Single();
                Assert.Equal(0.25, volume.Value);
            }
            finally { reopened.Close(); }
        });
    }

    [Fact]
    public async Task Runtime_SaveBrowserCreatesNamedSlot_AndRestoresChosenProgress()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", """{"main_menu":false,"auto_save":false}""");
        var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var store = new GameSaveStore(project.Path); var window = new GameWindow(project, store);
            try
            {
                await Act(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Click(window, "PauseButton");
                await InvokeTask(window, "ShowSaveBrowserAsync");
                Find<TextBox>(window, "SaveSlotName").Text = "Глава первая";
                await Act(window, () => Click(window, "SaveSelectedButton"));
                Assert.Equal("Добро пожаловать! Это базовая сцена.", (await store.LoadAsync("Глава первая")).Frame.Text);
                var slots = await store.ListAsync(); Find<ListBox>(window, "SaveSlotsList").ItemsSource = slots;
                Find<ListBox>(window, "SaveSlotsList").SelectedItem = slots.Single(s => s.Slot == "Глава первая");
                CaptureWindow(window, "save-browser.png");
                Invoke(window, "GoBackScreen"); Click(window, "ResumeButton");
                await Act(window, () => Click(window, "NextButton"));
                Assert.Equal("Нажмите «Далее» или пробел, чтобы продолжить.", Find<TextBlock>(window, "DialogueText").Text);
                Click(window, "PauseButton"); await InvokeTask(window, "ShowSaveBrowserAsync");
                Find<ListBox>(window, "SaveSlotsList").SelectedItem = Find<ListBox>(window, "SaveSlotsList").Items.Cast<GameSaveEntry>().Single(s => s.Slot == "Глава первая");
                await Act(window, () => Click(window, "LoadSelectedButton"));
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task RemovingMainMenu_StartsSceneDirectlyAndDisablesMissingSettings()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var document = await AppearanceDocument.OpenAsync(project); document.Layout.RemoveScreen("main_menu"); document.Layout.RemoveScreen("settings"); await document.SaveAsync();
        project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new GameWindow(project, new GameSaveStore(project.Path));
            try
            {
                await Act(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Click(window, "PauseButton"); Assert.False(ScreenButton(window, "settings").IsEnabled);
                Assert.True(Find<Button>(window, "ResumeButton").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Waves_ArePeriodicAndReducedMotionStopsAnimation_PalettesAndPatternsRender()
    {
        Assert.Equal(AnimatedBackground.WaveY(200, 1280, 120, 24, 0), AnimatedBackground.WaveY(200, 1280, 120, 24, 2 * Math.PI), 8);
        Assert.NotEqual(AnimatedBackground.WaveY(200, 1280, 120, 24, 0), AnimatedBackground.WaveY(200, 1280, 120, 24, Math.PI));
        await Sta(() =>
        {
            var theme = new GameTheme(); var background = new AnimatedBackground();
            background.Configure(theme, true); background.Step(.05); Assert.True(background.Phase > 0);
            background.Configure(theme, false); double phase = background.Phase; background.Step(.05); Assert.Equal(phase, background.Phase);
            foreach (string palette in ThemePalettes.Names.Where(n => n != "custom"))
            {
                ThemePalettes.Apply(theme, palette); Assert.Equal(palette, theme.Palette);
                Assert.Equal(theme.AccentColor, theme.Dialogue.SpeakerColor);
                foreach (string pattern in new[] { "waves", "grid", "dots" })
                {
                    theme.PatternStyle = pattern; background.Configure(theme, false);
                    background.Phase = 0; byte[] first = RenderPixels(background); Capture(background, palette + "-" + pattern + "-0.png", 640, 360);
                    background.Phase = Math.PI; byte[] next = RenderPixels(background); Capture(background, palette + "-" + pattern + "-1.png", 640, 360);
                    Assert.Contains(first, b => b != 0);
                    Assert.False(first.SequenceEqual(next));
                }
            }
            var element = new ScreenElement { MotionX = 50, MotionY = -20, Delay = 0.5, MotionOrder = 2, Duration = 0.3, Loop = true };
            var button = new Button(); ScreenRenderer.Animate(button, element, true);
            Assert.True(((TranslateTransform)button.RenderTransform).HasAnimatedProperties);
            ScreenRenderer.Animate(button, element, false);
            Assert.False(((TranslateTransform)button.RenderTransform).HasAnimatedProperties);
            return Task.CompletedTask;
        });
    }

    private static Canvas ScreenCanvas(GameWindow window) => Find<Canvas>(window, "ScreenCanvas");
    private static Button ScreenButton(GameWindow window, string action) => ScreenCanvas(window).Children.OfType<Button>().Single(b => b.Tag is ScreenElement e && e.Action == action);
    private static T Find<T>(FrameworkElement view, string name) => (T)view.FindName(name);
    private static void Click(FrameworkElement view, string name) => Find<Button>(view, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Invoke(GameWindow window, string method) => typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static Task InvokeTask(GameWindow window, string method) => (Task)typeof(GameWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
    private static async Task Act(GameWindow window, Action action)
    {
        var input = Find<TextBox>(window, "CommandInput"); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DependencyPropertyChangedEventHandler handler = (_, e) => { if ((bool)e.NewValue) ready.TrySetResult(); };
        input.IsEnabledChanged += handler;
        try { action(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { input.IsEnabledChanged -= handler; }
    }
    private static void CaptureWindow(GameWindow window, string name)
    {
        var overlay = Find<Border>(window, "MenuOverlay"); overlay.BeginAnimation(UIElement.OpacityProperty, null); overlay.Opacity = 1;
        Capture((FrameworkElement)window.Content, name, 1280, 720);
    }
    private static void Capture(FrameworkElement view, string name, int width, int height)
    {
        string? path = Environment.GetEnvironmentVariable("KNE_APPEARANCE_RENDER"); if (path == null) return;
        Directory.CreateDirectory(path);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(path, name)); encoder.Save(file);
    }
    private static byte[] RenderPixels(FrameworkElement view)
    {
        view.Measure(new Size(640, 360)); view.Arrange(new Rect(0, 0, 640, 360)); view.UpdateLayout();
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var pixels = new byte[640 * 360 * 4]; bitmap.CopyPixels(pixels, 640 * 4, 0); return pixels;
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () => { try { await action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
