using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class EngineWindowTests
{
    [Fact]
    public async Task NewGame_MenuWaitsForStart_ThenPauseResumePreservesScene()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        await OnStaAsync(async () =>
        {
            var saves = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves"));
            var window = new GameWindow(project, saves);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(1280, window.Width); Assert.Equal(720, window.Height);
                Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.False(Find<Button>(window, "ContinueButton").IsEnabled);
                Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
                Assert.Equal("", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
                Assert.False(saves.Exists("auto"));
                Assert.Equal(project.Title, Find<TextBlock>(window, "MenuTitle").Text);
                Capture(window, "main-menu.png");
                await ActAsync(window, () => Click(window, "StartButton"));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "DialoguePanel").Visibility);
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
                Assert.True(saves.Exists("auto"));
                Capture(window, "dialogue.png");
                Click(window, "PauseButton");
                Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Button>(window, "ResumeButton").Visibility);
                Click(window, "NextButton");
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
                Click(window, "ResumeButton");
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.Equal("Нажмите «Далее» или пробел, чтобы продолжить.", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ThemeAndWindowOverrides_AreAppliedToRealControls()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/window.json", """{"width":900,"height":600,"position":"manual","left":20,"top":30,"resizable":false}""");
        fixture.Write("engine/theme.json", """{"pattern":false,"background_color":"#FF102030","accent_color":"#FF112233","text_color":"#FFCCDDEE","font_family":"Georgia","character_width":350,"menu":{"width":360,"title_font_size":32,"subtitle_font_size":18,"alignment":"left","overlay_color":"#CC112233"},"dialogue":{"height":200,"margin":10,"padding":20,"corner_radius":6,"font_size":28,"speaker_font_size":24,"background_color":"#EE334455","border_color":"#FF456789","speaker_color":"#FFFFCC88","text_color":"#FFEEDDCC"}}""");
        fixture.Write("engine/game.json", """{"start_label":"Читать","continue_label":"Вернуться","exit_label":"Закрыть","subtitle":"Тестовая история"}""");
        var project = await fixture.LoadAsync();
        await OnStaAsync(() =>
        {
            var window = new GameWindow(project);
            try
            {
                Assert.Equal(900, window.Width); Assert.Equal(600, window.Height);
                Assert.Equal(20, window.Left); Assert.Equal(30, window.Top);
                Assert.Equal(ResizeMode.NoResize, window.ResizeMode);
                Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
                Assert.Equal("Georgia", window.FontFamily.Source);
                Assert.Equal("Читать", Find<Button>(window, "StartButton").Content);
                Assert.Equal("Вернуться", Find<Button>(window, "ContinueButton").Content);
                Assert.Equal("Закрыть", Find<Button>(window, "ExitButton").Content);
                Assert.Equal("Тестовая история", Find<TextBlock>(window, "MenuSubtitle").Text);
                Assert.Equal(360, Find<StackPanel>(window, "MenuPanel").Width);
                Assert.Equal(32, Find<TextBlock>(window, "MenuTitle").FontSize);
                Assert.Equal(18, Find<TextBlock>(window, "MenuSubtitle").FontSize);
                Assert.Equal(HorizontalAlignment.Left, Find<ScrollViewer>(window, "MenuScroll").HorizontalAlignment);
                Assert.Equal(Color.FromArgb(204, 17, 34, 51), ((SolidColorBrush)Find<Border>(window, "MenuOverlay").Background).Color);
                var panel = Find<Border>(window, "DialoguePanel");
                Assert.Equal(200, panel.Height); Assert.Equal(new Thickness(10), panel.Margin);
                Assert.Equal(new Thickness(20), panel.Padding); Assert.Equal(new CornerRadius(6), panel.CornerRadius);
                Assert.Equal(Color.FromArgb(238, 51, 68, 85), ((SolidColorBrush)panel.Background).Color);
                Assert.Equal(Color.FromRgb(69, 103, 137), ((SolidColorBrush)panel.BorderBrush).Color);
                Assert.Equal(28, Find<TextBlock>(window, "DialogueText").FontSize);
                Assert.Equal(Color.FromRgb(238, 221, 204), ((SolidColorBrush)Find<TextBlock>(window, "DialogueText").Foreground).Color);
                Assert.Equal(24, Find<TextBlock>(window, "SpeakerText").FontSize);
                Assert.Equal(Color.FromRgb(255, 204, 136), ((SolidColorBrush)Find<TextBlock>(window, "SpeakerText").Foreground).Color);
                Assert.Equal(350, Find<Image>(window, "CharacterImage").Width);
                Assert.Equal(Color.FromRgb(16, 32, 48), ((SolidColorBrush)Find<Border>(window, "DefaultBackground").Background).Color);
                return Task.CompletedTask;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("windowed", WindowState.Normal, WindowStyle.SingleBorderWindow)]
    [InlineData("maximized", WindowState.Maximized, WindowStyle.SingleBorderWindow)]
    [InlineData("fullscreen", WindowState.Maximized, WindowStyle.None)]
    public async Task StartupMode_AppliesAndFullscreenCanReturnToWindow(string mode, WindowState state, WindowStyle style)
    {
        using var fixture = new TempProject();
        fixture.Write("engine/window.json", "{\"mode\":\"" + mode + "\"}");
        var project = await fixture.LoadAsync();
        await OnStaAsync(() =>
        {
            var window = new GameWindow(project);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(state, window.WindowState); Assert.Equal(style, window.WindowStyle);
                if (mode != "fullscreen") Invoke(window, "ToggleFullscreen");
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Invoke(window, "ToggleFullscreen");
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
                Assert.Equal(mode == "maximized" ? WindowState.Maximized : WindowState.Normal, window.WindowState);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                return Task.CompletedTask;
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("quick")]
    public async Task ContinueMenu_LoadsAutoOrManualFallbackWithoutRunningNewGame(string slot)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'first'); state.progress = 7; d(nil,'saved progress') end");
        var project = await fixture.LoadAsync();
        var saves = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves"));
        await using (var runtime = new ProcessGameSession(project, _ => { }))
        {
            await runtime.StartAsync();
            if (slot == "auto") await saves.SaveAsync("quick", await runtime.GetCheckpointAsync());
            await runtime.AdvanceAsync();
            await saves.SaveAsync(slot, await runtime.GetCheckpointAsync());
        }
        await OnStaAsync(async () =>
        {
            var window = new GameWindow(project, saves);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.True(Find<Button>(window, "ContinueButton").IsEnabled);
                await ActAsync(window, () => Click(window, "ContinueButton"));
                Assert.Equal("saved progress", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(7, (await saves.LoadAsync(slot)).State.GetProperty("progress").GetInt32());
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task DirectStart_AutosaveCanBeDisabled_ManualSaveStillWorks()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", """{"main_menu":false,"auto_save":false}""");
        var project = await fixture.LoadAsync();
        await OnStaAsync(async () =>
        {
            var saves = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves"));
            var window = new GameWindow(project, saves);
            try
            {
                await ActAsync(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.False(saves.Exists("auto"));
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.False(saves.Exists("auto"));
                await ActAsync(window, () => Click(window, "SaveButton"));
                Assert.True(saves.Exists("quick"));
                Assert.Equal("Нажмите «Далее» или пробел, чтобы продолжить.", (await saves.LoadAsync("quick")).Frame.Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task InvalidContinue_LeavesMenuUsableAndShowsVisibleError()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var saves = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves"));
        Directory.CreateDirectory(saves.DirectoryPath);
        File.WriteAllText(Path.Combine(saves.DirectoryPath, "quick.json"), "broken json");
        await OnStaAsync(async () =>
        {
            var window = new GameWindow(project, saves);
            try
            {
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var enabled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var start = Find<Button>(window, "StartButton");
                start.IsEnabledChanged += (_, e) => { if ((bool)e.NewValue) enabled.TrySetResult(); };
                Click(window, "ContinueButton");
                await enabled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "NoticePanel").Visibility);
                Assert.StartsWith("Ошибка:", Find<TextBlock>(window, "NoticeText").Text);
                Assert.False(Find<TextBox>(window, "CommandInput").IsEnabled);
                await ActAsync(window, () => Click(window, "StartButton"));
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task DefaultImage_FallsBackAfterSceneClearsBackground_LuaColorOverridesPattern()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", """{"main_menu":false}""");
        fixture.Write("engine/theme.json", """{"background_image":"game/backgrounds/default.png"}""");
        fixture.Write("game/scenes/main.krl", "function main() background('game/backgrounds/default.png'); d(nil,'image'); background(nil); d(nil,'default'); set_bg_color('#FF102030'); d(nil,'solid'); set_bg_color('#FF201134'); d(nil,'pattern') end");
        await OnStaAsync(async () =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
            using (var file = File.Create(Path.Combine(fixture.ProjectPath, "game", "backgrounds", "default.png"))) encoder.Save(file);
            var project = await fixture.LoadAsync();
            var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves")));
            try
            {
                Assert.IsType<DrawingBrush>(Find<Border>(window, "DefaultBackground").Background);
                Assert.Equal(1, ((BitmapSource)Find<Image>(window, "DefaultBackgroundImage").Source).PixelWidth);
                await ActAsync(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.NotNull(Find<Image>(window, "BackgroundImage").Source);
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.Null(Find<Image>(window, "BackgroundImage").Source);
                Assert.Equal(Visibility.Visible, Find<Image>(window, "DefaultBackgroundImage").Visibility);
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.Equal(Color.FromRgb(16, 32, 48), ((SolidColorBrush)Find<Border>(window, "DefaultBackground").Background).Color);
                Assert.Equal(Visibility.Collapsed, Find<Image>(window, "DefaultBackgroundImage").Visibility);
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.IsType<DrawingBrush>(Find<Border>(window, "DefaultBackground").Background);
                Assert.Equal(Visibility.Visible, Find<Image>(window, "DefaultBackgroundImage").Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PatternBrush_IsFrozenAndContainsEditableColors()
    {
        await OnStaAsync(() =>
        {
            var theme = new GameTheme { BackgroundColor = "#FF102030", PatternColor = "#FF405060" };
            var brush = Assert.IsType<DrawingBrush>(EnginePresentation.CreateBackground(theme));
            Assert.True(brush.IsFrozen); Assert.Equal(TileMode.Tile, brush.TileMode);
            Assert.Equal(new Rect(0, 0, 240, 240), brush.Viewport);
            var group = Assert.IsType<DrawingGroup>(brush.Drawing);
            var background = Assert.IsType<GeometryDrawing>(group.Children[0]);
            Assert.Equal(Color.FromRgb(16, 32, 48), ((SolidColorBrush)background.Brush).Color);
            var ornament = Assert.IsType<GeometryDrawing>(group.Children[1]);
            Assert.Equal(Color.FromRgb(64, 80, 96), ((SolidColorBrush)ornament.Pen.Brush).Color);
            theme.Pattern = false;
            Assert.Equal(Color.FromRgb(16, 32, 48), Assert.IsType<SolidColorBrush>(EnginePresentation.CreateBackground(theme)).Color);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DebugHotkey_RespectsProjectSetting_AndEscapePausesGameplay(bool enabled)
    {
        using var fixture = new TempProject();
        fixture.Write("engine/game.json", "{\"main_menu\":false,\"debug_enabled\":" + (enabled ? "true" : "false") + "}");
        var project = await fixture.LoadAsync();
        await OnStaAsync(async () =>
        {
            using var source = new HwndSource(new HwndSourceParameters("KNE keyboard test") { Width = 1, Height = 1, WindowStyle = 0 });
            var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(fixture.Root, "saves")));
            void Key(Key key)
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                window.RaiseEvent(args);
            }
            try
            {
                await ActAsync(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Key(System.Windows.Input.Key.F3);
                Assert.Equal(enabled ? Visibility.Visible : Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
                Key(System.Windows.Input.Key.F3);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "DebugOverlay").Visibility);
                Key(System.Windows.Input.Key.Escape);
                Assert.Equal(Visibility.Visible, Find<Border>(window, "MenuOverlay").Visibility);
                Key(System.Windows.Input.Key.Space);
                Assert.Equal("Добро пожаловать! Это базовая сцена.", Find<TextBlock>(window, "DialogueText").Text);
                Key(System.Windows.Input.Key.Escape);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                Key(System.Windows.Input.Key.F11);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Key(System.Windows.Input.Key.F11);
                Assert.Equal(WindowStyle.SingleBorderWindow, window.WindowStyle);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ScenePreview_SkipsInitialMenu_AndTitleChangePreservesUserResize()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'first'); set_window_title('New title'); d(nil,'second') end");
        var project = await fixture.LoadAsync();
        await OnStaAsync(async () =>
        {
            var window = new GameWindow(project, new GameSaveStore(project.Path, Path.Combine(fixture.Root, "preview")), preview: true);
            try
            {
                await ActAsync(window, () => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.Equal("first", Find<TextBlock>(window, "DialogueText").Text);
                Assert.Equal(Visibility.Collapsed, Find<Border>(window, "MenuOverlay").Visibility);
                window.Width = 1100; window.Height = 650;
                await ActAsync(window, () => Click(window, "NextButton"));
                Assert.Equal("New title", window.Title);
                Assert.Equal(1100, window.Width); Assert.Equal(650, window.Height);
                Assert.Equal("second", Find<TextBlock>(window, "DialogueText").Text);
            }
            finally { window.Close(); }
        });
    }

    private static T Find<T>(GameWindow window, string name) => (T)window.FindName(name);
    private static void Click(GameWindow window, string name) => Find<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Invoke(GameWindow window, string name) => typeof(GameWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static async Task ActAsync(GameWindow window, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = Find<TextBox>(window, "CommandInput");
        DependencyPropertyChangedEventHandler handler = (_, e) => { if ((bool)e.NewValue) completion.TrySetResult(); };
        input.IsEnabledChanged += handler;
        try { action(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { input.IsEnabledChanged -= handler; }
    }

    private static void Capture(GameWindow window, string file)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_RENDER_DIR");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1280, 720)); content.Arrange(new Rect(0, 0, 1280, 720)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream);
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
