using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KNE;

public partial class GameWindow : Window
{
    private IGameSession runtime;
    private readonly ProjectInfo project;
    private readonly GameSaveStore saves;
    private readonly DispatcherTimer renderTimer;
    private readonly LogBuffer pendingOutput = new();
    private readonly Queue<string> lines = new();
    private CancellationTokenSource? execution;
    private bool ready;
    private bool closed;
    private int textLength;
    private RuntimeWindowSettings? renderedSettings;
    private StoryFrame? renderedStory;
    private ChoicePresentation? choicePresentation;
    private SceneVisuals? renderedVisuals;
    private readonly MediaPlayer music = new();
    private readonly MediaPlayer sound = new();
    private bool loaded;
    private bool startupFailed;
    private bool fullscreen;
    private WindowState previousWindowState;
    private DateTime noticeUntil;
    private readonly bool preview;

    public GameWindow(ProjectInfo project, GameSaveStore? saves = null, bool preview = false)
    {
        InitializeComponent();
        this.project = project;
        this.preview = preview;
        this.saves = saves ?? new GameSaveStore(project.Path);
        LoadPreferences();
        Title = project.Title; InitializePerformance();
        ApplyEngineTheme();
        runtime = new ProcessGameSession(project, QueueOutput, preview: preview);
        InitializeBlockEffects();
        InitializeTriggerInput();
        music.MediaEnded += (_, _) => { if (runtime.Visuals.AudioChannels.All(c => c.Channel != "music") && runtime.Visuals.LoopMusic) { music.Position = TimeSpan.Zero; music.Play(); } };
        music.MediaFailed += (_, e) => QueueOutput("Ошибка музыки: " + e.ErrorException.Message);
        sound.MediaFailed += (_, e) => QueueOutput("Ошибка звука: " + e.ErrorException.Message);
        renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            PlayerClock_Tick, Dispatcher);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        if (project.Engine.Window.Mode == "maximized") WindowState = WindowState.Maximized;
        if (preferences.Fullscreen ?? project.Engine.Window.Mode == "fullscreen") ToggleFullscreen();
        try { GameScripts.Load(project); }
        catch (Exception ex) { ShowStartupError(ex, "Проверка сценариев перед запуском"); return; }
        if (project.Engine.Game.MainMenu && !preview && project.Engine.Screens.Find("main_menu") != null) ShowMenu();
        else await StartGameAsync();
    }

    private void ApplyEngineTheme()
    {
        var settings = project.Engine;
        var theme = settings.Theme;
        var dialogue = theme.Dialogue;
        Width = settings.Window.Width; Height = settings.Window.Height;
        ResizeMode = settings.Window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
        WindowStartupLocation = settings.Window.Position == "center" ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        if (settings.Window.Position == "manual")
        {
            Left = Math.Clamp(settings.Window.Left, SystemParameters.VirtualScreenLeft,
                Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            Top = Math.Clamp(settings.Window.Top, SystemParameters.VirtualScreenTop,
                Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        }
        FontFamily = new FontFamily(theme.FontFamily);
        Resources["GameTextBrush"] = Brush(theme.TextColor);
        Resources["GameAccentBrush"] = Brush(theme.AccentColor);
        Resources["GameButtonBrush"] = Brush(theme.ButtonColor);
        Background = Brush(theme.BackgroundColor);
        DefaultBackground.Background = EnginePresentation.CreateBackground(theme);
        AnimatedBackdrop.Configure(theme, preferences.Animate);
        DefaultBackgroundImage.Source = theme.BackgroundImage == null ? null : LoadImage(ResourcePaths.Resolve(project.Path, theme.BackgroundImage));
        AnimatedBackdrop.Visibility = theme.BackgroundImage == null ? Visibility.Visible : Visibility.Collapsed;
        DialoguePanel.Height = dialogue.Height;
        DialoguePanel.Margin = new Thickness(dialogue.Margin);
        DialoguePanel.Padding = new Thickness(dialogue.Padding);
        DialoguePanel.CornerRadius = new CornerRadius(dialogue.CornerRadius);
        DialoguePanel.Background = Brush(dialogue.BackgroundColor);
        DialoguePanel.BorderBrush = Brush(dialogue.BorderColor);
        SpeakerText.FontSize = dialogue.SpeakerFontSize; SpeakerText.Foreground = Brush(dialogue.SpeakerColor);
        DialogueText.FontSize = dialogue.FontSize; DialogueText.Foreground = Brush(dialogue.TextColor);
        CharacterImage.Width = theme.CharacterWidth;
        MenuTitle.Text = project.Title;
        MenuSubtitle.Text = settings.Game.Subtitle;
        MenuPanel.Width = theme.Menu.Width;
        MenuTitle.FontSize = theme.Menu.TitleFontSize;
        MenuSubtitle.FontSize = theme.Menu.SubtitleFontSize;
        MenuOverlay.Background = Brush(theme.Menu.OverlayColor);
        SaveBrowserPanel.Background = Brush(theme.Dialogue.BackgroundColor);
        MenuScroll.HorizontalAlignment = theme.Menu.Alignment switch
        {
            "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center
        };
        StartButton.Content = settings.Game.StartLabel;
        ContinueButton.Content = settings.Game.ContinueLabel;
        ExitButton.Content = settings.Game.ExitLabel;
        MenuOverlay.Visibility = settings.Game.MainMenu && !preview ? Visibility.Visible : Visibility.Collapsed;
        renderedSettings = settings.RuntimeDefaults(project.Title);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private void ShowStartupError(Exception error, string operation = "Выполнение сценария")
    {
        startupFailed = true;
        ready = false;
        activeScreen = null;
        screenHistory.Clear();
        screenButtons.Clear();
        ScreenCanvas.Children.Clear();
        SceneArea.Visibility = Visibility.Collapsed;
        MenuOverlay.Visibility = Visibility.Collapsed;
        GameControls.Visibility = Visibility.Collapsed;
        DebugOverlay.Visibility = diagnosticConsole ? Visibility.Visible : Visibility.Collapsed;
        NoticePanel.Visibility = Visibility.Collapsed;
        MenuOverlay.IsEnabled = false;
        GameControls.IsEnabled = false;
        CommandInput.IsEnabled = false;
        CommandInput.Visibility = Visibility.Collapsed;
        NextButton.IsEnabled = false;
        ChoicePanel.IsEnabled = false;
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        ContinueButton.IsEnabled = false;
        ResumeButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        LoadAutoButton.IsEnabled = false;
        SaveSelectedButton.IsEnabled = false;
        LoadSelectedButton.IsEnabled = false;
        DeleteSelectedButton.IsEnabled = false;
        AnimatedBackdrop.Configure(project.Engine.Theme, false);
        renderTimer.Stop();
        DialogueEffects.Stop();
        choicePresentation?.Stop();
        CloseAudio();
        sceneEffects?.Stop();
        currentErrorReport = GameErrorReport.Create(project, error, operation);
        StartupErrorMessage.Text = currentErrorReport.ToDisplayText();
        ErrorActionStatus.Text = "";
        RefreshErrorActions();
        RenderConsole();
        StartupErrorOverlay.Visibility = Visibility.Visible;
        StartupErrorExitButton.Focus();
    }

    private void SceneArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (project == null) return;
        if (runtime?.Visuals.UiConfigurations.Any(item => item.Target == "dialogue") == true)
        {
            DialoguePanel.MaxHeight = double.PositiveInfinity;
            return;
        }
        // Reserve space for the top controls and margins when the player shrinks the window.
        DialoguePanel.MaxHeight = Math.Max(120, e.NewSize.Height - 2 * project.Engine.Theme.Dialogue.Margin - 80);
    }

    private async Task StartGameAsync()
    {
        await ExecuteAsync(async () =>
        {
            var candidate = new ProcessGameSession(project, QueueOutput, preview: preview);
            try { await candidate.StartAsync(execution!.Token); }
            catch { await candidate.DisposeAsync(); throw; }
            await ReplaceSessionAsync(candidate);
        }, starting: true, operation: "Запуск игры");
    }

    private async Task ReplaceSessionAsync(IGameSession candidate)
    {
        if (closed) { await candidate.DisposeAsync(); return; }
        await runtime.DisposeAsync();
        if (closed) { await candidate.DisposeAsync(); return; }
        runtime = candidate;
        triggerInput.Reset(); pendingTriggerSignals.Clear();
        gameplayUi?.Reset();
        DialogueEffects.Reset();
        choicePresentation?.Reset();
        clockSuspended = false;
        lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
        ready = true;
        renderedStory = null; renderedSettings = null; renderedVisuals = null;
        CloseAudio();
        sceneEffects?.Stop();
        ShowGameplay();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (startupFailed || execution != null) return;
        if (ready && MessageBox.Show(this, "Начать новую игру? Несохранённый прогресс будет потерян. Существующие сохранения останутся доступны.",
                "Новая игра", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await StartGameAsync();
    }

    private async void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        // Continue uses the most recent automatically recorded progress, with the manual slot as fallback.
        await LoadGameAsync(saves.Exists("auto") ? "auto" : "quick");
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) { if (await TryButtonTriggerAsync(sender)) return; if (execution == null && ready) ShowMenu(); }
    private void ResumeButton_Click(object sender, RoutedEventArgs e) { if (execution == null && ready) ShowGameplay(); }
    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowMenu()
    {
        if (startupFailed) return;
        screenHistory.Clear();
        var layout = project.Engine.Screens.Find(ready ? "pause" : "main_menu");
        if (layout == null)
        {
            if (ready) { ShowGameplay(); return; }
            Notify("Главное меню отключено."); return;
        }
        ShowLayout(layout.Id);
        MenuOverlay.Visibility = Visibility.Visible;
        ResumeButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        DialoguePanel.Visibility = Visibility.Collapsed;
        GameControls.Visibility = Visibility.Collapsed;
        if (!diagnosticConsole) DebugOverlay.Visibility = Visibility.Collapsed;
        PauseAudio();
        DialogueEffects.Stop();
        choicePresentation?.Stop();
        ContinueButton.IsEnabled = saves.Exists("auto") || saves.Exists("quick");
        if (ready) ResumeButton.Focus(); else StartButton.Focus();
    }

    private void ShowGameplay()
    {
        if (startupFailed) return;
        lastSceneTick = System.Diagnostics.Stopwatch.GetTimestamp();
        clockSuspended = false;
        activeScreen = null; screenHistory.Clear();
        MenuOverlay.Visibility = Visibility.Collapsed;
        DialoguePanel.Visibility = Visibility.Visible;
        GameControls.Visibility = Visibility.Visible;
        ResumeAudio();
        NextButton.Focus();
    }

    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = project.Engine.Window.Resizable ? ResizeMode.CanResize : ResizeMode.NoResize;
            WindowState = previousWindowState;
        }
        fullscreen = !fullscreen;
        preferences.Fullscreen = fullscreen; preferencesDirty = true;
    }

    private void Notify(string text)
    {
        if (startupFailed) return;
        NoticeText.Text = text;
        NoticePanel.Visibility = Visibility.Visible;
        noticeUntil = DateTime.UtcNow.AddSeconds(5);
    }

    private async Task ExecuteAsync(Func<Task> action, bool starting = false, string operation = "Выполнение сценария")
    {
        if (closed || startupFailed || execution != null) return;
        clockSuspended = false;
        execution = new CancellationTokenSource();
        string? operationNotice = null;
        StopButton.IsEnabled = true;
        CommandInput.IsEnabled = false;
        NextButton.IsEnabled = false;
        ChoicePanel.IsEnabled = false;
        StartButton.IsEnabled = false; ContinueButton.IsEnabled = false; ResumeButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        if (project.Engine.Performance.LoadingIndicator) Notify(starting ? "Загрузка игры…" : "Выполнение…");
        StatusText.Text = starting ? "Загрузка сценария…" : "Выполнение команды…";
        try
        {
            await action();
            if (closed) return;
            if (starting) ready = true;
            if (ready && project.Engine.Game.AutoSave && (!starting || !saves.Exists("auto")))
            {
                try { await saves.SaveAsync("auto", await runtime.GetCheckpointAsync(execution.Token), execution.Token); }
                catch (Exception ex) when (GameErrorReport.TryGet(ex) == null)
                { operationNotice = "Автосохранение не выполнено: " + ex.Message; QueueOutput(operationNotice); }
            }
            StatusText.Text = "Готово";
            if (operationNotice == null) NoticePanel.Visibility = Visibility.Collapsed; else Notify(operationNotice);
        }
        catch (OperationCanceledException)
        {
            QueueOutput("Выполнение остановлено.");
            StatusText.Text = "Остановлено";
            Notify("Выполнение остановлено.");
        }
        catch (Exception ex)
        {
            QueueOutput("Ошибка: " + ex.Message);
            StatusText.Text = "Ошибка выполнения";
            if (starting || GameErrorReport.TryGet(ex) != null) ShowStartupError(ex, operation);
            else Notify("Ошибка: " + ex.Message);
        }
        finally
        {
            execution.Dispose();
            execution = null;
            _ = Dispatcher.BeginInvoke(new Action(async () => await DrainTriggerSignalsAsync()));
            if (!closed && !startupFailed)
            {
                StopButton.IsEnabled = false;
                CommandInput.IsEnabled = ready;
                RenderOutput();
                StartButton.IsEnabled = true; ResumeButton.IsEnabled = ready; PauseButton.IsEnabled = ready;
                if (DebugOverlay.Visibility == Visibility.Visible && ready) CommandInput.Focus();
                else if (ready && MenuOverlay.Visibility != Visibility.Visible)
                {
                    if (runtime.Story.Kind == "choice") ChoicePanel.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
                    else if (runtime.Story.Kind != "input") NextButton.Focus();
                }
            }
            else if (!closed) RefreshErrorActions();
        }
    }

    private async void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || execution != null || !ready || MenuOverlay.Visibility == Visibility.Visible) return;
        e.Handled = true;
        string command = CommandInput.Text.Trim();
        if (command.Length == 0) return;
        CommandInput.Clear();
        await ExecuteAsync(() => runtime.ProcessCommandAsync(command, execution!.Token), operation: "Команда отладки");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        clockSuspended = true; clockTickOperation?.Cancel();
        execution?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Остановка…";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TryButtonTriggerAsync(sender)) { e.Handled = true; return; }
        if (execution != null || !ready) return;
        bool saved = false;
        await ExecuteAsync(async () =>
        {
            await saves.SaveAsync("quick", await runtime.GetCheckpointAsync(execution!.Token), execution.Token);
            QueueOutput("Игра сохранена.");
            saved = true;
        }, operation: "Сохранение игры");
        if (saved) Notify("Игра сохранена.");
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TryButtonTriggerAsync(sender)) { e.Handled = true; return; }
        string slot = sender is Button { Tag: "auto" } ? "auto" : "quick";
        await LoadGameAsync(slot);
    }

    private async Task LoadGameAsync(string slot)
    {
        if (execution != null) return;
        await ExecuteAsync(async () =>
        {
            var checkpoint = await saves.LoadAsync(slot, execution!.Token);
            var candidate = new ProcessGameSession(project, QueueOutput, preview: preview);
            try { await candidate.RestoreAsync(checkpoint, execution.Token); }
            catch { await candidate.DisposeAsync(); throw; }
            await ReplaceSessionAsync(candidate);
            QueueOutput("Сохранение загружено.");
        }, operation: "Загрузка сохранения");
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TryButtonTriggerAsync(sender)) { e.Handled = true; return; }
        if (NextButton.IsEnabled && MenuOverlay.Visibility != Visibility.Visible)
            await ExecuteAsync(() => runtime.AdvanceAsync(execution!.Token), operation: "Продолжение сценария");
        e.Handled = true;
    }

    private async void ChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TryButtonTriggerAsync(sender)) { e.Handled = true; return; }
        if (sender is Button { Tag: int index } && execution == null && MenuOverlay.Visibility != Visibility.Visible)
            await ExecuteAsync(() => runtime.ChooseAsync(index, execution!.Token), operation: "Выбор ответа");
        e.Handled = true;
    }

    private void Dialogue_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Button && NextButton.IsEnabled) NextButton_Click(NextButton, e);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (startupFailed)
        {
            if (e.Key is Key.F3 or Key.Escape or Key.Space or Key.F11) e.Handled = true;
            return;
        }
        var signal = triggerInput.KeyDown(e);
        if (signal != null && runtime.Story.Triggers.Any(trigger => trigger.Matches(signal)) && ready && execution == null)
        { e.Handled = true; await DispatchTriggerAsync(signal); return; }
        if (e.Key == Key.F11) { ToggleFullscreen(); e.Handled = true; return; }
        if (e.Key == Key.F3 && (project.Engine.Game.DebugEnabled || diagnosticConsole))
        {
            DebugOverlay.Visibility = DebugOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RenderConsole();
            if (DebugOverlay.Visibility == Visibility.Visible) CommandInput.Focus();
            else if (runtime.Story.Kind == "input") PlayerInput.FocusAnswer(); else NextButton.Focus();
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && runtime.Story.Kind == "input" && MenuOverlay.Visibility != Visibility.Visible)
        { PlayerInput.Cancel(); e.Handled = true; return; }
        if (e.Key == Key.Escape && ready && execution == null)
        {
            if (MenuOverlay.Visibility == Visibility.Visible) ShowGameplay(); else ShowMenu();
            e.Handled = true; return;
        }
        if (e.Key == Key.Space && e.OriginalSource is not TextBox && NextButton.IsEnabled)
        {
            NextButton_Click(NextButton, e);
            e.Handled = true;
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        closed = true; preloading?.Cancel(); preloading?.Dispose(); assets.Clear();
        CompositionTarget.Rendering -= PerformanceRendering;
        gameplayUi?.Reset(); gameplayUi = null;
        ScriptScreenCanvas.Children.Clear(); MainScriptScreenCanvas.Children.Clear(); ScreenCanvas.Children.Clear();
        formattedSnapshot = default; formattedVariableBody = renderedVariables = null;
        BackgroundImage.Source = CharacterImage.Source = DefaultBackgroundImage.Source = null;
        blockFlowLifetime.Cancel();
        renderTimer.Stop();
        FlushPreferences();
        CloseAudio();
        sceneEffects?.Stop();
        execution?.Cancel();
        DialogueEffects.Stop();
        choicePresentation?.Stop();
        try { await runtime.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Lua cleanup failed: {0}", ex); }
    }

    private void QueueOutput(string text)
    {
        pendingOutput.Enqueue(text);
    }

    private void RenderOutput()
    {
        if (closed || startupFailed) return;
        FlushPreferences();
        if (NoticePanel.Visibility == Visibility.Visible && execution == null && DateTime.UtcNow >= noticeUntil)
            NoticePanel.Visibility = Visibility.Collapsed;
        RenderConsole();
        var settings = runtime.WindowSettings;
        if (settings != renderedSettings)
        {
            Title = settings.Title;
            // Apply only changed runtime values so a title update does not undo a player's window resize.
            if (settings.Width != renderedSettings?.Width) Width = settings.Width;
            if (settings.Height != renderedSettings?.Height) Height = settings.Height;
            Background = Brush(settings.Background);
            bool defaultBackground = settings.Background == project.Engine.Theme.BackgroundColor;
            DefaultBackground.Background = defaultBackground ? EnginePresentation.CreateBackground(project.Engine.Theme) : Background;
            DefaultBackgroundImage.Visibility = defaultBackground ? Visibility.Visible : Visibility.Collapsed;
            AnimatedBackdrop.Visibility = defaultBackground && project.Engine.Theme.BackgroundImage == null ? Visibility.Visible : Visibility.Collapsed;
            renderedSettings = settings;
        }
        UpdatePerformance();
        RenderStory();
        RenderVisuals();
        ApplyGameplayUi(runtime.Visuals);
        RenderNarrative();
        HandleSceneEnd(runtime.Story);
    }

    private void RenderConsole()
    {
        bool changed = false;
        foreach (string line in pendingOutput.Drain(startupFailed ? 1000 : 100))
        {
            string entry = line + Environment.NewLine;
            lines.Enqueue(entry);
            textLength += entry.Length;
            while (lines.Count > 1000 || textLength > 200_000) textLength -= lines.Dequeue().Length;
            changed = true;
        }
        if (changed)
        {
            var builder = new StringBuilder(textLength);
            foreach (string line in lines) builder.Append(line);
            ConsoleOutput.Text = builder.ToString();
            ConsoleOutputScroll.ScrollToEnd();
        }
        RenderVariables();
    }

    private void RenderStory()
    {
        var frame = runtime.Story;
        if (renderedStory is null || frame.Kind != renderedStory.Kind || frame.Speaker != renderedStory.Speaker ||
            frame.Text != renderedStory.Text || !frame.Options.SequenceEqual(renderedStory.Options))
        {
            SpeakerText.Text = frame.Speaker;
            SpeakerText.Visibility = frame.Speaker.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            DialogueText.Text = frame.Text;
        }
        choicePresentation ??= new(ChoicePanel, ChoiceTimer, button => button.Style = (Style)FindResource(typeof(Button)), ChoiceButton_Click);
        choicePresentation.Display(frame, preferences.Animate && project.Engine.Theme.AnimationEnabled && !clockSuspended &&
            MenuOverlay.Visibility != Visibility.Visible, Brush(project.Engine.Theme.AccentColor));
        PlayerInput.Display(frame);
        PlayerInput.SubmitButton.IsEnabled = PlayerInput.CancelButton.IsEnabled = execution == null;
        renderedStory = frame;
        NextButton.IsEnabled = execution == null && (frame.CanAdvance || HasButtonTrigger("next"));
        NextButton.Visibility = frame.Kind == "input" && !HasButtonTrigger("next") ? Visibility.Collapsed : Visibility.Visible;
        ChoicePanel.IsEnabled = execution == null && frame.Kind == "choice";
        ChoicePanel.Visibility = frame.Kind == "choice" ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = execution == null && ready;
        LoadButton.IsEnabled = execution == null && (saves.Exists("quick") || HasButtonTrigger("load"));
        LoadAutoButton.IsEnabled = execution == null && (saves.Exists("auto") || HasButtonTrigger("auto_save"));
        ContinueButton.IsEnabled = execution == null && (saves.Exists("auto") || saves.Exists("quick"));
        UpdateScreenButtons();
        if (execution == null && ready)
            StatusText.Text = frame.Kind switch { "dialogue" => "Ожидание продолжения", "choice" => "Выберите ответ", "input" => "Введите ответ", "completed" => "История завершена", _ => "Готово" };
    }

    private void RenderVisuals()
    {
        var scene = runtime.Visuals;
        if (scene == renderedVisuals)
        {
            if (scenePolicyChanged) { RenderBlockEffects(scene); scenePolicyChanged = false; }
            return;
        }
        if (scene.Background != renderedVisuals?.Background) BackgroundImage.Source = LoadImage(scene.Background);
        if (scene.Character != renderedVisuals?.Character) CharacterImage.Source = LoadImage(scene.Character);
        CharacterImage.HorizontalAlignment = scene.Position switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        try
        {
            if (!scene.AudioChannels.Any(c => c.Channel == "music") && scene.MusicVersion != renderedVisuals?.MusicVersion)
            {
                music.Close();
                if (scene.Music != null) { music.Open(new Uri(scene.Music)); music.Play(); }
            }
        }
        catch (Exception ex) { QueueOutput("Ошибка музыки: " + ex.Message); }
        try
        {
            if (!scene.AudioChannels.Any(c => c.Channel == "sfx") && scene.Sound != null && scene.SoundVersion != renderedVisuals?.SoundVersion)
            {
                sound.Open(new Uri(scene.Sound)); sound.Play();
            }
        }
        catch (Exception ex) { QueueOutput("Ошибка звука: " + ex.Message); }
        RenderBlockEffects(scene);
        scenePolicyChanged = false;
        bool showDefaultBackground = !scene.BackgroundsCleared && runtime.WindowSettings.Background == project.Engine.Theme.BackgroundColor;
        DefaultBackground.Visibility = DefaultBackgroundImage.Visibility = showDefaultBackground ? Visibility.Visible : Visibility.Collapsed;
        AnimatedBackdrop.Visibility = showDefaultBackground && project.Engine.Theme.BackgroundImage == null ? Visibility.Visible : Visibility.Collapsed;
        renderedVisuals = scene;
    }

    private BitmapImage? LoadImage(string? path)
    {
        if (path == null) return null;
        try
        {
            return assets.Load(path);
        }
        catch (Exception ex) { QueueOutput("Ошибка изображения: " + ex.Message); return null; }
    }
}
