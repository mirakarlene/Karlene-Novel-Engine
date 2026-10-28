using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace KNE
{
    public partial class App : Application
    {
        private EngineDiagnostics? diagnostics;
        private int recovering;
        private int pluginRecoveryQueued;

        protected override async void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Contains("--disable-plugins", StringComparer.Ordinal))
                Environment.SetEnvironmentVariable(EngineLaunch.PluginsEnvironment, "1");
            if (e.Args.Length == 4 && e.Args[0] == "--engine-watchdog")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { EngineLaunch.WatchAsync(int.Parse(e.Args[1], CultureInfo.InvariantCulture), long.Parse(e.Args[2], CultureInfo.InvariantCulture), e.Args[3]).GetAwaiter().GetResult(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Engine watchdog: {0}", error); }
                Shutdown(); return;
            }
            if (e.Args.Length == 2 && e.Args[0] == "--engine-recovery")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try
                {
                    var journal = EngineDiagnostics.Read(e.Args[1]);
                    var report = journal.Failure ?? EngineDiagnostics.RecordUnexpectedExit(e.Args[1], -1)!;
                    ShowRecovery(report, Path.ChangeExtension(e.Args[1], ".txt"));
                }
                catch (Exception error) { MessageBox.Show("Не удалось прочитать отчёт о сбое движка. " + error.Message, "Ошибка диагностики"); Shutdown(1); }
                return;
            }
            if (e.Args.Length == 1 && e.Args[0] == "--analyze-syntax")
            {
                int exitCode = Task.Run(SyntaxCheckWorker.RunAsync).GetAwaiter().GetResult();
                Shutdown(exitCode); return;
            }
            if (e.Args.Length is 2 or 3 or 4 && e.Args[0] == "--lua-worker")
            {
                int exitCode = Task.Run(() => LuaWorker.RunAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null, e.Args.Length == 4 && e.Args[3] == "--preview")).GetAwaiter().GetResult();
                Shutdown(exitCode);
                return;
            }
#if KNE_PLAYER
            ProjectInfo standaloneProject;
            try { standaloneProject = await StandalonePlayer.LoadAsync(); }
            catch (Exception error) { MessageBox.Show(error.Message, "Ошибка запуска игры"); Shutdown(1); return; }
            if (e.Args.Contains("--verify-player", StringComparer.Ordinal))
            {
                try
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    var window = new GameWindow(standaloneProject, StandalonePlayer.Saves(standaloneProject));
                    await window.CloseForProjectDeletionAsync(); // Validate WPF resources without displaying a window.
                    await using var session = new ProcessGameSession(standaloneProject, _ => { }); await session.StartAsync();
                    var checkpoint = await session.GetCheckpointAsync(); await session.RestoreAsync(checkpoint);
                    using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { standaloneProject.Title, session.Story, Saves = StandalonePlayer.Current!.SavesDirectory(AppContext.BaseDirectory), SaveIdentity = checkpoint.ProjectPath })); Shutdown();
                }
                catch (Exception error) { using var output = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true }; output.WriteLine(error); Shutdown(1); }
                return;
            }
#endif
            PluginFailure.Detected += PresentPluginFailure;
            DispatcherUnhandledException += (_, problem) =>
            {
                problem.Handled = true;
                if (Volatile.Read(ref pluginRecoveryQueued) != 0) return;
                PresentFailure(problem.Exception, "Необработанная ошибка интерфейса");
            };
            AppDomain.CurrentDomain.UnhandledException += (_, problem) =>
            {
                // The CLR will terminate this process. Persist now; the separate helper presents the report.
                diagnostics?.Report(problem.ExceptionObject as Exception ?? new Exception("Неизвестная ошибка фонового потока"), "Необработанная ошибка фонового потока");
            };
            TaskScheduler.UnobservedTaskException += (_, problem) =>
            {
                problem.SetObserved();
                if (!Dispatcher.HasShutdownStarted)
                    Dispatcher.BeginInvoke(new Action(() => PresentFailure(problem.Exception, "Необработанная ошибка задачи")));
            };
            try
            {
                using var process = Process.GetCurrentProcess();
                string directory = StandalonePlayer.DiagnosticsDirectory;
                diagnostics = new(Path.Combine(directory, $"engine-{process.Id}-{Guid.NewGuid():N}.json"), process.Id, process.StartTime.ToUniversalTime().Ticks, !EngineLaunch.PluginsEnabled);
                EngineDiagnostics.Current = diagnostics;
                try
                {
                    using (Process.Start(EngineLaunch.CreateStartInfo("--engine-watchdog", process.Id.ToString(CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), diagnostics.SessionPath))) { }
                }
                catch (Exception helperError) { diagnostics.Record("Наблюдатель недоступен", helperError.Message); }
                base.OnStartup(e);
#if KNE_PLAYER
                MainWindow = new GameWindow(standaloneProject, StandalonePlayer.Saves(standaloneProject));
#else
                MainWindow = new MainWindow();
#endif
                if (!EngineLaunch.PluginsEnabled) MainWindow.Title += " · Без плагинов";
                MainWindow.Show();
                diagnostics.Record("Интерфейс готов");
            }
            catch (Exception error) { PresentFailure(error, "Ошибка запуска движка"); }
        }

        private void PresentPluginFailure(PluginFailureException error)
        {
            if (Dispatcher.HasShutdownStarted || Volatile.Read(ref recovering) != 0 || Interlocked.Exchange(ref pluginRecoveryQueued, 1) != 0) return;
            // Persist before dispatching: an unhandled plugin exception on another thread may terminate the CLR first.
            diagnostics?.Report(error, "Ошибка плагина");
            Dispatcher.BeginInvoke(new Action(() => PresentFailure(error, "Ошибка плагина")), System.Windows.Threading.DispatcherPriority.Send);
        }

        private void PresentFailure(Exception error, string kind)
        {
            if (Interlocked.Exchange(ref recovering, 1) != 0) { diagnostics?.Report(error, "Повторный сбой окна восстановления"); Shutdown(1); return; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = diagnostics?.Report(error, kind) ?? new EngineFailureReport(DateTimeOffset.UtcNow, kind, error.Message, error.GetType().FullName ?? "", error.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, Environment.ProcessId, !EngineLaunch.PluginsEnabled, "Не открыт", GC.GetTotalMemory(false), []);
            if (diagnostics != null && diagnostics.PersistenceError == null)
            {
                try
                {
                    // Leave the damaged dispatcher and its timers/workers behind before presenting recovery.
                    using var recoveryProcess = Process.Start(EngineLaunch.RecoveryInfo(diagnostics.SessionPath))
                        ?? throw new IOException("Не удалось запустить окно восстановления.");
                    Shutdown(1); return;
                }
                catch (Exception launchError) { diagnostics.Record("Запуск окна восстановления недоступен", launchError.Message); }
            }
            foreach (Window window in Windows) { window.IsEnabled = false; window.Hide(); }
            ShowRecovery(report, diagnostics?.PersistenceError is { } persistenceError ? "Отчёт не сохранён: " + persistenceError : diagnostics?.ReportPath ?? "Отчёт не удалось сохранить");
        }

        private void ShowRecovery(EngineFailureReport report, string reportPath)
        {
            var recovery = new EngineRecoveryWindow(report, reportPath, action =>
            {
                if (action != EngineRecoveryAction.Close)
                    using (Process.Start(EngineLaunch.RestartInfo(action == EngineRecoveryAction.RestartWithoutPlugins))) { }
                Shutdown();
            });
            MainWindow = recovery;
            recovery.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            PluginFailure.Detected -= PresentPluginFailure;
            diagnostics?.Complete();
            EngineDiagnostics.Current = null;
            base.OnExit(e);
        }
    }
}
