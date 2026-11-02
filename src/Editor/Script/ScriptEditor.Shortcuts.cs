using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class ScriptEditor
{
    internal async Task<bool> ExecuteEditorActionAsync(string action, DependencyObject? source)
    {
        if (Document == null) return false;
        bool graph = Document.IsVisual && GraphMode.IsChecked == true && !IsTextEditingSource(source);
        if (action == "file.save") { await GuardAsync(SaveAsync); return true; }
        if (action == "blocks.search") { if (!graph || !IsGraphSource(source)) return false; Guard(OpenQuickSearch); return true; }
        if (graph)
        {
            var key = action switch { "blocks.delete" => Key.Delete, "blocks.duplicate" => Key.D, "edit.copy" => Key.C, "edit.paste" => Key.V, "edit.undo" => Key.Z, "edit.redo" => Key.Y, _ => Key.None };
            if (key == Key.None) return false;
            Guard(() => HandleGraphShortcut(key, key == Key.Delete ? ModifierKeys.None : ModifierKeys.Control, source)); return true;
        }
        if (!IsTextEditingSource(source)) return false;
        var command = action switch { "edit.copy" => ApplicationCommands.Copy, "edit.paste" => ApplicationCommands.Paste, "edit.undo" => ApplicationCommands.Undo, "edit.redo" => ApplicationCommands.Redo, _ => null };
        if (command == null) return false;
        Guard(() => { if (command.CanExecute(null, Keyboard.FocusedElement)) command.Execute(null, Keyboard.FocusedElement); });
        return true;
    }
}
