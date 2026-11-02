using System.IO;
using System.Windows;

namespace KNE;

public partial class MainWindow
{
    internal async Task ApplyStartupBehaviorAsync()
    {
        string action = settings.Editor.Startup;
        if (action == "home") return;
        if (action == "settings") { BtnSettings_Click(this, new RoutedEventArgs()); return; }
        string? path = action == "last" ? settings.RecentProjects.FirstOrDefault() : settings.Editor.StartupProject;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(Path.Combine(path, "project.json"))) throw new DirectoryNotFoundException("Назначенный проект перемещён или удалён.");
            if (!await OpenProjectAsync(path, remember: true)) return;
            if (action == "new_scene") await CreateStartupSceneAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            ShowPanel(WelcomePanel); string warning = "Не удалось открыть проект при запуске. " + error.Message;
            OperationStatus.Text = warning;
            if (MissingProjectWarning != null) MissingProjectWarning(warning);
            else if (IsVisible) MessageBox.Show(this, warning, "Запуск KNE", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CreateStartupSceneAsync()
    {
        if (currentProject is not { } project) return;
        string name = "scene_" + Guid.NewGuid().ToString("N")[..12];
        bool visual = project.Configuration.AuthoringMode == ProjectAuthoringMode.Visual;
        string path = PathSafety.ResolveRelative(project.Path, "game/scenes/" + name + (visual ? ".knegraph" : ".krl"));
        string source;
        if (visual)
        {
            var graph = VisualScript.Create(); graph.FunctionName = name;
            graph.Nodes.RemoveAll(node => node.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.End));
            graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Start).Next = graph.Nodes.Single(node => node.Kind == ScriptNodeKind.End).Id;
            source = graph.Serialize();
        }
        else source = "function " + name + "()\nend\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(output)) await writer.WriteAsync(source.AsMemory(), lifetime.Token);
        await OpenProjectAsync(project.Path); await OpenScriptAsync(path);
    }
}
