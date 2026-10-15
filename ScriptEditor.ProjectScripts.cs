namespace KNE;

public partial class ScriptEditor
{
    /// <summary>Captures the selected script block after applying its pending property edits.</summary>
    public (string NodeId, string Path)? CaptureProjectScript()
    {
        ApplyProperties(); return Selected?.Kind == ScriptNodeKind.Script ? (Selected.Id, Selected.Path) : null;
    }

    /// <summary>Assigns a project-relative source path, recording one undo action.</summary>
    public void SetProjectScriptPath(string nodeId, string relativePath)
    {
        if (Document?.Graph is not { } graph || graph.Nodes.FirstOrDefault(node => node.Id == nodeId) is not { Kind: ScriptNodeKind.Script } node)
            throw new InvalidOperationException("Блок скрипта больше не открыт.");
        if (node.Path == relativePath) return;
        Remember(); node.Path = relativePath; Repaint();
    }
}
