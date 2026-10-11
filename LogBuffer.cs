namespace KNE;

/// <summary>Bounded producer queue with finite batches for UI rendering.</summary>
public sealed class LogBuffer
{
    private readonly Queue<string> pending = new();
    private readonly object sync = new();

    public void Enqueue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (sync)
        {
            pending.Enqueue(text.Length > 4096 ? text[..4096] + "…" : text);
            while (pending.Count > 1000) pending.Dequeue();
        }
    }

    public IReadOnlyList<string> Drain(int maximum = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        lock (sync)
        {
            var batch = new List<string>(Math.Min(maximum, pending.Count));
            while (batch.Count < maximum && pending.TryDequeue(out string? item)) batch.Add(item);
            return batch;
        }
    }
}
