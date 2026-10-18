using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private async void SceneEditor_SubsystemRequested(object? sender, string nodeId)
    {
        if (currentProject is not { } project || sender is not ScriptEditor editor) return;
        await RunOperationAsync(async () =>
        {
            var node = editor.Document?.Graph?.Nodes.SingleOrDefault(node => node.Id == nodeId && node.Kind == ScriptNodeKind.Subsystem);
            if (node == null) return;
            string path;
            if (string.IsNullOrWhiteSpace(node.Path))
            {
                var dialog = new SaveFileDialog { Title = "Создать или открыть подсистему", InitialDirectory = Path.Combine(project.Path, "game", "scenes"), FileName = "subsystem.knegraph", DefaultExt = ".knegraph", Filter = "Блок-схема KNE (*.knegraph)|*.knegraph", OverwritePrompt = false };
                if (dialog.ShowDialog(this) != true) return;
                path = PathSafety.EnsureInside(project.Path, dialog.FileName);
            }
            else path = PathSafety.ResolveRelative(project.Path, node.Path);
            await SubsystemFiles.EnsureAsync(project.Path, path, lifetime.Token);
            editor.SetSubsystemPath(nodeId, Path.GetRelativePath(project.Path, path).Replace('\\', '/'));
            if (!await OpenProjectAsync(project.Path)) return;
            await OpenScriptAsync(path);
        }, "Открытие подсистемы…");
    }
}
