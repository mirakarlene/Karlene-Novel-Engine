using System.IO;

namespace KNE;

/// <summary>A line or chosen answer retained for display in the game's history.</summary>
public sealed record DialogueHistoryEntry(long Id, string Speaker, string Text, bool Choice, bool RolledBack = false);

/// <summary>The active position and configured size of the journal at a checkpoint.</summary>
public sealed record DialogueJournalPosition(long Id, int Limit);

/// <summary>The saved journal includes entries from branches the player rolled back.</summary>
public sealed record DialogueJournalSnapshot(DialogueJournalPosition Position, long LastId, IReadOnlyList<DialogueHistoryEntry> Entries)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int Limit => Position.Limit;

    internal DialogueJournalSnapshot Rewind(DialogueJournalPosition position) => new(position, Math.Max(LastId, position.Id), Entries
        .TakeLast(position.Limit)
        .Select(entry => entry.Id > position.Id ? entry with { RolledBack = true } : entry)
        .ToArray());

    internal void Validate()
    {
        if (Position is null || Position.Id < 0 || Position.Id > LastId || Entries is null ||
            Entries.Count > DialogueJournal.ValidateLimit(Position.Limit))
            throw new InvalidDataException("Повреждена история реплик сохранения.");
        long prior = 0;
        long characters = 0;
        foreach (var entry in Entries)
        {
            if (entry == null || entry.Id <= prior || entry.Id > LastId || entry.Speaker == null || entry.Text == null ||
                entry.Speaker.Length > DialogueJournal.MaxText || entry.Text.Length > DialogueJournal.MaxText ||
                !entry.RolledBack && entry.Id > Position.Id)
                throw new InvalidDataException("Повреждена запись истории реплик.");
            prior = entry.Id;
            characters += entry.Speaker.Length + entry.Text.Length;
        }
        if (characters > DialogueJournal.MaxCharacters)
            throw new InvalidDataException("История реплик слишком велика.");
    }
}

internal sealed class DialogueJournal
{
    internal const int DefaultLimit = 100;
    internal const int MaximumLimit = 1000;
    internal const int MaxText = 32_768;
    internal const int MaxCharacters = 350_000;
    private readonly List<DialogueHistoryEntry> entries = [];
    private int limit = DefaultLimit;
    private long lastId, position;
    private int characters;
    private DialogueJournalSnapshot? snapshot;

    internal DialogueJournalSnapshot Snapshot => snapshot ??= new(new(position, limit), lastId, entries.ToArray());

    internal static int ValidateLimit(double limit)
    {
        if (!double.IsFinite(limit) || limit < 0 || limit > MaximumLimit || limit != Math.Truncate(limit))
            throw new InvalidDataException($"Размер истории: целое число от 0 до {MaximumLimit}.");
        return (int)limit;
    }

    internal void SetLimit(int size)
    {
        if (limit == size) return;
        limit = size;
        Trim();
        snapshot = null;
    }

    internal void Record(string speaker, string text, bool choice, bool sameLine = false)
    {
        if (limit == 0 || string.IsNullOrEmpty(text)) return;
        if (speaker.Length > MaxText) speaker = speaker[..MaxText];
        if (text.Length > MaxText) text = text[..MaxText];
        if (sameLine)
        {
            int index = entries.FindLastIndex(entry => entry.Id == position && !entry.Choice && !entry.RolledBack);
            if (index < 0 || entries[index].Text == text && entries[index].Speaker == speaker) return;
            characters -= entries[index].Speaker.Length + entries[index].Text.Length;
            entries[index] = entries[index] with { Speaker = speaker, Text = text };
        }
        else
        {
            position = ++lastId;
            entries.Add(new(position, speaker, text, choice));
        }
        characters += speaker.Length + text.Length;
        Trim();
        snapshot = null;
    }

    internal void Restore(DialogueJournalSnapshot value)
    {
        value.Validate();
        entries.Clear();
        entries.AddRange(value.Entries);
        limit = value.Limit;
        position = value.Position.Id;
        lastId = value.LastId;
        characters = entries.Sum(entry => entry.Speaker.Length + entry.Text.Length);
        snapshot = null;
    }

    private void Trim()
    {
        int remove = 0;
        while (entries.Count - remove > limit || characters > MaxCharacters)
        {
            var entry = entries[remove++];
            characters -= entry.Speaker.Length + entry.Text.Length;
        }
        if (remove > 0) entries.RemoveRange(0, remove);
    }
}
