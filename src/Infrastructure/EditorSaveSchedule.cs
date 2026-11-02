namespace KNE;

/// <summary>Monotonic periodic saves, independently scheduled from save-triggered backups.</summary>
internal sealed class EditorSaveSchedule
{
    private TimeSpan autosave, backup;
    internal void Reset(TimeSpan now) { autosave = backup = now; }
    internal bool TakeAutosaveDue(TimeSpan now, EditorPreferences preferences)
    {
        if (preferences.AutosaveMinutes <= 0 || now - autosave < TimeSpan.FromMinutes(preferences.AutosaveMinutes)) return false;
        autosave = now; return true;
    }
    internal bool TakeBackupDue(TimeSpan now, EditorPreferences preferences)
    {
        if (preferences.BackupMinutes <= 0 || now - backup < TimeSpan.FromMinutes(preferences.BackupMinutes)) return false;
        backup = now; return true;
    }
}
