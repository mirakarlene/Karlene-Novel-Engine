namespace KNE;

/// <summary>Retains discrete input and resize events while the worker is busy and coalesces successive pointer samples.</summary>
internal sealed class TriggerSignalQueue
{
    private readonly LinkedList<(long Visit, TriggerSignal Signal)> items = new();

    internal bool HasItems => items.Count != 0;

    internal void Enqueue(long visit, TriggerSignal signal)
    {
        if (items.Last is { Value: var last } tail && last.Visit == visit && signal.Kind == "pointer_move" && last.Signal.Kind == signal.Kind)
        {
            tail.Value = (visit, signal);
            return;
        }
        items.AddLast((visit, signal));
    }

    internal bool TryTake(long visit, out TriggerSignal signal)
    {
        while (items.First is { Value: var entry })
        {
            items.RemoveFirst();
            if (entry.Visit == visit) { signal = entry.Signal; return true; }
        }
        signal = null!;
        return false;
    }

    internal void Clear() => items.Clear();
}
