using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    private bool panning;
    private Point panStart;
    private Point panOrigin;

    private string DescribeNodeIdentity(ScriptNode node)
    {
        var entry = frameCatalog?.Entries.FirstOrDefault(entry => entry.NodeId == node.Id &&
            Path.GetRelativePath(projectRoot, Document!.FilePath).Replace('\\', '/').Equals(entry.File, StringComparison.OrdinalIgnoreCase));
        return entry == null ? "Блок " + node.Id + " · числовой ID будет назначен при сохранении."
            : $"Блок {node.Id} · ID в сцене: {entry.Identity.SceneFrameId} · ID в игре: {entry.Identity.GlobalFrameId}";
    }

    public void PanGraph(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        CanvasPan.X += x; CanvasPan.Y += y;
    }
    public void ChangeZoom(int steps) => ZoomSlider.Value = Math.Clamp(Math.Round(ZoomSlider.Value + steps * .1, 2), ZoomSlider.Minimum, ZoomSlider.Maximum);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-1);

    private void GraphPan_Start(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? target = e.OriginalSource as DependencyObject; target != null && target != GraphScroll; target = VisualTreeHelper.GetParent(target))
            if (target is Button) return;
        panning = true; panStart = e.GetPosition(GraphScroll); panOrigin = new Point(CanvasPan.X, CanvasPan.Y);
        GraphScroll.CaptureMouse(); GraphScroll.Cursor = Cursors.SizeAll; e.Handled = true;
    }
    private void GraphPan_Move(object sender, MouseEventArgs e)
    {
        if (!panning) return;
        var current = e.GetPosition(GraphScroll);
        CanvasPan.X = panOrigin.X + current.X - panStart.X; CanvasPan.Y = panOrigin.Y + current.Y - panStart.Y;
        e.Handled = true;
    }
    private void GraphPan_End(object sender, MouseButtonEventArgs e)
    {
        if (!panning) return;
        panning = false; GraphScroll.ReleaseMouseCapture(); GraphScroll.Cursor = null; e.Handled = true;
    }
    private void GraphPan_LostCapture(object sender, MouseEventArgs e) { panning = false; GraphScroll.Cursor = null; }

    private void UpdateGraphExtent()
    {
        var nodes = Document?.Graph?.Nodes;
        double left = Math.Min(0, nodes?.Select(n => n.X).DefaultIfEmpty(0).Min() ?? 0);
        double top = Math.Min(0, nodes?.Select(n => n.Y).DefaultIfEmpty(0).Min() ?? 0);
        GraphCanvas.Width = Math.Max(1600, (nodes?.Select(n => n.X + 410).DefaultIfEmpty(0).Max() ?? 0) - left);
        GraphCanvas.Height = Math.Max(1000, (nodes?.Select(n => n.Y + 260 + n.Outputs().Count() * 28).DefaultIfEmpty(0).Max() ?? 0) - top);
    }
}
