using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class SceneTestingWindow
{
    private readonly GameTriggerInput triggerInput = new();
    private void InitializeTestingTriggers()
    {
        PreviewKeyDown += async (_, args) =>
        {
            if (!sceneMode || scene == null || busy || closed) return;
            var signal = triggerInput.KeyDown(args);
            if (signal == null || !scene.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true; await ExecuteAsync(() => scene.TriggerAsync(signal, lifetime.Token));
        };
        PreviewKeyUp += (_, args) => triggerInput.KeyUp(args);
        Deactivated += (_, _) => triggerInput.Reset();
        SceneSurface.PreviewMouseLeftButtonUp += async (_, args) =>
        {
            if (!sceneMode || scene == null || busy || closed) return;
            var area = (FrameworkElement)SceneSurface.FindName("SceneArea");
            var signal = GameTriggerInput.PointerSignal(scene.Story, GameTriggerInput.FindButton(args.OriginalSource as DependencyObject), project.Engine.Screens, args.GetPosition(area), area.RenderSize);
            if (!scene.Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            args.Handled = true; await ExecuteAsync(() => scene.TriggerAsync(signal, lifetime.Token));
        };
    }
}
