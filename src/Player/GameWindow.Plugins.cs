using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    /// <summary>The project presented by this game window.</summary>
    public ProjectInfo Project => project;
    private PluginHost? playerPlugins;
    private readonly Grid pluginOverlay = new() { IsHitTestVisible = true };
    private readonly Dictionary<string, FrameworkElement> pluginViews = new(StringComparer.Ordinal);
    private void InitializePlayerPlugins()
    {
        if (!EngineLaunch.PluginsEnabled) return;
        var host = new PluginHost(project.Path, false, this);
        try
        {
            host.Load(() => new EditorPluginContext(host.Api)); playerPlugins = host;
            var root = (Grid)Content; root.Children.Add(pluginOverlay); Panel.SetZIndex(pluginOverlay, 500);
            PreviewMouseDown += PluginMouseDown;
        }
        catch { host.Dispose(); throw; }
    }
    private void PluginMouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        e.Handled = ProcessPluginInput(new("mouse", e.ChangedButton.ToString(), position.X, position.Y));
    }
    private bool ProcessPluginInput(PluginInput input)
    {
        if (playerPlugins?.Api.HasInputListeners != true || startupFailed) return false;
        try
        {
            bool handled = playerPlugins.Api.Input(input);
            if (ready) _ = NotifyPluginInputAsync(input);
            return handled;
        }
        catch (Exception error) { ShowStartupError(error, "Перехват ввода плагином"); return true; }
    }
    private async Task NotifyPluginInputAsync(PluginInput input)
    {
        try { await runtime.NotifyPluginInputAsync(input); }
        catch (Exception error) { if (!closed) ShowStartupError(error, "Событие ввода плагина"); }
    }
    private void RenderPluginScreens()
    {
        if (playerPlugins == null || startupFailed) { pluginOverlay.Visibility = Visibility.Collapsed; return; }
        var active = runtime.PluginScreens;
        foreach (string id in pluginViews.Keys.Where(id => !active.Contains(id)).ToArray()) { pluginOverlay.Children.Remove(pluginViews[id]); pluginViews.Remove(id); }
        foreach (string id in active)
        {
            if (!pluginViews.TryGetValue(id, out var view))
            {
                var definition = playerPlugins.Api.screens.FirstOrDefault(s => s.Id == id); if (definition == null) continue;
                view = definition.Create(runtime.Variables); pluginViews.Add(id, view); pluginOverlay.Children.Add(view);
            }
            if (view is IPluginScreenView live) live.UpdateState(runtime.Variables);
        }
        pluginOverlay.Visibility = pluginViews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void DisposePlayerPlugins()
    {
        PreviewMouseDown -= PluginMouseDown; pluginOverlay.Children.Clear(); pluginViews.Clear(); playerPlugins?.Dispose(); playerPlugins = null;
    }
}
