using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private readonly SceneAssetCache assets = new();
    internal void ClearResourceCache() => assets.Clear();
    private ScenePerformanceSettings activeScenePerformance = new();
    private string? performanceScene;
    private string? performanceFunction;
    private bool scenePolicyChanged;
    private CancellationTokenSource? preloading;
    private readonly TextBlock performanceText = new() { Foreground = Brushes.White, FontFamily = new("Consolas"), FontSize = 12 };
    private Border? performanceOverlay;
    private readonly FramePacer performancePacer = new();
    private long lastPerformanceReport;
    private bool? lastFpsOverride;
    internal string ProjectPath => project.Path;
    internal async Task CloseForProjectDeletionAsync() { exitApproved = true; Close(); if (shaders != null) await shaders.DisposeAsync(); await runtime.DisposeAsync(); }
    /// <summary>Applies saved graphics and scene policies to an already open game, without restarting its story.</summary>
    internal void ReloadPerformanceSettings()
    {
        var saved = PerformanceSettingsStore.LoadProject(project.Path);
        var scenes = PerformanceSettingsStore.LoadScenes(project.Path);
        var active = project.Engine.Performance;
        active.Graphics = saved.Graphics; active.Preloading = saved.Preloading; active.PreloadScenes = saved.PreloadScenes;
        active.UnloadPreviousScene = saved.UnloadPreviousScene; active.AudioBuffer = saved.AudioBuffer; active.MaxAsyncTasks = saved.MaxAsyncTasks;
        active.FpsCounter = saved.FpsCounter; active.StatisticsOverlay = saved.StatisticsOverlay;
        active.FpsCorner = saved.FpsCorner; active.LoadingIndicator = saved.LoadingIndicator;
        if (active.DisableDefaultAnimations != saved.DisableDefaultAnimations)
        {
            active.DisableDefaultAnimations = saved.DisableDefaultAnimations;
            ConfigureDefaultBackdrop();
            UpdateDefaultBackdropVisibility(renderedVisuals?.BackgroundsCleared != true &&
                renderedSettings?.Background == project.Engine.Theme.BackgroundColor);
        }
        bool reopen = IsConsoleOpen && (active.ConsoleMode != saved.ConsoleMode || active.ConsoleHotkey != saved.ConsoleHotkey);
        if (reopen) SetConsoleVisible(false, focus: false);
        active.ConsoleMode = saved.ConsoleMode; active.ConsoleHotkey = saved.ConsoleHotkey;
        if (reopen) SetConsoleVisible(true, focus: false);
        project.Engine.ScenePerformance.Scenes = scenes.Scenes;
        project.Engine.ScenePerformance.Functions = scenes.Functions;
        preloading?.Cancel(); preloading?.Dispose(); preloading = null;
        bool textureChanged = GraphicsEnvironment.GetSettings(this).TextureQuality != saved.Graphics.TextureQuality;
        assets.Configure(saved.Graphics); GraphicsEnvironment.SetSettings(this, saved.Graphics);
        if (textureChanged)
        {
            BackgroundImage.Source = LoadImage(runtime.Visuals.Background); CharacterImage.Source = LoadImage(runtime.Visuals.Character); gameplayUi?.Reset();
            DefaultBackgroundImage.Source = project.Engine.Theme.BackgroundImage == null ? null : LoadImage(ResourcePaths.Resolve(project.Path, project.Engine.Theme.BackgroundImage));
            refreshScreenResources = true; scenePolicyChanged = true;
            if (activeScreen is { } screen) ShowLayout(screen);
        }
        performanceScene = null; lastPerformanceReport = 0; performancePacer.Reset();
        UpdatePerformance();
    }
    private void ConfigureDefaultBackdrop() => AnimatedBackdrop.Configure(project.Engine.Theme,
        preferences.Animate && !project.Engine.Performance.DisableDefaultAnimations);

    private void UpdateDefaultBackdropVisibility(bool showDefaultBackground) =>
        AnimatedBackdrop.Visibility = showDefaultBackground && project.Engine.Theme.BackgroundImage == null &&
            !project.Engine.Performance.DisableDefaultAnimations ? Visibility.Visible : Visibility.Collapsed;

    private void PerformanceRendering(object? sender, EventArgs args)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || systemFrozen) return;
        RenderGlobalTimer();
        if (args is RenderingEventArgs frame && performancePacer.ShouldRender(frame.RenderingTime.TotalSeconds, project.Engine.Performance.Graphics.MaxFps)) GraphicsEnvironment.For(this).Presented();
    }
    private void InitializePerformance()
    {
        var settings = project.Engine.Performance;
        GraphicsEnvironment.SetSettings(this, settings.Graphics);
        assets.Configure(settings.Graphics);
        Loaded += (_, _) => { CompositionTarget.Rendering -= PerformanceRendering; CompositionTarget.Rendering += PerformanceRendering; };
        diagnosticConsole = settings.DebugMode && (StandalonePlayer.Current is not { TestBuild: false } || project.Engine.Security.ReleaseDebugConsole);
        DebugOverlay.Visibility = diagnosticConsole && settings.ConsoleMode == "embedded" ? Visibility.Visible : Visibility.Collapsed;
        CommandInput.ToolTip = $"Команда отладки: Enter. {settings.ConsoleHotkey} — закрыть консоль.";
        performanceOverlay = new Border { Child = performanceText, Background = new SolidColorBrush(Color.FromArgb(210, 20, 16, 30)), Padding = new(8), Margin = new(12), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(performanceOverlay, 9);
        ((Grid)Content).Children.Add(performanceOverlay);
    }
    private void UpdatePerformance()
    {
        string? path = runtime.Visuals.Scene?.File ?? (runtime as ProcessGameSession)?.Location?.File;
        string? function = runtime.Visuals.Scene?.Function;
        if (path != null && Path.IsPathFullyQualified(path)) path = Path.GetRelativePath(project.Path, path);
        path = path?.Replace('\\', '/');
        shaders?.SetScene(path, function);
        if (shaders != null) _ = shaders.SetActiveAsync(runtime.Visuals.ShaderSuppressions.Length == 0 ? runtime.Visuals.Shaders : []);
        if (path != performanceScene || function != performanceFunction)
        {
            preloading?.Cancel(); preloading?.Dispose(); preloading = null;
            if (performanceScene != null && project.Engine.Performance.UnloadPreviousScene) assets.RetainForScene(project, runtime.Visuals);
            performanceScene = path; performanceFunction = function; activeScenePerformance = project.Engine.ScenePerformance.For(path, function);
            AnimatedBackdrop.SetQuality(activeScenePerformance.EffectsQuality);
            sceneEffects?.Stop(); scenePolicyChanged = true;
            if (project.Engine.Performance.Preloading && path != null)
            { preloading = new(); _ = PreloadResourcesAsync(path, preloading.Token); }
        }
        var telemetry = GraphicsEnvironment.For(this);
        int objects = (runtime.Visuals.Background == null ? 0 : 1) + (runtime.Visuals.Character == null ? 0 : 1) + ScriptScreenCanvas.Children.Count + MainScriptScreenCanvas.Children.Count;
        if (path != null) SceneMetrics.Record(project.Path, path, runtime.LogicMilliseconds, telemetry.DrawCalls, objects);
        var settings = project.Engine.Performance;
        if (performanceOverlay == null) return;
        bool? fpsOverride = runtime.Visuals.FpsVisible;
        bool showFps = fpsOverride ?? (settings.FpsCounter || settings.StatisticsOverlay);
        if (lastFpsOverride != fpsOverride) { lastFpsOverride = fpsOverride; lastPerformanceReport = 0; }
        performanceOverlay.Visibility = showFps || settings.StatisticsOverlay ? Visibility.Visible : Visibility.Collapsed;
        if (performanceOverlay.Visibility != Visibility.Visible) return;
        performanceOverlay.HorizontalAlignment = settings.FpsCorner.EndsWith("left", StringComparison.Ordinal) ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        performanceOverlay.VerticalAlignment = settings.FpsCorner.StartsWith("top", StringComparison.Ordinal) ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        long now = Stopwatch.GetTimestamp();
        if (lastPerformanceReport != 0 && Stopwatch.GetElapsedTime(lastPerformanceReport, now).TotalSeconds < 1) return;
        lastPerformanceReport = now;
        performanceText.Text = showFps ? $"FPS компоновки: {telemetry.Fps:F1}" : "";
        if (settings.StatisticsOverlay)
        {
            using var process = Process.GetCurrentProcess();
            if (showFps) performanceText.Text += "\n";
            performanceText.Text += $"API: {(telemetry.Backend.Length == 0 ? "WPF / ожидание кадра" : telemetry.Backend)}\nRAM: {process.WorkingSet64 / 1048576d:F1} МБ\nVRAM: н/д; ресурсы KNE: {telemetry.AllocatedBytes / 1048576d:F1} МБ\nDraw Calls слоя: {telemetry.DrawCalls?.ToString() ?? "н/д"}\nЛогика: {runtime.LogicMilliseconds:F2} мс\nОбъектов сцены: {objects}";
            if (telemetry.Error != null) performanceText.Text += "\nРезерв WPF: " + telemetry.Error;
        }
    }
    private async Task PreloadResourcesAsync(string path, CancellationToken token)
    {
        try { await assets.PreloadAsync(project, path, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { EngineDiagnostics.Trace("Предзагрузка ресурсов", error.Message); }
    }
}
