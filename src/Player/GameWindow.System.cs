using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

public partial class GameWindow
{
    private GameSystemServices? systemServices;
    private GameSystemConsent? systemConsent;
    private Task systemInitialization = Task.CompletedTask;
    private GameStorageStatus? saveStorage;
    private string? startupStorageNotice;
    private bool SaveStorageWritable => saveStorage?.Writable != false;
    internal bool CountSystemLaunch { get; init; } = true;
    private bool systemFrozen;
    private Action<string>? scanInput;
    private readonly object playClockLock = new();
    private double playedSeconds;
    private long playStartedAt;

    private ProcessGameSession CreateGameSession() => new(project, QueueOutput, preview: preview)
    {
        SystemHost = InvokeSystemAsync,
        PlayClock = ReadPlayTime,
        InitialDialogueSpeed = preferences.TextSpeed,
        PresentationLanguage = preferences.Language
    };

    private void UpdatePlayClock()
    {
        bool active = ready && !closed && !startupFailed && !systemFrozen &&
            MenuOverlay.Visibility != Visibility.Visible && WindowState != WindowState.Minimized && IsActive;
        lock (playClockLock)
        {
            if (active && playStartedAt == 0) playStartedAt = Stopwatch.GetTimestamp();
            else if (!active && playStartedAt != 0)
            {
                playedSeconds += Stopwatch.GetElapsedTime(playStartedAt).TotalSeconds;
                playStartedAt = 0;
            }
        }
    }

    private double ReadPlayTime()
    {
        lock (playClockLock)
            return playedSeconds + (playStartedAt == 0 ? 0 : Stopwatch.GetElapsedTime(playStartedAt).TotalSeconds);
    }

    private void ResetPlayTime(double seconds = 0)
    {
        lock (playClockLock)
        {
            playStartedAt = 0;
            playedSeconds = seconds;
        }
        UpdatePlayClock();
    }

    private async Task InitializeSystemServicesAsync(bool usesFiles)
    {
        var storage = await Task.Run(() => GameStartupDiagnostics.SelectStorage(project, saves.DirectoryPath), blockFlowLifetime.Token);
        saveStorage = storage.Effective;
        if (storage.Effective.Directory != storage.Requested.Directory)
        {
            saves = new GameSaveStore(project.Path, storage.Effective.Directory);
            LoadPreferences();
            ContinueButton.IsEnabled = saves.Exists("auto") || saves.Exists("quick");
            UpdateScreenButtons();
        }
        if (!storage.Requested.Writable)
        {
            startupStorageNotice = storage.Requested.Message + (saveStorage.Writable ? " Игра использует папку сохранений в LocalAppData." : "");
            QueueOutput("Сохранения: " + startupStorageNotice);
            if (!closed) Notify(startupStorageNotice);
        }
        if (saves.LegacyImportError is { } importError) QueueOutput("Старые сохранения не перенесены. " + importError);
        systemServices = new(project, saves);
        systemConsent = new(systemServices.ConsentDirectory);
        if (CountSystemLaunch && !preview)
        {
            try { await Task.Run(systemServices.RecordLaunch, blockFlowLifetime.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                QueueOutput("Не удалось обновить счётчик запусков в persistent. " + error.Message);
            }
        }
        if (usesFiles) await AskForFileAccessAsync(blockFlowLifetime.Token);
    }

    private static bool UsesFiles(GameScript script)
    {
        if (script.Graph is { } graph)
            return graph.Nodes.Any(node => node.Kind is >= ScriptNodeKind.FileRead and <= ScriptNodeKind.FreezeScan);
        return LuaSyntax.Parse(script.Source).Any(token => token.Kind is LuaTokenKind.Function or LuaTokenKind.Identifier &&
            SystemBlocks.UsesFileSystem(script.Source[token.Start..token.End]));
    }

    private Task<bool> AskForFileAccessAsync(CancellationToken token)
    {
        string scope = project.Engine.Security.FileSystem;
        string detail = scope == "project" ? "Доступ ограничен папкой проекта." : "Проект запрашивает доступ к файлам за пределами своей папки.";
        return AskForSystemAccessAsync("files:" + scope, "Эта игра запрашивает доступ к файловой системе. Разрешить?\n\n" + detail, token);
    }

    private Task<bool> AskForSystemAccessAsync(string category, string message, CancellationToken token) =>
        systemConsent!.AllowAsync(category, () => OnGameThreadAsync(() =>
            MessageBox.Show(this, message, "Разрешение игрока", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes, token), token);

    private Task<T> OnGameThreadAsync<T>(Func<T> operation, CancellationToken token) =>
        Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (closed) throw new OperationCanceledException(token);
            return operation();
        }, System.Windows.Threading.DispatcherPriority.Normal, token).Task;

    private async Task<SystemReply> InvokeSystemAsync(SystemCall call, CancellationToken token)
    {
        if (systemServices == null) return SystemReply.Failure("Системные службы игры недоступны.");
        if (SystemBlocks.UsesFileSystem(call.Operation) && !await AskForFileAccessAsync(token))
            return SystemReply.Failure("Игрок запретил доступ к файловой системе.");
        switch (call.Operation)
        {
            case "save_checkpoint" when !SaveStorageWritable:
                QueueOutput("Именованный слот пропущен. " + saveStorage!.Message);
                return SystemReply.From(false);
            case "play_time": return SystemReply.From(ReadPlayTime() / 60d);
            case "enable_console":
            case "disable_console":
                return await OnGameThreadAsync(() =>
                {
                    scriptConsoleEnabled = call.Operation == "enable_console";
                    RenderConsole();
                    SetConsoleVisible(scriptConsoleEnabled.Value, focus: false);
                    return SystemReply.From(true);
                }, token);
            case "persistent_clear":
                bool confirmed = await OnGameThreadAsync(() => MessageBox.Show(this,
                    "Очистить все глобальные переменные и счётчик запусков этой игры?", "Сброс persistent",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes, token);
                if (confirmed) await systemServices.InvokeAsync(call with { Operation = "persistent_clear_confirmed" }, token);
                return SystemReply.From(confirmed);
            case "minimize_game":
            case "fullscreen_game":
            case "windowed_game":
                return await OnGameThreadAsync(() =>
                {
                    if (call.Operation == "minimize_game") WindowState = WindowState.Minimized;
                    else if (fullscreen != (call.Operation == "fullscreen_game")) ToggleFullscreen();
                    return SystemReply.From(true);
                }, token);
            case "open_browser":
            case "clipboard_write":
            case "clipboard_read":
            case "take_screenshot":
                return await InvokeExternalAsync(call, token);
            case "freeze_and_scan":
                return SystemReply.From(await (await OnGameThreadAsync(() => ScanProjectAsync(call, token), token)));
            case "global_pause":
                await (await OnGameThreadAsync(() => PauseProjectAsync(call, token), token));
                return SystemReply.From(true);
            case "show_video":
                await (await OnGameThreadAsync(() => PlayVideoAsync(call, token), token));
                return SystemReply.From(true);
        }
        if (SystemBlocks.FileFunctions.Contains(call.Operation))
            return await Task.Run(() => systemServices.InvokeFile(call, token), token);
        return await systemServices.InvokeAsync(call, token);
    }

    private async Task<SystemReply> InvokeExternalAsync(SystemCall call, CancellationToken token)
    {
        Uri? link = null;
        if (call.Operation == "open_browser")
        {
            if (!project.Engine.Security.AllowNetwork) return SystemReply.Failure("Внешние ссылки запрещены параметрами проекта.");
            if (!Uri.TryCreate(GameSystemServices.String(call, 0), UriKind.Absolute, out link) ||
                link.Scheme is not ("http" or "https") || string.IsNullOrEmpty(link.Host))
                throw new ArgumentException("Укажите абсолютный URL http или https.");
        }
        if (!await AskForSystemAccessAsync("external", "Эта сцена будет запрашивать доступ к внешним функциям. Разрешить?\n\nБраузер, буфер обмена и снимок окна игры.", token))
            return SystemReply.Failure("Игрок запретил доступ к внешним функциям.");
        if (call.Operation == "take_screenshot")
        {
            string relative = GameSystemServices.String(call, 0);
            if (relative is "" or ".") relative = Path.Combine("screenshots", "game-" + Guid.NewGuid().ToString("N") + ".png");
            string path = PathSafety.ResolveRelative(project.Path, relative);
            if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Снимок окна сохраняется в файл PNG внутри проекта.");
            byte[] image = await OnGameThreadAsync(() =>
            {
                var bitmap = SceneTransitionPresenter.Capture((FrameworkElement)Content) ?? throw new InvalidOperationException("Снимок окна недоступен.");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return stream.ToArray();
            }, token);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            PathSafety.RejectLinks(path);
            await File.WriteAllBytesAsync(path, image, token);
            return SystemReply.From(path);
        }
        return await OnGameThreadAsync(() =>
        {
            if (call.Operation == "open_browser")
            {
                using var process = Process.Start(new ProcessStartInfo(link!.AbsoluteUri) { UseShellExecute = true });
            }
            else if (call.Operation == "clipboard_write") Clipboard.SetText(GameSystemServices.String(call, 0));
            else
            {
                string text = Clipboard.GetText();
                if (text.Length > 262_144) throw new InvalidDataException("Текст в буфере обмена слишком длинный.");
                return SystemReply.From(text);
            }
            return SystemReply.From(true);
        }, token);
    }

    private async Task<string> ScanProjectAsync(SystemCall call, CancellationToken token)
    {
        string resume = GameSystemServices.String(call, 0, "left");
        if (resume is not ("left" or "right" or "key" or "timer"))
            throw new ArgumentException("Разморозка: left, right, key или timer.");
        double seconds = 5;
        if (resume == "timer" && call.Arguments.GetArrayLength() > 1 &&
            (!call.Arguments[1].TryGetDouble(out seconds) || !double.IsFinite(seconds) || seconds is < .1 or > 3600))
            throw new ArgumentException("Таймер разморозки: от 0,1 до 3600 секунд.");

        var report = new TextBlock { Text = "Проверка проекта…", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(20) };
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(245, 26, 22, 34)),
            Child = new ScrollViewer { Content = report, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Focusable = true
        };
        var root = (Grid)Content;
        Panel.SetZIndex(overlay, int.MaxValue);
        root.Children.Add(overlay);
        overlay.Focus();
        systemFrozen = true;
        UpdatePlayClock();
        PauseAudio();
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        overlay.PreviewMouseDown += (_, args) =>
        {
            args.Handled = true;
            scanInput?.Invoke(args.ChangedButton == MouseButton.Left ? "left" : args.ChangedButton == MouseButton.Right ? "right" : "");
        };
        try
        {
            var telemetry = GraphicsEnvironment.For(this);
            using var process = Process.GetCurrentProcess();
            string statistics = FormattableString.Invariant($"FPS: {telemetry.Fps:F1} · RAM: {process.WorkingSet64 / 1048576d:F1} МиБ · логика: {runtime.LogicMilliseconds:F2} мс · API: {telemetry.Backend}");
            string result = await Task.Run(() => ProjectSystemScan.RunAsync(project, statistics, token), token);
            report.Text = result + "\n\n" + (resume switch
            {
                "left" => "ЛКМ — продолжить.", "right" => "ПКМ — продолжить.",
                "key" => "Любая клавиша — продолжить.", _ => $"Продолжение через {seconds.ToString(CultureInfo.CurrentCulture)} с."
            });
            scanInput = signal => { if (signal == resume) resumed.TrySetResult(); };
            if (resume == "timer") await Task.Delay(TimeSpan.FromSeconds(seconds), token);
            else await resumed.Task.WaitAsync(token);
            return result;
        }
        finally
        {
            scanInput = null;
            root.Children.Remove(overlay);
            systemFrozen = false;
            UpdatePlayClock();
            if (!closed && MenuOverlay.Visibility != Visibility.Visible) ResumeAudio();
        }
    }

    private async Task ResetSystemSessionAsync(bool restart)
    {
        if (closed) return;
        try
        {
            if (restart)
            {
                FlushPreferences();
                if (StandalonePlayer.Current != null)
                {
                    var info = EngineLaunch.CreateStartInfo("--system-restart");
                    info.WindowStyle = ProcessWindowStyle.Normal;
                    using var process = Process.Start(info) ?? throw new IOException("Не удалось перезапустить игру.");
                    exitApproved = true;
                    Close();
                    return;
                }
                var loadedProject = await ProjectLoader.LoadProjectAsync(project.Path, blockFlowLifetime.Token);
                loadedProject.ApplicationPlugins = project.ApplicationPlugins;
                var replacement = new GameWindow(loadedProject, preview: preview) { CountSystemLaunch = false };
                if (Application.Current?.MainWindow == this) Application.Current.MainWindow = replacement;
                replacement.Show();
                exitApproved = true;
                Close();
                return;
            }
            ready = false;
            await runtime.DisposeAsync();
            if (closed) return;
            runtime = CreateGameSession();
            rollback.Clear();
            autosave.Reset();
            sceneSaves.Reset();
            ResetTriggerInput();
            pendingTriggerSignals.Clear();
            gameplayUi?.Reset();
            DialogueEffects.Reset();
            choicePresentation?.Reset();
            sceneEffects?.Stop();
            renderedStory = null;
            renderedVisuals = null;
            ResetPlayTime();
            if (project.Engine.Screens.Find("main_menu") != null) ShowMenu();
            else await StartGameAsync();
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception error) { if (!closed) ShowStartupError(error, "Перезапуск игровой сессии"); }
    }
}
