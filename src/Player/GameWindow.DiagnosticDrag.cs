using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

public partial class GameWindow
{
    private TranslateTransform? consoleDragOffset;

    private void UpdateDiagnosticDragHandles()
    {
        bool enabled = project.Engine.Performance.DragDiagnostics;
        StatusText.Background = enabled && detachedConsole == null ? Brushes.Transparent : null;
        StatusText.Cursor = enabled && detachedConsole == null ? Cursors.SizeAll : null;
        NoticePanel.Cursor = enabled ? Cursors.SizeAll : null;
        if (performanceOverlay != null)
        {
            performanceOverlay.IsHitTestVisible = enabled;
            performanceOverlay.Cursor = enabled ? Cursors.SizeAll : null;
        }
    }

    private static TranslateTransform EnableDiagnosticDrag(FrameworkElement panel, FrameworkElement handle,
        FrameworkElement host, Func<bool> enabled)
    {
        var offset = new TranslateTransform();
        panel.RenderTransform = offset;
        Point previous = default;
        handle.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (!enabled() || !handle.CaptureMouse()) return;
            previous = args.GetPosition(host);
            args.Handled = true;
        };
        handle.PreviewMouseMove += (_, args) =>
        {
            if (!handle.IsMouseCaptured) return;
            if (!enabled() || args.LeftButton != MouseButtonState.Pressed) { handle.ReleaseMouseCapture(); return; }
            Point position = args.GetPosition(host);
            MoveDiagnosticPanel(panel, host, offset, position - previous);
            previous = position;
            args.Handled = true;
        };
        handle.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (!handle.IsMouseCaptured) return;
            handle.ReleaseMouseCapture();
            args.Handled = true;
        };
        host.SizeChanged += (_, _) =>
        {
            if (enabled()) MoveDiagnosticPanel(panel, host, offset, default);
        };
        panel.SizeChanged += (_, _) =>
        {
            if (enabled()) MoveDiagnosticPanel(panel, host, offset, default);
        };
        return offset;
    }

    private static void MoveDiagnosticPanel(FrameworkElement panel, FrameworkElement host, TranslateTransform offset, Vector movement)
    {
        if (!host.IsAncestorOf(panel) || panel.ActualWidth <= 0 || panel.ActualHeight <= 0) return;
        var bounds = panel.TransformToAncestor(host).TransformBounds(new Rect(panel.RenderSize));
        offset.X += Math.Clamp(movement.X, -bounds.Left, Math.Max(-bounds.Left, host.ActualWidth - bounds.Right));
        offset.Y += Math.Clamp(movement.Y, -bounds.Top, Math.Max(-bounds.Top, host.ActualHeight - bounds.Bottom));
    }
}
