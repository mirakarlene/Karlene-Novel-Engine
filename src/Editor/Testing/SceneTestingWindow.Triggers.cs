using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class SceneTestingWindow
{
    private readonly GameTriggerInput triggerInput = new();
    private readonly TriggerSignalQueue testingTriggers = new();
    private WindowTriggerInput advancedTestingTriggers = null!;
    private bool drainingTestingTriggers;
    private long TestingVisit => scene?.Visuals.Scene?.Visit ?? 0;

    private void InitializeTestingTriggers()
    {
        var area = (FrameworkElement)SceneSurface.FindName("SceneArea");
        advancedTestingTriggers = new(this, area, () =>
            !closed && sceneMode && scene is { Completed: false } active && active.Story.Triggers.Count != 0 ? new(TestingVisit, active.Story.Triggers) : null,
            (visit, signal) => { testingTriggers.Enqueue(visit, signal); ScheduleTestingTriggers(); });
        PreviewKeyDown += async (_, args) =>
        {
            if (!sceneMode || scene == null || closed) return;
            if (advancedTestingTriggers.KeyDown(args)) { args.Handled = true; return; }
            if (busy) return;
            var signal = triggerInput.KeyDown(args);
            if (signal == null || !scene.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true; await ExecuteAsync(() => scene.TriggerAsync(signal, lifetime.Token));
        };
        PreviewKeyUp += (_, args) => triggerInput.KeyUp(args);
        Deactivated += (_, _) => triggerInput.Reset();
        SceneSurface.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (!sceneMode || scene == null || busy || closed) return;
            var signal = GameTriggerInput.PointerSignal(scene.Story, GameTriggerInput.FindButton(args.OriginalSource as DependencyObject), project.Engine.Screens, args.GetPosition(area), area.RenderSize);
            if (!scene.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true;
            if (!advancedTestingTriggers.DeferSingleClick(args, signal)) await ExecuteAsync(() => scene.TriggerAsync(signal, lifetime.Token));
        };
    }

    private void ScheduleTestingTriggers()
    {
        if (closed || busy || drainingTestingTriggers || !testingTriggers.HasItems) return;
        drainingTestingTriggers = true;
        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                while (!closed && !busy && scene is { Completed: false } active && testingTriggers.TryTake(TestingVisit, out var signal))
                    if (active.Story.Triggers.Any(trigger => trigger.Matches(signal)))
                        await ExecuteAsync(() => active.TriggerAsync(signal, lifetime.Token));
            }
            finally { drainingTestingTriggers = false; }
        }));
    }

    private void ResetTestingTriggers()
    {
        triggerInput.Reset();
        advancedTestingTriggers.Reset();
        testingTriggers.Clear();
    }
}
