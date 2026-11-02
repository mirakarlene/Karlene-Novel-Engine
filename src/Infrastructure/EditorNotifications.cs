using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Xml.Linq;

namespace KNE;

internal sealed record EditorNotification(string Kind, string Message, bool Critical = false, bool Tip = false, int Count = 1);

/// <summary>Filters events and combines saved-file events before delivering OS notifications.</summary>
internal sealed class EditorNotificationQueue
{
    private readonly List<EditorNotification> pending = [];
    internal void Add(EditorNotification value) => pending.Add(value);
    internal EditorNotification[] Drain(EditorPreferences preferences, bool minimized, bool closed)
    {
        var eligible = pending.Where(value => !closed && (!minimized || preferences.NotifyMinimized) && (preferences.NotifyTips || value.Critical)).ToArray(); pending.Clear();
        if (!preferences.AggregateNotifications) return eligible;
        return eligible.GroupBy(value => (value.Kind, value.Critical, value.Tip, Message: value.Kind == "saved" ? "" : value.Message)).Select(group => group.Key.Kind == "saved"
            ? new EditorNotification("saved", $"Сохранено {group.Sum(item => item.Count)} файлов", group.Key.Critical, group.Key.Tip, group.Sum(item => item.Count))
            : group.Last() with { Count = group.Sum(item => item.Count) }).ToArray();
    }
}

/// <summary>Windows Action Center delivery via the built-in Windows PowerShell WinRT projection.</summary>
internal sealed class WindowsEditorNotifications : IDisposable
{
    internal const string ApplicationId = "KarleneNovelEngine.Editor";
    private MediaPlayer? sound;
    private bool disposed;
    // A fixed helper program, never interpolated with file paths or notification text. All data travels over UTF-8 stdin.
    private const string Bridge = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] > $null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] > $null
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\AppUserModelId\KarleneNovelEngine.Editor')
        try { $key.SetValue('DisplayName', 'Karlene Novel Engine'); $key.SetValue('ShowInSettings', 1, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $key.Dispose() }
        $xml = [Windows.Data.Xml.Dom.XmlDocument]::new()
        $xml.LoadXml($request.Xml)
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        $toast.Tag = 'kne-' + $request.Tag
        $toast.Group = 'editor'
        $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('KarleneNovelEngine.Editor')
        $notifier.Show($toast)
        [Console]::Out.Write($notifier.Setting.ToString())
        """;

    internal static string Payload(string text) => new XDocument(new XElement("toast", new XAttribute("duration", "short"),
        new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"), new XElement("text", "Karlene Novel Engine"), new XElement("text", text))),
        new XElement("audio", new XAttribute("silent", "true")))).ToString(SaveOptions.DisableFormatting);

    internal async Task<string> ShowAsync(EditorNotification notification, EditorPreferences preferences, CancellationToken token)
    {
        if (disposed) return "Закрыто";
        if (preferences.NotificationSound) PlaySound(preferences);
        if (!preferences.Toasts) return "Визуальные уведомления отключены.";
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Bridge)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось вызвать Windows Action Center.");
        using var cancellation = token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        string tag = preferences.AggregateNotifications ? notification.Kind == "saved" ? "saved" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(notification.Kind + "\n" + notification.Message)))[..12] : Guid.NewGuid().ToString("N")[..12];
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { Xml = Payload(notification.Message), Tag = tag }).AsMemory(), token);
        process.StandardInput.Close(); await process.WaitForExitAsync(token);
        string result = await output, failure = await error;
        if (process.ExitCode != 0) throw new IOException("Windows Action Center: " + failure);
        return result.Trim() == "Enabled" ? "Тост передан в Windows Action Center." : "Windows ограничивает уведомления KNE: " + result.Trim();
    }

    private void PlaySound(EditorPreferences preferences)
    {
        string file = preferences.Sound == "custom" ? preferences.SoundFile : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
        if (!File.Exists(file)) throw new FileNotFoundException("Звук уведомления не найден.", file);
        sound?.Close(); var player = new MediaPlayer { Volume = preferences.Sound switch { "quiet" => .2, "loud" => 1, _ => .5 } }; sound = player;
        player.MediaEnded += (_, _) => { player.Close(); if (ReferenceEquals(sound, player)) sound = null; }; player.Open(new Uri(file)); player.Play();
    }
    public void Dispose() { disposed = true; sound?.Close(); sound = null; }
}
