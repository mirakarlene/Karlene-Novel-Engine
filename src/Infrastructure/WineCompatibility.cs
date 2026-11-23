using System.Runtime.InteropServices;

namespace KNE;

internal static class WineCompatibility
{
    internal const string Flag = "--wine-compatibility";
    private const string Override = "KNE_WINE_COMPATIBILITY";
    private static readonly Lazy<bool> Wine = new(HasWineRuntime);

    internal static bool Enabled => Environment.GetEnvironmentVariable(Override) == "1" || Wine.Value;

    internal static void ApplyOverride(IEnumerable<string> arguments)
    {
        if (arguments.Contains(Flag, StringComparer.Ordinal))
            Environment.SetEnvironmentVariable(Override, "1");
    }

    private static bool HasWineRuntime()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("ntdll.dll", typeof(WineCompatibility).Assembly,
                DllImportSearchPath.System32, out var module)) return false;
        try { return NativeLibrary.TryGetExport(module, "wine_get_version", out _); }
        finally { NativeLibrary.Free(module); }
    }
}
