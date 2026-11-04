using System.IO;
using System.Windows.Input;
using Xunit;

namespace KNE.Tests;

public sealed class EditorPreferencesTests
{
    [Fact]
    public async Task Preferences_DefaultsAndAllOptionsRoundTrip_AndBackupDefaultIsSiblingOfStorage()
    {
        using var fixture = new TempProject(); var store = new UserSettingsStore(Path.Combine(fixture.Root, "profile/settings.json"));
        var defaults = await store.LoadAsync(); Assert.Equal(5, defaults.Editor.BackupCopies); Assert.Equal(25, defaults.Editor.AutosaveVersions); Assert.Equal(60, defaults.Editor.BreakMinutes); Assert.Equal("home", defaults.Editor.Startup);
        Assert.Equal(Path.Combine(fixture.Root, "backups"), EditorPreferences.DefaultBackupPath(Path.Combine(fixture.Root, "projects"), store.SettingsPath));
        var preferences = new EditorPreferences { BackupPath = Path.Combine(fixture.Root, "backup"), BackupMinutes = 10, BackupCopies = 25, AutosaveMinutes = 1, AutosaveVersions = 100, KeepHistory = false, Startup = "new_scene", StartupProject = fixture.ProjectPath, NotificationSound = true, Sound = "loud", Toasts = false, AggregateNotifications = false, NotifyMinimized = false, NotifyTips = true, BreakReminders = true, BreakMinutes = 90, StrictBreak = true, BreakDuration = 15, ShowWorkTime = true };
        await store.SaveAsync(new UserSettings { Editor = preferences, Hotkeys = new() { ["file.save"] = "Ctrl+Shift+S" }, UserName = "Author", ApplicationPlugins = [] });
        var loaded = await store.LoadAsync(); Assert.Equal(preferences, loaded.Editor); Assert.Equal("Ctrl+Shift+S", loaded.Hotkeys["file.save"]); Assert.Equal("Author", loaded.UserName);
        foreach (int value in EditorPreferences.BackupIntervals) (preferences with { BackupMinutes = value }).Validate();
        foreach (int value in EditorPreferences.AutosaveIntervals) (preferences with { AutosaveMinutes = value }).Validate();
        foreach (int value in EditorPreferences.HistoryLimits) (preferences with { AutosaveVersions = value }).Validate();
        foreach (int value in EditorPreferences.BreakIntervals) (preferences with { BreakMinutes = value }).Validate();
        Assert.Throws<InvalidDataException>(() => (preferences with { BackupMinutes = 5 }).Validate()); Assert.Throws<InvalidDataException>(() => (preferences with { AutosaveVersions = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (preferences with { StartupProject = "" }).Validate()); Assert.Throws<InvalidDataException>(() => (preferences with { Sound = "custom", SoundFile = "" }).Validate());
    }

    [Fact]
    public void HotkeyProfiles_RoundTripExistingBindings_ReassignDisableAndRejectConflictsAtomically()
    {
        var changed = EditorHotkeys.Validate(new Dictionary<string, string> { ["file.save"] = "shift+ctrl+S", ["blocks.delete"] = "", ["debug.run"] = "F5" });
        Assert.Equal("Ctrl+Shift+S", changed["file.save"]); Assert.Null(EditorHotkeys.Match(Key.S, ModifierKeys.Control, changed)); Assert.Equal("file.save", EditorHotkeys.Match(Key.S, ModifierKeys.Control | ModifierKeys.Shift, changed)); Assert.Equal("debug.run", EditorHotkeys.Match(Key.F5, ModifierKeys.None, changed)); Assert.Null(EditorHotkeys.Match(Key.Delete, ModifierKeys.None, changed));
        var imported = EditorHotkeys.Import(EditorHotkeys.Export(changed));
        foreach (var action in EditorHotkeys.Actions) Assert.Equal(EditorHotkeys.Keys(action, changed), EditorHotkeys.Keys(action, imported));
        Assert.Throws<InvalidDataException>(() => EditorHotkeys.Validate(new Dictionary<string, string> { ["debug.run"] = "Ctrl+S" }));
        Assert.Throws<InvalidDataException>(() => EditorHotkeys.Import("{\"Version\":2,\"Bindings\":{}}"));
        Assert.Throws<InvalidDataException>(() => EditorHotkeys.Validate(new Dictionary<string, string> { ["unknown"] = "F5" }));
        Assert.Throws<InvalidDataException>(() => EditorHotkeys.Format(Key.S, ModifierKeys.None)); Assert.Throws<InvalidDataException>(() => EditorHotkeys.Format(Key.LeftCtrl, ModifierKeys.Control));
        Assert.Equal("Ctrl+Shift+S", changed["file.save"]); Assert.Equal("edit.redo", EditorHotkeys.Match(Key.X, ModifierKeys.Control, null));
    }

    [Theory]
    [InlineData(1)] [InlineData(5)] [InlineData(10)] [InlineData(30)]
    public void SaveSchedule_UsesExactIntervalAndSeparateBackupClock_AndManualOrDisabledModesNeverRun(int minutes)
    {
        var schedule = new EditorSaveSchedule(); var preferences = new EditorPreferences { AutosaveMinutes = minutes, BackupMinutes = 120 }; schedule.Reset(TimeSpan.FromMinutes(10));
        TimeSpan due = TimeSpan.FromMinutes(10 + minutes);
        Assert.False(schedule.TakeAutosaveDue(due - TimeSpan.FromTicks(1), preferences)); Assert.True(schedule.TakeAutosaveDue(due, preferences)); Assert.False(schedule.TakeAutosaveDue(due + TimeSpan.FromTicks(1), preferences));
        Assert.False(schedule.TakeBackupDue(TimeSpan.FromMinutes(130) - TimeSpan.FromTicks(1), preferences)); Assert.True(schedule.TakeBackupDue(TimeSpan.FromMinutes(130), preferences)); Assert.False(schedule.TakeBackupDue(TimeSpan.FromMinutes(131), preferences));
        Assert.False(schedule.TakeAutosaveDue(TimeSpan.FromDays(1), preferences with { AutosaveMinutes = 0 }));
        Assert.False(schedule.TakeBackupDue(TimeSpan.FromDays(1), preferences with { BackupMinutes = 0 })); Assert.False(schedule.TakeBackupDue(TimeSpan.FromDays(1), preferences with { BackupMinutes = -1 }));
    }

    [Fact]
    public void NotificationQueue_AggregatesSavedFilesAndHonorsMinimizedClosedAndTipPreferences()
    {
        var queue = new EditorNotificationQueue(); queue.Add(new("saved", "one", Count: 2)); queue.Add(new("saved", "two", Count: 3)); queue.Add(new("tip", "tip", Tip: true)); queue.Add(new("error", "error", Critical: true));
        var result = queue.Drain(new() { NotifyTips = true }, false, false); Assert.Equal(3, result.Length); Assert.Equal("Сохранено 5 файлов", result[0].Message); Assert.Equal(5, result[0].Count); Assert.True(result[2].Critical); Assert.Empty(queue.Drain(new(), false, false));
        queue.Add(new("saved", "one")); queue.Add(new("saved", "two")); Assert.Equal(2, queue.Drain(new() { AggregateNotifications = false, NotifyTips = true }, false, false).Length);
        queue.Add(new("saved", "not critical")); queue.Add(new("tip", "tip", Tip: true)); queue.Add(new("error", "critical", Critical: true)); Assert.Equal("critical", Assert.Single(queue.Drain(new() { NotifyTips = false }, false, false)).Message);
        queue.Add(new("error", "critical", Critical: true)); Assert.Empty(queue.Drain(new() { NotifyMinimized = false }, true, false));
        queue.Add(new("error", "critical", Critical: true)); Assert.Empty(queue.Drain(new(), false, true));
        queue.Add(new("tip", "tip", Tip: true)); Assert.Single(queue.Drain(new() { NotifyTips = true }, false, false));
        queue.Add(new("error", "first", Critical: true)); queue.Add(new("error", "different", Critical: true)); Assert.Equal(new[] { "first", "different" }, queue.Drain(new(), false, false).Select(item => item.Message));
        string xml = WindowsEditorNotifications.Payload("<test>&\"русский"); Assert.Contains("&lt;test&gt;&amp;", xml); Assert.Contains("ToastGeneric", xml); Assert.Contains("silent=\"true\"", xml);
    }

    [Fact]
    public void EarlyBreakEndPreservesWorkTimeAndRestartsFullReminderInterval()
    {
        var preferences = new EditorPreferences { BreakReminders = true, BreakMinutes = 60, StrictBreak = true, BreakDuration = 10 };
        var clock = new EditorSessionClock();
        Assert.True(clock.Advance(TimeSpan.FromHours(1), preferences));
        clock.Advance(TimeSpan.FromMinutes(2), preferences);
        Assert.Equal(TimeSpan.FromMinutes(8), clock.BreakRemaining);
        clock.EndBreak();
        Assert.False(clock.IsOnBreak);
        Assert.Equal(TimeSpan.FromHours(1), clock.WorkTime);
        Assert.Equal(TimeSpan.Zero, clock.SinceBreak);
        Assert.False(clock.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromTicks(1), preferences));
        Assert.True(clock.Advance(TimeSpan.FromTicks(1), preferences));
        Assert.Equal(TimeSpan.FromHours(2), clock.WorkTime);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.BreakRemaining);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void WorkClock_RemindsAtBoundary_StrictBreakBlocksForSelectedDuration_AndResetClearsSession(bool strict)
    {
        var preferences = new EditorPreferences { BreakReminders = true, BreakMinutes = 60, StrictBreak = strict, BreakDuration = 10 }; var clock = new EditorSessionClock();
        Assert.False(clock.Advance(TimeSpan.FromMinutes(60) - TimeSpan.FromTicks(1), preferences)); Assert.True(clock.Advance(TimeSpan.FromTicks(1), preferences)); Assert.Equal(TimeSpan.FromHours(1), clock.WorkTime); Assert.Equal(strict, clock.IsOnBreak);
        if (strict)
        {
            Assert.False(clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1), preferences)); Assert.True(clock.IsOnBreak); Assert.Equal(TimeSpan.FromHours(1), clock.WorkTime);
            clock.Advance(TimeSpan.FromTicks(1), preferences); Assert.False(clock.IsOnBreak);
        }
        clock.Reset(); Assert.Equal(TimeSpan.Zero, clock.WorkTime); Assert.Equal(TimeSpan.Zero, clock.SinceBreak);
        Assert.False(clock.Advance(TimeSpan.FromHours(2), preferences with { BreakReminders = false }));
    }
}
