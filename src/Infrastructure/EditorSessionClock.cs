namespace KNE;

/// <summary>Monotonic editor-session time; strict breaks do not count as working time.</summary>
internal sealed class EditorSessionClock
{
    internal TimeSpan WorkTime { get; private set; }
    internal TimeSpan SinceBreak { get; private set; }
    internal TimeSpan BreakRemaining { get; private set; }
    internal bool IsOnBreak => BreakRemaining > TimeSpan.Zero;
    internal bool Advance(TimeSpan elapsed, EditorPreferences preferences)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (IsOnBreak) { BreakRemaining = BreakRemaining > elapsed ? BreakRemaining - elapsed : TimeSpan.Zero; return false; }
        WorkTime += elapsed; SinceBreak += elapsed;
        if (!preferences.BreakReminders || SinceBreak < TimeSpan.FromMinutes(preferences.BreakMinutes)) return false;
        SinceBreak = TimeSpan.Zero;
        if (preferences.StrictBreak) BreakRemaining = TimeSpan.FromMinutes(preferences.BreakDuration);
        return true;
    }
    internal void EndBreak() { BreakRemaining = TimeSpan.Zero; SinceBreak = TimeSpan.Zero; }
    internal void Reset() { WorkTime = TimeSpan.Zero; SinceBreak = TimeSpan.Zero; }
}
