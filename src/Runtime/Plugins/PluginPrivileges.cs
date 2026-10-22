using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace KNE;

/// <summary>Full-trust extensions use the current user's token. Elevation is a separate explicit operation.</summary>
public static class PluginPrivileges
{
    public static bool IsElevated
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    internal static void EnsureUserProcess()
    {
        if (IsElevated) throw new InvalidOperationException("Плагины требуют обычный пользовательский процесс. Перезапустите KNE без прав администратора; повышение прав доступно отдельно через PluginPrivileges.RequestAdministrator().");
    }
    /// <summary>Shows the exact program, arguments and reason before requesting Windows UAC. Returns false on refusal.</summary>
    public static bool RequestAdministrator(string executable, IReadOnlyList<string> arguments, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string full = System.IO.Path.GetFullPath(executable);
        if (!System.IO.File.Exists(full)) throw new System.IO.FileNotFoundException("Программа не найдена.", full);
        bool Confirm() => MessageBox.Show("Плагин запрашивает права администратора.\n\nПричина: " + reason + "\nПрограмма: " + full + "\nАргументы: " + string.Join(" ", arguments), "Разрешение администратора", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        bool approved = Application.Current?.Dispatcher is { } dispatcher ? dispatcher.Invoke(Confirm) : Confirm();
        if (!approved) return false;
        var start = new ProcessStartInfo(full) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try { using var process = Process.Start(start); return process != null; }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
    }
}
