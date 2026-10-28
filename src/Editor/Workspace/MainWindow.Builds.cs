using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class MainWindow
{
    private sealed class RemoteDebugTab;
    private async Task RunConfiguredProjectAsync(ProjectInfo project)
    {
        var options = ProjectBuildSettingsStore.Load(project.Path);
        if (options.TestMode == "vm") { new GameWindow(project).Show(); return; }
        RemoteDebugView? debug = null;
        if (options.TestMode == "remote_debug") debug = OpenRemoteDebug(project);
        try
        {
            var build = await new GameBuildExporter().BuildAsync(project, options, options.TestPath, test: true,
                log: line => Dispatcher.BeginInvoke(() => OperationStatus.Text = line), token: lifetime.Token);
            var start = new ProcessStartInfo(build.Executable) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(build.Executable)!, WindowStyle = ProcessWindowStyle.Normal };
            // A release executable exposes no listening port; only this explicit editor launch enables the worker's JSON client.
            start.Environment.Remove(RemoteDebugConnection.EnvironmentName);
            if (debug != null) start.Environment[RemoteDebugConnection.EnvironmentName] = debug.Connection.PipeName;
            using (Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить тестовый билд.")) { }
        }
        catch { if (debug != null) await CloseRemoteDebugAsync(debug); throw; }
    }

    private RemoteDebugView OpenRemoteDebug(ProjectInfo project)
    {
        var definition = new RemoteDebugTab(); var view = new RemoteDebugView(project) { Tag = definition };
        var tab = new TabItem { Tag = definition, ToolTip = "JSON Remote Debug · " + project.Title };
        var header = new StackPanel { Orientation = Orientation.Horizontal }; header.Children.Add(new TextBlock { Text = "Remote Debug", VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 8, 0) });
        var close = new Button { Content = "×", Padding = new(5, 0, 5, 0), ToolTip = "Отключить отладчик; игра продолжит работу" };
        close.Click += async (_, args) => { args.Handled = true; await CloseRemoteDebugAsync(view); }; header.Children.Add(close); tab.Header = header;
        view.SourceRequested += place => _ = RunOperationAsync(async () =>
        {
            await OpenScriptAsync(PathSafety.ResolveRelative(project.Path, place.File));
            if (place.NodeId != null) ActiveEditor.NavigateToBlock(place.NodeId); else ActiveEditor.NavigateToSceneLine(place.Line);
        }, "Открытие места остановки");
        EditorWorkspace.Children.Add(view); DocumentTabs.Items.Add(tab); DocumentTabs.SelectedItem = tab; ShowActiveEditor(); return view;
    }
    private async Task CloseRemoteDebugAsync(RemoteDebugView view)
    {
        var tab = DocumentTabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, view.Tag));
        if (tab != null) DocumentTabs.Items.Remove(tab); EditorWorkspace.Children.Remove(view); ShowActiveEditor(); await view.DisposeAsync();
    }
}
