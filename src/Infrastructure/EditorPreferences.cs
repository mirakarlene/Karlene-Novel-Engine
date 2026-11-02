using System.IO;

namespace KNE;

/// <summary>User-owned editor automation preferences, independent of game runtime settings.</summary>
public sealed record EditorPreferences
{
    public string BackupPath { get; set; } = "";
    public int BackupMinutes { get; set; } = -1;
    public int BackupCopies { get; set; } = 5;
    public int AutosaveMinutes { get; set; } = 5;
    public int AutosaveVersions { get; set; } = 25;
    public bool KeepHistory { get; set; } = true;
    public string Startup { get; set; } = "home";
    public string StartupProject { get; set; } = "";
    public bool NotificationSound { get; set; }
    public string Sound { get; set; } = "standard";
    public string SoundFile { get; set; } = "";
    public bool Toasts { get; set; } = true;
    public bool AggregateNotifications { get; set; } = true;
    public bool NotifyMinimized { get; set; } = true;
    public bool NotifyTips { get; set; }
    public bool BreakReminders { get; set; }
    public int BreakMinutes { get; set; } = 60;
    public bool StrictBreak { get; set; }
    public int BreakDuration { get; set; } = 5;
    public bool ShowWorkTime { get; set; }

    public static int[] BackupIntervals => [-1, 120, 60, 30, 10, 0];
    public static int[] BackupLimits => [3, 5, 10, 25];
    public static int[] AutosaveIntervals => [1, 5, 10, 30, 0];
    public static int[] HistoryLimits => [5, 10, 25, 50, 100];
    public static int[] BreakIntervals => [30, 45, 60, 90, 120];
    public static int[] BreakDurations => [5, 10, 15, 30];

    internal void Validate()
    {
        if (!BackupIntervals.Contains(BackupMinutes) || !BackupLimits.Contains(BackupCopies) || !AutosaveIntervals.Contains(AutosaveMinutes) || !HistoryLimits.Contains(AutosaveVersions) || !BreakIntervals.Contains(BreakMinutes) || !BreakDurations.Contains(BreakDuration))
            throw new InvalidDataException("Неизвестный интервал или лимит настроек редактора.");
        if (Startup is not ("home" or "last" or "project" or "new_scene" or "settings")) throw new InvalidDataException("Неизвестное действие при запуске.");
        if (Sound is not ("quiet" or "standard" or "loud" or "custom")) throw new InvalidDataException("Неизвестный звук уведомления.");
        foreach (string path in new[] { BackupPath, StartupProject, SoundFile })
            if (path == null || path.Length > 2000 || path.Any(char.IsControl) || path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Укажите полный путь в настройках редактора.");
        if ((Startup is "project" or "new_scene") && StartupProject == "") throw new InvalidDataException("Выберите проект для запуска.");
        if (NotificationSound && Sound == "custom" && (!File.Exists(SoundFile) || !Path.GetExtension(SoundFile).Equals(".wav", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Для своего звука выберите существующий WAV-файл.");
    }

    internal static string DefaultBackupPath(string storage, string settingsPath)
    {
        string parent = string.IsNullOrWhiteSpace(storage) ? Path.GetDirectoryName(Path.GetFullPath(settingsPath))! : Path.GetDirectoryName(PathSafety.NormalizeDirectory(storage))!;
        return Path.Combine(parent, "backups");
    }
}
