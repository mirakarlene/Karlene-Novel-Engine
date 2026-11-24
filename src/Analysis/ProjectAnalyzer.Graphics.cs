using System.IO;

namespace KNE;

public static partial class ProjectAnalyzer
{
    private static void AnalyzeGraphics(string root, List<ProjectDiagnostic> findings)
    {
        const string path = "engine/performance.json";
        try
        {
            var settings = PerformanceSettingsStore.LoadProject(root);
            if (settings.RenderProxy == "veldrid" || settings.Graphics.MsaaSamples == 0) return;
            string label = ProjectPerformanceSettings.RenderProxies.Single(option => option.Value == settings.RenderProxy).Label;
            string message = settings.RenderProxy == "skia" && settings.SkiaBackend == "cpu"
                ? $"{label} в режиме CPU не поддерживает MSAA. Для подготовки графики будет использован Veldrid."
                : $"{label} требует поддержки MSAA ×{settings.Graphics.MsaaSamples} в OpenGL. Если драйвер её не предоставляет, KNE перейдёт на Veldrid.";
            findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE306", path, null, message));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            findings.Add(new(ProjectDiagnosticSeverity.Error, "KNE009", path, null, error.Message));
        }
    }

    private static void AnalyzeShaderReachability(VisualScript graph, string path, List<ProjectDiagnostic> findings)
    {
        var nodes = graph.Nodes.Where(ShaderLibrary.IsData).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var pending = new Stack<ScriptNode>(nodes.Values.Where(node => node.Kind == ScriptNodeKind.ShaderOutput));
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var node))
        {
            if (!reachable.Add(node.Id) || node.Shader == null) continue;
            foreach (var link in node.Shader.Inputs.Values)
                if (nodes.TryGetValue(link.Node, out var input)) pending.Push(input);
        }
        foreach (var node in nodes.Values.Where(node => !reachable.Contains(node.Id)))
            findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE304", path, null,
                "Шейдерный блок не связан с выходом Fragment Output.", node.Id));
    }
}
