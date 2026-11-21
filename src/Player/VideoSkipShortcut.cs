using System.IO;
using System.Windows.Input;

namespace KNE;

internal static class VideoSkipShortcut
{
    internal static (Key Key, ModifierKeys Modifiers) Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60)
            throw new InvalidDataException("Укажите клавишу или сочетание для пропуска видео.");
        string shortcut = string.Join('+', value.Split('+', StringSplitOptions.TrimEntries));
        if (shortcut.Equals("Esc", StringComparison.OrdinalIgnoreCase)) return (Key.Escape, ModifierKeys.None);
        if (shortcut.Equals("Space", StringComparison.OrdinalIgnoreCase)) return (Key.Space, ModifierKeys.None);
        if (shortcut.Equals("Enter", StringComparison.OrdinalIgnoreCase)) return (Key.Enter, ModifierKeys.None);
        var (key, modifiers) = EditorHotkeys.Parse(shortcut);
        if (modifiers.HasFlag(ModifierKeys.Windows) || (key, modifiers) is
            (Key.F4, ModifierKeys.Alt) or (Key.Escape, ModifierKeys.Control | ModifierKeys.Shift))
            throw new InvalidDataException("Это сочетание используется Windows.");
        return (key, modifiers);
    }
}
