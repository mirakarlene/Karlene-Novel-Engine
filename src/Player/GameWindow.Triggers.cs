using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private readonly GameTriggerInput triggerInput = new();
    private readonly Queue<TriggerSignal> pendingTriggerSignals = new();
    private bool dispatchingTriggers;
    private bool HasButtonTrigger(string id) => ready && runtime.Story.Triggers.Any(trigger => trigger.Matches(new("button", Button: id)));

    private void InitializeTriggerInput()
    {
        PreviewKeyUp += (_, args) => triggerInput.KeyUp(args);
        Deactivated += (_, _) => triggerInput.Reset();
        SceneArea.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (!ready || startupFailed || execution != null) return;
            var signal = GameTriggerInput.PointerSignal(runtime.Story, GameTriggerInput.FindButton(args.OriginalSource as DependencyObject), project.Engine.Screens, args.GetPosition(SceneArea), SceneArea.RenderSize);
            if (!runtime.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true; await DispatchTriggerAsync(signal);
        };
        DialoguePanel.IsVisibleChanged += (_, args) =>
        {
            if (ready && !startupFailed && !closed && args.OldValue is true && args.NewValue is false)
            {
                pendingTriggerSignals.Enqueue(new("text_closed"));
                _ = Dispatcher.BeginInvoke(new Action(async () => await DrainTriggerSignalsAsync()));
            }
        };
    }
    private async Task DrainTriggerSignalsAsync()
    {
        if (closed || startupFailed || dispatchingTriggers || execution != null) return;
        dispatchingTriggers = true;
        try { while (pendingTriggerSignals.TryDequeue(out var signal)) await DispatchTriggerAsync(signal); }
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
