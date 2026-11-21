using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace KNE;

internal sealed record WindowTriggerScope(long Visit, IReadOnlyList<TriggerSettings> Triggers);

/// <summary>Recognizes stateful input once, before it reaches the replayable scenario event protocol.</summary>
internal sealed partial class WindowTriggerInput
{
    private readonly Window window;
    private readonly FrameworkElement area;
    private readonly Func<WindowTriggerScope?> scope;
    private readonly Action<long, TriggerSignal> publish;
    private readonly DispatcherTimer clock;
    private readonly List<string> keys = [];
    private readonly HashSet<Key> keysDown = [];
    private readonly Dictionary<(string Key, string Button), Hold> holds = [];
    private readonly Dictionary<string, List<Point>> gestures = [];
    private long? activeVisit;
    private Point? pointer;
    private long? lastResize;

    private sealed record Hold(long Started, double[] Thresholds)
    {
        public int Next { get; set; }
    }

    internal WindowTriggerInput(Window window, FrameworkElement area, Func<WindowTriggerScope?> scope, Action<long, TriggerSignal> publish)
    {
        this.window = window;
        this.area = area;
        this.scope = scope;
        this.publish = publish;
        clock = new(DispatcherPriority.Input, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(16) };
        clock.Tick += (_, _) => Tick();
        window.PreviewKeyUp += (_, args) => ReleaseKey(args);
        window.PreviewMouseMove += (_, args) => MovePointer(args);
        window.MouseLeave += (_, _) => LeaveWindow();
        window.PreviewMouseWheel += (_, args) =>
        {
            if (args.Delta != 0 && Current() is { } current) Emit(current, new("wheel", Button: args.Delta > 0 ? "up" : "down"));
        };
        window.PreviewMouseDown += (_, args) => MouseDown(args);
        window.PreviewMouseUp += (_, args) => MouseUp(args);
        window.Activated += (_, _) => Focus("gained");
        window.Deactivated += (_, _) =>
        {
            long? pendingResize = lastResize;
            ClearTracking(retainCompletedClicks: true);
            suppressedReleases.Clear();
            Focus("lost");
            if (pendingResize != null && activeVisit != null)
            {
                lastResize = pendingResize;
                clock.Start();
            }
            if (deferredClicks.Count > 0) clock.Start();
        };
        window.SizeChanged += (_, args) => Resize(args.NewSize);
        window.Closed += (_, _) => Reset();
    }

    private WindowTriggerScope? Current()
    {
        var current = scope();
        if (current?.Visit != activeVisit) { ClearTracking(); activeVisit = current?.Visit; }
        return current;
    }

    internal void Reset()
    {
        activeVisit = null;
        ClearTracking();
    }

    private void ClearTracking(bool retainCompletedClicks = false)
    {
        keys.Clear();
        keysDown.Clear();
        holds.Clear();
        gestures.Clear();
        ResetClicks(retainCompletedClicks);
        pointer = null;
        lastResize = null;
        clock.Stop();
    }

    private void Emit(WindowTriggerScope current, TriggerSignal signal)
    {
        if (current.Triggers.Any(trigger => trigger.Matches(signal))) publish(current.Visit, signal);
    }

    internal bool KeyDown(KeyEventArgs args)
    {
        if (args.IsRepeat || Current() is not { } current) return false;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        if (key is Key.None or Key.System or Key.DeadCharProcessed or Key.ImeProcessed) return false;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.C or Key.V or Key.X)
            Emit(current, new("clipboard", Button: key == Key.C ? "copy" : key == Key.V ? "paste" : "cut"));
        if (GameTriggerInput.IsEditing(args.OriginalSource as DependencyObject)) return false;
        string name = TriggerSettings.NormalizeSingleKey(key.ToString());
        AddHold(current, name, "");
        if (!keysDown.Add(key) || keysDown.Count != 1) { keys.Clear(); return false; }
        return AcceptSequenceKey(current, name);
    }

    private bool AcceptSequenceKey(WindowTriggerScope current, string key)
    {
        var sequences = current.Triggers.Where(trigger => trigger.Kind == "sequence")
            .Select(trigger => TriggerSettings.NormalizeSequence(trigger.Keys)).Distinct(StringComparer.Ordinal)
            .Select(value => (Text: value, Keys: value.Split(','))).ToArray();
        if (sequences.Length == 0) { keys.Clear(); return false; }
        keys.Add(key);
        int excess = keys.Count - sequences.Max(item => item.Keys.Length);
        if (excess > 0) keys.RemoveRange(0, excess);
        bool matched = false;
        foreach (var sequence in sequences)
        {
            if (keys.Count < sequence.Keys.Length || !keys.TakeLast(sequence.Keys.Length).SequenceEqual(sequence.Keys)) continue;
            publish(current.Visit, new("sequence", Keys: sequence.Text));
            matched = true;
        }
        return matched;
    }

    private void ReleaseKey(KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        keysDown.Remove(key);
        if (key is not (Key.None or Key.System or Key.DeadCharProcessed or Key.ImeProcessed))
            holds.Remove((TriggerSettings.NormalizeSingleKey(key.ToString()), ""));
    }

    private void AddHold(WindowTriggerScope current, string key, string button)
    {
        var thresholds = current.Triggers.Where(trigger => trigger.Kind == "long_press" && (trigger.MouseButton == null
                ? button.Length == 0 && TriggerSettings.NormalizeSingleKey(trigger.Keys) == key : trigger.MouseButton == button))
            .Select(trigger => trigger.EffectiveHoldSeconds).Distinct().Order().ToArray();
        if (thresholds.Length == 0) return;
        if (holds.TryAdd((key, button), new Hold(Stopwatch.GetTimestamp(), thresholds))) clock.Start();
    }

    private void MovePointer(MouseEventArgs args)
    {
        if (Current() is not { } current) return;
        var position = args.GetPosition(window);
        if (pointer == position) return;
        foreach (var path in gestures.Values)
            if ((path[^1] - position).Length >= 2 && path.Count < 512) path.Add(position);
        Point? previous = pointer;
        pointer = position;
        if (!current.Triggers.Any(trigger => trigger.Kind is "pointer_move" or "pointer_enter_zone")) return;
        var at = GameTriggerInput.Hotspot(args.GetPosition(area), area.RenderSize);
        Emit(current, at with { Kind = "pointer_move" });
        if (previous is { } prior)
        {
            Point old = window.TranslatePoint(prior, area);
            var priorSignal = GameTriggerInput.Hotspot(old, area.RenderSize);
            Emit(current, at with { Kind = "pointer_enter_zone", PreviousX = priorSignal.X, PreviousY = priorSignal.Y });
        }
        else Emit(current, at with { Kind = "pointer_enter_zone" });
    }

    private void LeaveWindow()
    {
        pointer = null;
        gestures.Clear();
        foreach (var input in holds.Keys.Where(input => input.Button != "").ToArray()) holds.Remove(input);
        if (Current() is { } current) Emit(current, new("pointer_leave"));
    }

    private static string? MouseName(MouseButton button) => button switch
    { MouseButton.Left => "left", MouseButton.Right => "right", MouseButton.Middle => "middle", _ => null };

    private void MouseDown(MouseButtonEventArgs args)
    {
        if (Current() is not { } current || MouseName(args.ChangedButton) is not { } button) return;
        var point = args.GetPosition(window);
        suppressedReleases.Remove(button);
        if (TrackClickDown(current, button, point)) { args.Handled = true; return; }
        AddHold(current, "", button);
        if (current.Triggers.Any(trigger => trigger.Kind == "gesture" && trigger.EffectiveMouseButton == button))
            gestures[button] = [point];
    }

    private void MouseUp(MouseButtonEventArgs args)
    {
        if (MouseName(args.ChangedButton) is not { } button) return;
        holds.Remove(("", button));
        if (suppressedReleases.Remove(button)) { args.Handled = true; return; }
        if (Current() is not { } current) return;
        var position = args.GetPosition(window);
        if (gestures.Remove(button, out var path))
        {
            path.Add(position);
            if (MouseGesture.Identify(path) is { } gesture)
                Emit(current, new("gesture", Keys: gesture, Button: button));
        }
        TrackClickUp(button, position, args);
    }

    private void Focus(string direction)
    {
        if (Current() is { } current) Emit(current, new("focus", Button: direction));
    }

    private void Resize(Size size)
    {
        if (Current() is not { } current) return;
        Emit(current, new("resize", Button: "changing", X: size.Width, Y: size.Height));
        if (current.Triggers.Any(trigger => trigger.Kind == "resize" && trigger.EffectiveMode == "finished"))
        {
            lastResize = Stopwatch.GetTimestamp();
            clock.Start();
        }
    }

    private void Tick()
    {
        if (Current() is not { } current) { Reset(); return; }
        foreach (var (input, hold) in holds.ToArray())
        {
            double elapsed = Stopwatch.GetElapsedTime(hold.Started).TotalSeconds;
            while (hold.Next < hold.Thresholds.Length && elapsed >= hold.Thresholds[hold.Next])
                Emit(current, new("long_press", Keys: input.Key, Button: input.Button, Elapsed: hold.Thresholds[hold.Next++]));
        }
        if (lastResize is long started && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 250)
        {
            lastResize = null;
            Emit(current, new("resize", Button: "finished", X: window.ActualWidth, Y: window.ActualHeight));
        }
        bool clickPending = FlushClicks(current);
        if (lastResize == null && !clickPending && holds.Values.All(hold => hold.Next == hold.Thresholds.Length)) clock.Stop();
    }
}
