using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KNE;

public partial class MainWindow
{
    /// <summary>The project currently open in the editor.</summary>
    public ProjectInfo? Project => currentProject;
    private PluginHost? editorPlugins;
    private string? pluginProject;
    private void LoadEditorPlugins(string? projectDirectory)
    {
        if (pluginProject == (projectDirectory ?? "")) return;
        UnloadEditorPlugins();
        if (!EngineLaunch.PluginsEnabled) { if (projectDirectory != null) AddSceneNavigatorButton(); return; }
        if (projectDirectory == null && settings.ApplicationPlugins.Count == 0) { EditorTheme.Apply(this, settings.Theme); return; }
        string directory = projectDirectory ?? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsStore.SettingsPath))!;
        System.IO.Directory.CreateDirectory(directory);
        var host = new PluginHost(directory, true, this);
        try
        {
            host.Load(() => new EditorPluginContext(host.Api, lifetime.Token), lifetime.Token, settings.ApplicationPlugins, includeProjectPlugins: projectDirectory != null);
            editorPlugins = host; pluginProject = projectDirectory ?? ""; PluginCatalog.Current = host.Api;
            ThemeInput.ItemsSource = EditorTheme.Themes; ThemeInput.SelectedValue = settings.Theme;
            EditorTheme.Apply(this, settings.Theme);
            applicationPluginLoadError = null;
            if (projectDirectory == null) return;
            var translations = AddPluginButton("Диалоги / переводы", ShowPluginTranslations, 10);
            translations.Margin = new Thickness(8, 0, 8, 0);
            translations.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = AppearanceButton });
            AddSceneNavigatorButton();
            foreach (var command in host.Api.commands.Where(c => c.Location == "toolbar")) AddPluginButton(command.Title, () => command.Execute(PluginEditor()));
            foreach (var tab in host.Api.tabs) AddPluginButton(tab.Title, () => OpenPluginTab(tab));
            if (host.Api.commands.Any(c => c.Location == "context"))
            {
                var menu = new ContextMenu();
                foreach (var command in host.Api.commands.Where(c => c.Location == "context"))
                {
                    var item = new MenuItem { Header = command.Title };
                    item.Click += async (_, _) => await RunOperationAsync(() => { command.Execute(PluginEditor()); return Task.CompletedTask; }, command.Title);
                    menu.Items.Add(item);
                }
                EditorWorkspace.ContextMenu = menu;
            }
        }
        catch (Exception error) { UnloadEditorPlugins(); host.Dispose(); applicationPluginLoadError = error.Message; throw; }
    }
    private PluginEditorContext PluginEditor() => new(currentProject!.Path, () => ActiveEditor.GetPluginGraph(), graph => ActiveEditor.ReplacePluginGraph(graph));
    private Button AddPluginButton(string title, Action execute, double horizontalPadding = 14)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(horizontalPadding, 6, horizontalPadding, 6) };
        button.Click += async (_, _) => await RunOperationAsync(() => { execute(); return Task.CompletedTask; }, title);
        PluginToolbar.Children.Add(button);
        return button;
    }
    private void OpenPluginTab(PluginEditorTab definition)
    {
        var existing = DocumentTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag is PluginEditorTab d && d.Id == definition.Id);
        if (existing != null) { DocumentTabs.SelectedItem = existing; return; }
        FrameworkElement content = definition.Create(); content.Tag = definition;
        var tab = new TabItem { Tag = definition };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = definition.Title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        var close = new Button { Content = "×", Padding = new Thickness(5, 0, 5, 0) };
        close.Click += (_, e) =>
        {
            e.Handled = true; int index = DocumentTabs.Items.IndexOf(tab);
            DocumentTabs.Items.Remove(tab); EditorWorkspace.Children.Remove(content);
            if (DocumentTabs.Items.Count > 0) DocumentTabs.SelectedIndex = Math.Min(index, DocumentTabs.Items.Count - 1);
            ShowActiveEditor();
        };
        header.Children.Add(close); tab.Header = header;
        EditorWorkspace.Children.Add(content); DocumentTabs.Items.Add(tab); DocumentTabs.SelectedItem = tab; ShowActiveEditor();
    }
    private void UnloadEditorPlugins()
    {
        PluginToolbar.Children.Clear(); EditorWorkspace.ContextMenu = null;
        foreach (var view in EditorWorkspace.Children.OfType<FrameworkElement>().Where(e => e.Tag is PluginEditorTab).ToArray()) EditorWorkspace.Children.Remove(view);
        foreach (var tab in DocumentTabs.Items.OfType<TabItem>().Where(t => t.Tag is PluginEditorTab).ToArray()) DocumentTabs.Items.Remove(tab);
        if (ReferenceEquals(PluginCatalog.Current, editorPlugins?.Api)) PluginCatalog.Current = null;
        foreach (var editor in EditorWorkspace.Children.OfType<ScriptEditor>()) editor.ApplyPluginTexture();
        editorPlugins?.Dispose(); editorPlugins = null; pluginProject = null;
        ThemeInput.ItemsSource = EditorTheme.Themes;
    }
}
