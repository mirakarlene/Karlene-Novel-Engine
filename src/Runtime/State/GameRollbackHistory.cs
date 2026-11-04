using System.IO;

namespace KNE;

/// <summary>Bounded rollback checkpoints, separate from the complete deterministic replay log in each save.</summary>
internal sealed class GameRollbackHistory(int depth)
{
    private readonly List<GameCheckpoint> entries = [];
    internal int Count => entries.Count;
    internal void Clear() => entries.Clear();
    internal void Push(GameCheckpoint previous)
    {
        if (depth <= 0) return;
        entries.Add(previous with { Rollback = null }); while (entries.Count > depth) entries.RemoveAt(0);
    }
    internal GameCheckpoint Peek() => entries.Count == 0 ? throw new InvalidOperationException("История отката пуста.") : entries[^1];
    internal void Pop() { _ = Peek(); entries.RemoveAt(entries.Count - 1); }
    internal GameCheckpoint Attach(GameCheckpoint current) => current with { Rollback = entries.ToArray() };
    internal void Restore(GameCheckpoint current)
    {
        Validate(current); entries.Clear(); entries.AddRange((current.Rollback ?? []).TakeLast(Math.Max(0, depth)).Select(c => c with { Rollback = null }));
    }
    internal static void Validate(GameCheckpoint current)
    {
        if (current.Rollback is not { } rollback) return;
        if (rollback.Count > 1000 || rollback.Any(c => c == null || c.Version != 1 || c.ProjectPath != current.ProjectPath || c.Fingerprint != current.Fingerprint || c.History == null || c.History.Count > 10000 || c.Frame == null || c.Frame.Speaker == null || c.Frame.Text == null || c.Frame.Options == null || c.Frame.Options.Count > 20 || c.Frame.Options.Any(string.IsNullOrWhiteSpace) || c.Rollback is { Count: > 0 } || c.State.ValueKind != System.Text.Json.JsonValueKind.Object))
            throw new InvalidDataException("Повреждена история отката сохранения.");
    }
}

/// <summary>Counts only completed player actions, not rendering ticks, saves, loads, or menu navigation.</summary>
internal sealed class GameAutosavePolicy
{
    private int actions;
    internal void Reset() => actions = 0;
    internal bool Completed(GameDefaults settings, string previousKind)
    {
        if (!settings.AutoSave) return false;
        actions++;
        return settings.AutoSaveInterval switch { "choice" => previousKind == "choice", "actions" => actions % settings.AutoSaveActions == 0, _ => previousKind == "dialogue" };
    }
}
