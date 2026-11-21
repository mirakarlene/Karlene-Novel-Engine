using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private readonly GameTriggerInput triggerInput = new();
    private readonly TriggerSignalQueue pendingTriggerSignals = new();
    private WindowTriggerInput advancedTriggers = null!;
    private bool dispatchingTriggers;
    private bool triggerDrainScheduled;
    private long SceneVisit => runtime.Visuals.Scene?.Visit ?? 0;
    private bool HasButtonTrigger(string id) => ready && runtime.Story.Triggers.Any(trigger => trigger.Matches(new("button", Button: id)));

    private void InitializeTriggerInput()
    {
        advancedTriggers = new(this, SceneArea, TriggerScope, QueueTrigger);
        PreviewKeyUp += (_, args) => triggerInput.KeyUp(args);
        Deactivated += (_, _) => triggerInput.Reset();
        SceneArea.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (!ready || startupFailed || execution != null) return;
            var signal = GameTriggerInput.PointerSignal(runtime.Story, GameTriggerInput.FindButton(args.OriginalSource as DependencyObject), project.Engine.Screens, args.GetPosition(SceneArea), SceneArea.RenderSize);
            if (!runtime.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true;
            if (!advancedTriggers.DeferSingleClick(args, signal)) await DispatchTriggerAsync(signal);
        };
        DialoguePanel.IsVisibleChanged += (_, args) =>
        {
            if (ready && !startupFailed && !closed && args.OldValue is true && args.NewValue is false)
            {
                QueueTrigger(SceneVisit, new("text_closed"));
            }
        };
    }
    private WindowTriggerScope? TriggerScope()
    {
        if (!ready || closed || startupFailed || systemFrozen || videoPlayback != null || MenuOverlay.Visibility == Visibility.Visible)
            return null;
        var story = runtime.Story;
        return !story.Completed && story.Triggers.Count != 0 ? new(SceneVisit, story.Triggers) : null;
    }

    private void QueueTrigger(long visit, TriggerSignal signal)
    {
        pendingTriggerSignals.Enqueue(visit, signal);
        ScheduleTriggerDrain();
    }

    private void ScheduleTriggerDrain()
    {
        if (closed || dispatchingTriggers || triggerDrainScheduled || execution != null || !pendingTriggerSignals.HasItems) return;
        triggerDrainScheduled = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            triggerDrainScheduled = false;
            await DrainTriggerSignalsAsync();
        }));
    }

    private void ResetTriggerInput()
    {
        triggerInput.Reset();
        advancedTriggers.Reset();
        pendingTriggerSignals.Clear();
    }

    private async Task DrainTriggerSignalsAsync()
    {
        if (closed || startupFailed || dispatchingTriggers || execution != null) return;
        dispatchingTriggers = true;
        try
        {
            while (!closed && execution == null && pendingTriggerSignals.TryTake(SceneVisit, out var signal))
                if (signal.Kind == "text_closed" || TriggerScope() != null) await DispatchTriggerAsync(signal);
        }
        finally { dispatchingTriggers = false; }
    }
    private async Task<bool> DispatchTriggerAsync(TriggerSignal signal)
    {
        if (!ready || closed || startupFailed || execution != null || !runtime.Story.Triggers.Any(trigger => trigger.Matches(signal))) return false;
        await ExecuteAsync(() => runtime.TriggerAsync(signal, execution!.Token), operation: "Событие триггера");
        return true;
    }
    private Task<bool> TryButtonTriggerAsync(object sender)
    {
        string? id = GameTriggerInput.ButtonId(sender as Button, project.Engine.Screens);
        return id == null ? Task.FromResult(false) : DispatchTriggerAsync(new("button", Button: id));
    }
}
