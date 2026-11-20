using System.IO;
using System.Windows.Input;

namespace KNE;

internal static class GameConsoleShortcut
{
    internal static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 60)
            throw new InvalidDataException("Укажите клавишу или сочетание для консоли.");
        var (key, modifiers) = EditorHotkeys.Parse(string.Join('+', input.Split('+', StringSplitOptions.TrimEntries)));
        if (modifiers.HasFlag(ModifierKeys.Windows) || key == Key.F4 && modifiers == ModifierKeys.Alt ||
            key == Key.F11 && modifiers == ModifierKeys.None)
            throw new InvalidDataException("Это сочетание используется системой или полноэкранным режимом.");
        return EditorHotkeys.Format(key, modifiers);
    }

    internal static bool Matches(Key key, ModifierKeys modifiers, string configured) =>
        EditorHotkeys.Parse(configured) == (key, modifiers);

    internal static bool Matches(ConsoleKeyInfo input, string configured)
    {
        ModifierKeys modifiers = ModifierKeys.None;
        if ((input.Modifiers & ConsoleModifiers.Control) != 0) modifiers |= ModifierKeys.Control;
        if ((input.Modifiers & ConsoleModifiers.Alt) != 0) modifiers |= ModifierKeys.Alt;
        if ((input.Modifiers & ConsoleModifiers.Shift) != 0) modifiers |= ModifierKeys.Shift;
        return Matches(KeyInterop.KeyFromVirtualKey((int)input.Key), modifiers, configured);
    }
}
