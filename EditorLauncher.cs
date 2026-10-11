using System.Diagnostics;
using System.IO;

namespace KNE;

public static class EditorLauncher
{
    public static ProcessStartInfo CreateStartInfo(string? editorPath, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string editor = string.IsNullOrWhiteSpace(editorPath) ? "notepad.exe" : editorPath.Trim().Trim('"');
        if (!editor.Equals("notepad.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(editor))
            throw new FileNotFoundException("Исполняемый файл редактора не найден.", editor);
        var info = new ProcessStartInfo(editor) { UseShellExecute = false };
        info.ArgumentList.Add(filePath);
        return info;
    }
}
