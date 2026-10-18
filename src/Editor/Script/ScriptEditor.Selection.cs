using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KNE;

public partial class ScriptEditor
{
    private const string GraphClipboardFormat = "KNE.VisualBlocks.v1";
    private readonly HashSet<string> selectedIds = new(StringComparer.Ordinal);
    private Rectangle? selectionRectangle;
    private Point? selectionOrigin;
    private HashSet<string> selectionBefore = [];
    private bool selectionAdditive;

    internal Func<string?> ReadGraphClipboard { get; set; } = () => Clipboard.GetData(GraphClipboardFormat) as string;
    internal Action<string> WriteGraphClipboard { get; set; } = text => Clipboard.SetData(GraphClipboardFormat, text);
    internal IReadOnlySet<string> SelectedBlockIds => selectedIds;

    private void InitializeGraphSelection()
    {
        GraphScroll.Focusable = true;
        GraphScroll.PreviewMouseLeftButtonDown += Selection_Start;
        GraphScroll.PreviewMouseMove += Selection_Move;
        GraphScroll.PreviewMouseLeftButtonUp += Selection_End;
        GraphScroll.LostMouseCapture += (_, _) => FinishMarquee();
    }

    internal void SelectGraphBlocks(IEnumerable<string> ids)
    {
        var requested = ids.ToArray();
        ApplyProperties(repaint: false);
        var existing = Document?.Graph?.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        selectedIds.Clear();
        selectedIds.UnionWith(requested.Where(existing.Contains));
        selectedId = selectedIds.FirstOrDefault();
        RefreshCardSelection();
        UpdateProperties();
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SynchronizeGraphSelection()
    {
        var nodes = Document?.Graph?.Nodes;
        selectedIds.RemoveWhere(id => nodes?.Any(node => node.Id == id) != true);
        if (selectedId != null && nodes?.Any(node => node.Id == selectedId) == true)
            selectedIds.Add(selectedId);
        else selectedId = selectedIds.FirstOrDefault();
    }

    internal void CopyGraphSelection()
    {
        ApplyProperties();
        if (Document?.Graph is not { } graph) return;
        SynchronizeGraphSelection();
        var copy = VisualScript.Parse(graph.Serialize());
        copy.Nodes.RemoveAll(node => !selectedIds.Contains(node.Id) || node.Kind == ScriptNodeKind.Start);
        if (copy.Nodes.Count == 0)
        {
            EditorStatus.Text = "Выберите блоки для копирования. Блок «Начало» должен оставаться единственным.";
            return;
        }
        var included = copy.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        DetachExternalConnections(copy.Nodes, included);
        WriteGraphClipboard(copy.Serialize());
        EditorStatus.Text = $"Скопировано блоков: {copy.Nodes.Count}. Внутренние связи сохранены.";
    }

    internal void PasteGraphSelection() => PasteGraphBlocks(ReadGraphClipboard());

    internal void DuplicateGraphSelection()
    {
        ApplyProperties();
        if (Document?.Graph is not { } graph) return;
        SynchronizeGraphSelection();
        var copy = VisualScript.Parse(graph.Serialize());
        copy.Nodes.RemoveAll(node => !selectedIds.Contains(node.Id) || node.Kind == ScriptNodeKind.Start);
        if (copy.Nodes.Count == 0) return;
        DetachExternalConnections(copy.Nodes, copy.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal));
        PasteGraphBlocks(copy.Serialize(), offset: true);
    }

    private static void DetachExternalConnections(IEnumerable<ScriptNode> nodes, HashSet<string> included)
    {
        foreach (var node in nodes)
        {
            node.Next = Keep(node.Next); node.True = Keep(node.True); node.False = Keep(node.False);
            foreach (var option in node.Options) option.Target = Keep(option.Target);
        }
        string? Keep(string? target) => target != null && included.Contains(target) ? target : null;
    }

    private void PasteGraphBlocks(string? json, bool offset = false)
    {
        if (string.IsNullOrEmpty(json) || Document?.Graph == null) return;
        if (json.Length > 4_000_000) throw new InvalidDataException("Слишком большой набор блоков в буфере обмена.");
        ApplyProperties();
        var graph = Document.Graph;
        var pasted = VisualScript.Parse(json);
        pasted.Nodes.RemoveAll(node => node.Kind == ScriptNodeKind.Start);
        if (pasted.Nodes.Count == 0) return;
        if (graph.Nodes.Count + pasted.Nodes.Count > 500) throw new InvalidOperationException("Допускается не более 500 блоков.");
        var identities = pasted.Nodes.ToDictionary(node => node.Id, _ => Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        var characterIds = graph.Nodes.Where(node => node.Character != null).Select(node => node.Character!.Id).ToHashSet(StringComparer.Ordinal);
        var characterRemap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in pasted.Nodes.Where(node => node.Kind == ScriptNodeKind.RegisterCharacter && node.Character != null))
        {
            string original = node.Character!.Id, next = original;
            for (int suffix = 2; !characterIds.Add(next); suffix++) next = original[..Math.Min(original.Length, 70)] + "_" + suffix;
            characterRemap[original] = next;
            node.Character = node.Character with { Id = next };
        }
        double minX = pasted.Nodes.Min(node => node.X), minY = pasted.Nodes.Min(node => node.Y);
        double maxX = pasted.Nodes.Max(node => node.X + node.Width), maxY = pasted.Nodes.Max(node => node.Y + NodeHeight(node));
        double dx = offset ? 32 : (GraphScroll.ActualWidth / 2 - CanvasPan.X) / ZoomSlider.Value - (minX + maxX) / 2;
        double dy = offset ? 32 : (GraphScroll.ActualHeight / 2 - CanvasPan.Y) / ZoomSlider.Value - (minY + maxY) / 2;
        foreach (var node in pasted.Nodes)
        {
            node.Next = Remap(node.Next); node.True = Remap(node.True); node.False = Remap(node.False);
            foreach (var option in node.Options) option.Target = Remap(option.Target);
            node.Id = identities[node.Id];
            node.X = Math.Clamp(node.X + dx, -1_000_000_000, 1_000_000_000);
            node.Y = Math.Clamp(node.Y + dy, -1_000_000_000, 1_000_000_000);
            if (characterRemap.TryGetValue(node.SpeakerId, out string? speaker)) node.SpeakerId = speaker;
            if (node.Kind == ScriptNodeKind.UpdateCharacter && node.Character is { } update && characterRemap.TryGetValue(update.Id, out string? updatedId))
                node.Character = update with { Id = updatedId };
        }
        // Validate the complete candidate before changing the live graph or undo history.
        var candidate = VisualScript.Parse(graph.Serialize());
        candidate.Nodes.AddRange(pasted.Nodes);
        _ = candidate.Serialize();
        Remember();
        Document.Graph = candidate;
        selectedIds.Clear(); selectedIds.UnionWith(pasted.Nodes.Select(node => node.Id));
        selectedId = pasted.Nodes[0].Id;
        Repaint();
        EditorStatus.Text = $"Добавлено блоков: {pasted.Nodes.Count}. Ctrl+Z — отменить, Ctrl+X — повторить.";
        string? Remap(string? target) => target != null && identities.TryGetValue(target, out string? mapped) ? mapped : null;
    }

    internal bool HandleGraphShortcut(Key key, ModifierKeys modifiers, DependencyObject? source)
    {
        if (Document?.IsVisual != true || GraphMode.IsChecked != true || IsTextEditingSource(source)) return false;
        if (key == Key.Delete && modifiers == ModifierKeys.None)
        {
            Delete_Click(this, new RoutedEventArgs());
            return true;
        }
        if (!modifiers.HasFlag(ModifierKeys.Control)) return false;
        switch (key)
        {
            case Key.C: CopyGraphSelection(); break;
            case Key.V: PasteGraphSelection(); break;
            case Key.D: DuplicateGraphSelection(); break;
            case Key.Z: Undo_Click(this, new RoutedEventArgs()); break;
            case Key.X:
            case Key.Y: Redo_Click(this, new RoutedEventArgs()); break;
            default: return false;
        }
        return true;
    }

    private static bool IsTextEditingSource(DependencyObject? source)
    {
        for (var current = source; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is TextBoxBase or PasswordBox or KrlCodeEditor) return true;
        return false;
    }

    internal void SelectGraphArea(Rect area, bool additive = false)
    {
        var ids = Document?.Graph?.Nodes.Where(node => area.IntersectsWith(new Rect(node.X, node.Y, node.Width, NodeHeight(node))))
            .Select(node => node.Id).ToArray() ?? [];
        SelectGraphBlocks(additive ? selectedIds.Concat(ids).ToArray() : ids);
    }

    private void Selection_Start(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        for (var current = source; current != null && current != GraphScroll; current = VisualTreeHelper.GetParent(current))
            if (current is Border border && border.Tag is string || current is ButtonBase or Thumb) return;
        ApplyProperties(repaint: false);
        GraphScroll.Focus();
        selectionOrigin = e.GetPosition(GraphCanvas);
        selectionBefore = new HashSet<string>(selectedIds, StringComparer.Ordinal);
        selectionAdditive = (Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0;
        selectionRectangle = new Rectangle { Stroke = Brushes.CornflowerBlue, Fill = new SolidColorBrush(Color.FromArgb(32, 70, 125, 230)), StrokeThickness = 1, IsHitTestVisible = false };
        Panel.SetZIndex(selectionRectangle, 10000); GraphCanvas.Children.Add(selectionRectangle);
        GraphScroll.CaptureMouse(); e.Handled = true;
        Selection_Move(sender, e);
    }

    private void Selection_Move(object sender, MouseEventArgs e)
    {
        if (selectionOrigin is not { } origin || selectionRectangle == null) return;
        var area = new Rect(origin, e.GetPosition(GraphCanvas));
        Canvas.SetLeft(selectionRectangle, area.X); Canvas.SetTop(selectionRectangle, area.Y);
        selectionRectangle.Width = area.Width; selectionRectangle.Height = area.Height;
        var ids = Document?.Graph?.Nodes.Where(node => area.IntersectsWith(new Rect(node.X, node.Y, node.Width, NodeHeight(node)))).Select(node => node.Id) ?? [];
        selectedIds.Clear(); if (selectionAdditive) selectedIds.UnionWith(selectionBefore); selectedIds.UnionWith(ids);
        selectedId = selectedIds.FirstOrDefault(); RefreshCardSelection();
        e.Handled = true;
    }

    private void Selection_End(object sender, MouseButtonEventArgs e)
    {
        if (selectionOrigin == null) return;
        Selection_Move(sender, e); FinishMarquee(); e.Handled = true;
    }

    private void FinishMarquee()
    {
        if (selectionOrigin == null) return;
        selectionOrigin = null;
        if (selectionRectangle != null) GraphCanvas.Children.Remove(selectionRectangle);
        selectionRectangle = null;
        if (Mouse.Captured == GraphScroll) GraphScroll.ReleaseMouseCapture();
        UpdateProperties(); PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HookCardSelectionAndMovement(Border card, FrameworkElement header, ScriptNode node)
    {
        card.MouseLeftButtonDown += (_, args) => Guard(() =>
        {
            if (args.Handled) return;
            SelectFromPointer(node.Id); GraphScroll.Focus(); args.Handled = true;
        });
        Point? origin = null;
        string? before = null;
        Dictionary<string, Point> positions = [];
        header.MouseLeftButtonDown += (_, args) => Guard(() =>
        {
            if (args.Handled || args.OriginalSource is ButtonBase or Thumb) return;
            SelectFromPointer(node.Id); GraphScroll.Focus();
            ApplyProperties(repaint: false);
            origin = args.GetPosition(GraphScroll);
            before = Document!.Graph!.Serialize();
            positions = Document.Graph.Nodes.Where(item => selectedIds.Contains(item.Id)).ToDictionary(item => item.Id, item => new Point(item.X, item.Y));
            header.CaptureMouse(); args.Handled = true;
        });
        header.MouseMove += (_, args) =>
        {
            if (origin is not { } start || args.LeftButton != MouseButtonState.Pressed || Document?.Graph == null) return;
            var delta = (args.GetPosition(GraphScroll) - start) / ZoomSlider.Value;
            MoveGraphNodes(positions, delta);
            args.Handled = true;
        };
        header.MouseLeftButtonUp += (_, args) => { if (origin != null) { header.ReleaseMouseCapture(); args.Handled = true; } };
        header.LostMouseCapture += (_, _) =>
        {
            if (origin == null) return;
            origin = null;
            if (before != null) CommitGraphGesture(before);
            UpdateTitle();
        };
    }

    private void SelectFromPointer(string id)
    {
        bool toggle = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (toggle && selectedIds.Contains(id)) SelectGraphBlocks(selectedIds.Where(item => item != id).ToArray());
        else if (toggle || additive) SelectGraphBlocks(selectedIds.Append(id).ToArray());
        else if (!selectedIds.Contains(id)) SelectGraphBlocks([id]);
        else if (selectedId != id) { ApplyProperties(repaint: false); selectedId = id; UpdateProperties(); }
    }

    private void MoveGraphNodes(IReadOnlyDictionary<string, Point> positions, Vector delta)
    {
        if (Document?.Graph == null) return;
        foreach (var item in Document.Graph.Nodes.Where(item => positions.ContainsKey(item.Id)))
        {
            item.X = Math.Clamp(positions[item.Id].X + delta.X, -1_000_000_000, 1_000_000_000);
            item.Y = Math.Clamp(positions[item.Id].Y + delta.Y, -1_000_000_000, 1_000_000_000);
            if (cards.TryGetValue(item.Id, out var card)) { Canvas.SetLeft(card, item.X); Canvas.SetTop(card, item.Y); }
        }
        UpdateGraphExtent(); RenderLinks();
    }

    internal void MoveSelectedBlocks(Vector delta)
    {
        if (!double.IsFinite(delta.X) || !double.IsFinite(delta.Y)) throw new ArgumentOutOfRangeException(nameof(delta));
        ApplyProperties(repaint: false);
        if (Document?.Graph is not { } graph) return;
        string before = graph.Serialize();
        MoveGraphNodes(graph.Nodes.Where(item => selectedIds.Contains(item.Id)).ToDictionary(item => item.Id, item => new Point(item.X, item.Y)), delta);
        CommitGraphGesture(before); UpdateTitle();
    }
}
