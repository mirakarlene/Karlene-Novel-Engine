using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private readonly Func<System.Windows.Window, string, string?> chooseProjectScript;
    private readonly Action<ProcessStartInfo> launchProjectScript;

    private static string? ShowProjectScriptDialog(System.Windows.Window owner, string suggestedPath)
    {
        var dialog = new SaveFileDialog { Title = "Создать или открыть скрипт проекта", InitialDirectory = Path.GetDirectoryName(suggestedPath),
            FileName = Path.GetFileName(suggestedPath), DefaultExt = ".lua",
            Filter = "Lua (*.lua)|*.lua|KRL (*.krl)|*.krl|C# (*.cs)|*.cs", OverwritePrompt = false };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private async void SceneEditor_ProjectScriptRequested(object? sender, EventArgs e)
    {
        if (currentProject is not { } project) return;
        await RunOperationAsync(async () =>
        {
            var source = ActiveEditor.CaptureProjectScript(); if (source == null) return;
            string file;
            if (string.IsNullOrWhiteSpace(source.Value.Path))
            {
                string suggested = Path.Combine(project.Path, "game", "scripts", "script-" + source.Value.NodeId[..Math.Min(8, source.Value.NodeId.Length)] + ".lua");
                string? selected = chooseProjectScript(this, suggested); if (selected == null) return; file = selected;
            }
            else file = PathSafety.ResolveRelative(project.Path, source.Value.Path);
            string relative = await ProjectScriptFiles.EnsureAsync(project.Path, file, lifetime.Token);
            ActiveEditor.SetProjectScriptPath(source.Value.NodeId, relative);
            await ActiveEditor.SaveAsync();
            launchProjectScript(EditorLauncher.CreateStartInfo(settings.EditorPath, PathSafety.ResolveRelative(project.Path, relative)));
            OperationStatus.Text = "Скрипт открыт во внешнем редакторе: " + relative;
        }, "Открытие скрипта…");
    }
}
