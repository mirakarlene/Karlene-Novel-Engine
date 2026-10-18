using System.Diagnostics;
using System.IO;

namespace KNE;

internal static class EngineLaunch
{
    internal const string PluginsEnvironment = "KNE_DISABLE_PLUGINS";
    internal static bool PluginsEnabled => Environment.GetEnvironmentVariable(PluginsEnvironment) != "1";
    internal static bool IsPluginPath(string path) => ("/" + path.Replace('\\', '/')).Contains("/plugins/", StringComparison.OrdinalIgnoreCase);

    internal static ProcessStartInfo CreateStartInfo(params string[] arguments) => CreateStartInfo(typeof(App).Assembly.Location, arguments);

    internal static ProcessStartInfo CreateStartInfo(string assembly, IReadOnlyList<string> arguments)
    {
        string executable = Path.ChangeExtension(assembly, ".exe");
        bool appHost = File.Exists(executable);
        var info = new ProcessStartInfo(appHost ? executable : "dotnet")
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(assembly)!,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!appHost) info.ArgumentList.Add(assembly);
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static ProcessStartInfo RestartInfo(bool disablePlugins)
    {
        var info = disablePlugins ? CreateStartInfo("--disable-plugins") : CreateStartInfo();
        info.Environment[PluginsEnvironment] = disablePlugins ? "1" : "0";
        info.WindowStyle = ProcessWindowStyle.Normal;
        return info;
    }

    internal static ProcessStartInfo RecoveryInfo(string sessionPath)
    {
        var info = CreateStartInfo("--engine-recovery", sessionPath);
        info.WindowStyle = ProcessWindowStyle.Normal;
        return info;
    }

    /// <summary>The helper survives the UI process, including native failures which managed handlers cannot catch.</summary>
    internal static async Task WatchAsync(int processId, long startedTicks, string sessionPath, Action<ProcessStartInfo>? launch = null)
    {
        int exitCode = -1;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != startedTicks) return; // PID was reused.
            await process.WaitForExitAsync().ConfigureAwait(false);
            exitCode = process.ExitCode;
        }
        catch (ArgumentException) { /* Parent exited before the helper attached. */ }
        var report = EngineDiagnostics.RecordUnexpectedExit(sessionPath, exitCode);
        if (report != null)
        {
            var info = RecoveryInfo(sessionPath);
            if (launch != null) launch(info); else using (Process.Start(info)) { }
        }
    }
}
