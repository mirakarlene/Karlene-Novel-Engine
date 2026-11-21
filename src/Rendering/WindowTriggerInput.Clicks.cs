using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace KNE;

internal sealed partial class WindowTriggerInput
{
    private readonly Dictionary<string, Click> pressedClicks = [];
    private readonly Dictionary<string, Click> previousClicks = [];
    private readonly List<Click> deferredClicks = [];
    private readonly HashSet<string> suppressedReleases = [];
    private sealed record Click(Point Position, long Timestamp, double Interval)
    {
        public int? InputTimestamp { get; set; }
        public TriggerSignal? Single { get; set; }
    }

    private void ResetClicks(bool retainCompletedClicks)
    {
        pressedClicks.Clear();
        previousClicks.Clear();
        if (!retainCompletedClicks) deferredClicks.Clear();
    }

    private bool TrackClickDown(WindowTriggerScope current, string button, Point point)
    {
        var matching = current.Triggers.Where(trigger => trigger.Kind == "double_click" && trigger.EffectiveMouseButton == button).ToArray();
        if (matching.Length == 0) return false;
        var click = new Click(point, Stopwatch.GetTimestamp(), matching.Max(trigger => trigger.EffectiveDoubleClickMilliseconds));
        if (previousClicks.Remove(button, out var previous) && Nearby(previous.Position, point))
        {
            var doubleClick = new TriggerSignal("double_click", Button: button,
                Elapsed: Stopwatch.GetElapsedTime(previous.Timestamp, click.Timestamp).TotalMilliseconds);
            if (matching.Any(trigger => trigger.Matches(doubleClick)))
            {
                deferredClicks.Remove(previous);
                suppressedReleases.Add(button);
                publish(current.Visit, doubleClick);
                return true;
            }
        }
        pressedClicks[button] = click;
        return false;
    }

    private void TrackClickUp(string button, Point point, MouseButtonEventArgs args)
    {
        if (!pressedClicks.Remove(button, out var click) || !Nearby(click.Position, point)) return;
        click.InputTimestamp = args.Timestamp;
        previousClicks[button] = click;
    }

    internal bool DeferSingleClick(MouseButtonEventArgs args, TriggerSignal signal)
    {
        if (MouseName(args.ChangedButton) is not { } button || !previousClicks.TryGetValue(button, out var click) ||
            click.InputTimestamp != args.Timestamp || Stopwatch.GetElapsedTime(click.Timestamp).TotalMilliseconds >= click.Interval)
            return false;
        click.Single = signal;
        deferredClicks.Add(click);
        clock.Start();
        return true;
    }

    private bool FlushClicks(WindowTriggerScope current)
    {
        for (int index = 0; index < deferredClicks.Count;)
        {
            var click = deferredClicks[index];
            if (Stopwatch.GetElapsedTime(click.Timestamp).TotalMilliseconds < click.Interval) { index++; continue; }
            deferredClicks.RemoveAt(index);
            Emit(current, click.Single!);
        }
        return deferredClicks.Count > 0;
    }

    private static bool Nearby(Point first, Point second) =>
        Math.Abs(first.X - second.X) <= SystemParameters.MinimumHorizontalDragDistance &&
        Math.Abs(first.Y - second.Y) <= SystemParameters.MinimumVerticalDragDistance;
}
