using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class ProjectParametersWindow
{
    internal Button RecordConsoleShortcut { get; } = new() { Content = "Назначить…", Margin = new(8, 0, 0, 0) };
    private bool recordingConsoleShortcut;

    private DockPanel ConsoleShortcutField()
    {
        const string prompt = "Нажмите клавишу или сочетание";
        var field = new DockPanel();
        DockPanel.SetDock(RecordConsoleShortcut, Dock.Right);
        field.Children.Add(RecordConsoleShortcut);
        field.Children.Add(ConsoleHotkeyInput);
        void Finish()
        {
            recordingConsoleShortcut = false;
            RecordConsoleShortcut.Content = "Назначить…";
            RecordConsoleShortcut.ToolTip = null;
        }
        RecordConsoleShortcut.Click += (_, _) =>
        {
            if (recordingConsoleShortcut) { Finish(); return; }
            recordingConsoleShortcut = true;
            RecordConsoleShortcut.Content = "Отменить запись";
            RecordConsoleShortcut.ToolTip = prompt;
            RecordConsoleShortcut.Focus();
        };
        RecordConsoleShortcut.LostKeyboardFocus += (_, _) => Finish();
        PreviewKeyDown += (_, e) =>
        {
            if (!recordingConsoleShortcut) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;
            try
            {
                ConsoleHotkeyInput.Text = GameConsoleShortcut.Normalize(EditorHotkeys.Format(key, Keyboard.Modifiers));
                status.Text = "";
                Finish();
            }
            catch (InvalidDataException error) { status.Text = error.Message + " " + prompt + "."; }
        };
        return field;
    }
}
