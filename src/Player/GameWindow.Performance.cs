using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private readonly SceneAssetCache assets = new();
    private ScenePerformanceSettings activeScenePerformance = new();
    private string? performanceScene;
    private bool scenePolicyChanged;
    private CancellationTokenSource? preloading;
    private readonly TextBlock performanceText = new() { Foreground = Brushes.White, FontFamily = new("Consolas"), FontSize = 12 };
    private Border? performanceOverlay;
    private readonly FramePacer performancePacer = new();
    private long lastPerformanceReport;
    internal string ProjectPath => project.Path;
    /// <summary>Applies saved graphics and scene policies to an already open game, without restarting its story.</summary>
    internal void ReloadPerformanceSettings()
    {
        var saved = PerformanceSettingsStore.LoadProject(project.Path);
        var scenes = PerformanceSettingsStore.LoadScenes(project.Path);
        var active = project.Engine.Performance;
        active.Graphics = saved.Graphics; active.Preloading = saved.Preloading;
        active.FpsCounter = saved.FpsCounter; active.StatisticsOverlay = saved.StatisticsOverlay;
        active.FpsCorner = saved.FpsCorner; active.LoadingIndicator = saved.LoadingIndicator;
        project.Engine.ScenePerformance.Scenes = scenes.Scenes;
        preloading?.Cancel(); preloading?.Dispose(); preloading = null;
        assets.Configure(saved.Graphics); GraphicsEnvironment.SetSettings(this, saved.Graphics);
        performanceScene = null; lastPerformanceReport = 0; performancePacer.Reset();
        UpdatePerformance();
    }
    private void PerformanceRendering(object? sender, EventArgs args)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !(project.Engine.Performance.FpsCounter || project.Engine.Performance.StatisticsOverlay)) return;
        if (args is RenderingEventArgs frame && performancePacer.ShouldRender(frame.RenderingTime.TotalSeconds, project.Engine.Performance.Graphics.MaxFps)) GraphicsEnvironment.For(this).Presented();
    }
    private void InitializePerformance()
    {
        var settings = project.Engine.Performance;
        GraphicsEnvironment.SetSettings(this, settings.Graphics);
        assets.Configure(settings.Graphics);
        Loaded += (_, _) => { CompositionTarget.Rendering -= PerformanceRendering; CompositionTarget.Rendering += PerformanceRendering; };
        diagnosticConsole = settings.DebugMode;
        DebugOverlay.Visibility = diagnosticConsole ? Visibility.Visible : Visibility.Collapsed;
        performanceOverlay = new Border { Child = performanceText, Background = new SolidColorBrush(Color.FromArgb(210, 20, 16, 30)), Padding = new(8), Margin = new(12), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Panel.SetZIndex(performanceOverlay, 9);
        ((Grid)Content).Children.Add(performanceOverlay);
    }
    private void UpdatePerformance()
    {
        string? path = (runtime as ProcessGameSession)?.Location?.File;
        if (path != null && Path.IsPathFullyQualified(path)) path = Path.GetRelativePath(project.Path, path);
        path = path?.Replace('\\', '/');
        if (path != performanceScene)
        {
            performanceScene = path; activeScenePerformance = project.Engine.ScenePerformance.For(path);
            AnimatedBackdrop.SetQuality(activeScenePerformance.EffectsQuality);
            sceneEffects?.Stop(); scenePolicyChanged = true;
            preloading?.Cancel(); preloading?.Dispose(); preloading = null;
            if (project.Engine.Performance.Preloading && path != null)
            { preloading = new(); _ = PreloadResourcesAsync(path, preloading.Token); }
        }
        var telemetry = GraphicsEnvironment.For(this);
        int objects = (runtime.Visuals.Background == null ? 0 : 1) + (runtime.Visuals.Character == null ? 0 : 1) + ScriptScreenCanvas.Children.Count + MainScriptScreenCanvas.Children.Count;
        if (path != null) SceneMetrics.Record(project.Path, path, runtime.LogicMilliseconds, telemetry.DrawCalls, objects);
        var settings = project.Engine.Performance;
        if (performanceOverlay == null) return;
        performanceOverlay.Visibility = settings.FpsCounter || settings.StatisticsOverlay ? Visibility.Visible : Visibility.Collapsed;
        if (performanceOverlay.Visibility != Visibility.Visible) return;
        performanceOverlay.HorizontalAlignment = settings.FpsCorner.EndsWith("left", StringComparison.Ordinal) ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        performanceOverlay.VerticalAlignment = settings.FpsCorner.StartsWith("top", StringComparison.Ordinal) ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        long now = Stopwatch.GetTimestamp();
        if (lastPerformanceReport != 0 && Stopwatch.GetElapsedTime(lastPerformanceReport, now).TotalSeconds < 1) return;
        lastPerformanceReport = now;
        performanceText.Text = $"FPS компоновки: {telemetry.Fps:F1}";
        if (settings.StatisticsOverlay)
        {
            using var process = Process.GetCurrentProcess();
            performanceText.Text += $"\nAPI: {(telemetry.Backend.Length == 0 ? "WPF / ожидание кадра" : telemetry.Backend)}\nRAM: {process.WorkingSet64 / 1048576d:F1} МБ\nVRAM: н/д; ресурсы KNE: {telemetry.AllocatedBytes / 1048576d:F1} МБ\nDraw Calls слоя: {telemetry.DrawCalls?.ToString() ?? "н/д"}\nЛогика: {runtime.LogicMilliseconds:F2} мс\nОбъектов сцены: {objects}";
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
