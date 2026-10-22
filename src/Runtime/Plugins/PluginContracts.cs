using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace KNE;

/// <summary>A versioned project extension, discovered from plugins/*.kneplugin.json.</summary>
public sealed record PluginManifest(string Id, string Source, int ApiVersion = 1);

/// <summary>A configurable value in a plugin block.</summary>
public sealed record PluginParameter(string Id, string Label, string Kind = "text", string Default = "",
    double Minimum = 0, double Maximum = 100, string[]? Choices = null);

/// <summary>Persistent definition of a custom execution block. Ports are ordered and named.</summary>
public sealed record PluginBlock(string Id, string Title, string Group, string Description = "", string Icon = "◇",
    string Color = "#7C5CFF", string[]? Inputs = null, string[]? Outputs = null, PluginParameter[]? Parameters = null);

/// <summary>Saved alongside the graph so missing extensions never destroy its parameters or connections.</summary>
public sealed class PluginNodeSettings
{
    public string BlockId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "◇";
    public string Color { get; set; } = "#7C5CFF";
    public string[] Inputs { get; set; } = ["Вход"];
    public JsonObject Parameters { get; set; } = new();
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BlockId)) throw new ArgumentException("Укажите ID блока плагина.");
        PluginApi.Id(BlockId);
        if (Title == null || Title.Length > 200 || Icon == null || Icon.Length > 32 || Color == null || !System.Text.RegularExpressions.Regex.IsMatch(Color, "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) throw new ArgumentException("Некорректное оформление блока плагина.");
        if (Inputs == null || Inputs.Length > 20 || Inputs.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 80) || Inputs.Distinct().Count() != Inputs.Length) throw new ArgumentException("Некорректные входные порты блока плагина.");
        if (Parameters == null || Parameters.Count > 32 || Parameters.ToJsonString().Length > 1024 * 1024) throw new ArgumentException("Некорректные параметры блока плагина.");
    }
}

/// <summary>Mutable execution interception. Dialogue hooks may replace Text and Speaker.</summary>
public sealed class PluginExecution(IPluginContext context, string kind, string nodeId = "", string text = "", string speaker = "")
{
    public IPluginContext Context { get; } = context;
    public string Kind { get; } = kind;
    public string NodeId { get; } = nodeId;
    public string Text { get; set; } = text;
    public string Speaker { get; set; } = speaker;
}

/// <summary>One runtime notification, delivered synchronously on the serialized Lua thread.</summary>
public sealed record PluginEvent(string Name, JsonElement Data, IPluginContext Context);

/// <summary>Input is offered to plugins before the engine. Mark Handled to consume it.</summary>
public sealed class PluginInput(string kind, string key, double x = 0, double y = 0)
{
    public string Kind { get; } = kind;
    public string Key { get; } = key;
    public double X { get; } = x;
    public double Y { get; } = y;
    public bool Handled { get; set; }
}

/// <summary>Colors available through DynamicResource, including optional block category overrides.</summary>
public sealed record PluginPalette(string Id, string Title, string Background, string Surface, string Text,
    string Accent, string Muted, Dictionary<string, string>? Groups = null);

/// <summary>A plugin-provided editor surface, constructed on the editor's dispatcher.</summary>
public sealed record PluginEditorTab(string Id, string Title, Func<FrameworkElement> Create);

/// <summary>A toolbar or context command; actions run on the editor dispatcher.</summary>
public sealed record PluginEditorCommand(string Id, string Title, Action<PluginEditorContext> Execute, string Location = "toolbar");

/// <summary>Access to the active editable graph, with undo-aware atomic replacement.</summary>
public sealed class PluginEditorContext(string projectDirectory, Func<VisualScript?> readGraph, Action<VisualScript> replaceGraph)
{
    public string ProjectDirectory { get; } = projectDirectory;
    public VisualScript? GetGraph() => readGraph();
    public void ReplaceGraph(VisualScript graph) { ArgumentNullException.ThrowIfNull(graph); replaceGraph(graph); }
}

/// <summary>Custom screen drawn above the novel. State is a detached JSON snapshot.</summary>
public sealed record PluginGameScreen(string Id, Func<JsonElement, FrameworkElement> Create);

/// <summary>Optional live snapshot updates for a plugin screen, called on the game dispatcher.</summary>
public interface IPluginScreenView { void UpdateState(JsonElement variables); }

/// <summary>A plugin diagnostic included in the project analysis report.</summary>
public sealed record PluginDiagnostic(string Message, string? NodeId = null, bool IsError = false);

/// <summary>A translator-friendly dialogue record with a stable block ID.</summary>
public sealed record PluginDialogue(string Id, string Speaker, string Text);

/// <summary>A plugin-defined translation format, paired with import and export callbacks.</summary>
public sealed record PluginDialogueFormat(string Extension, Func<IReadOnlyList<PluginDialogue>, byte[]> Export,
    Func<byte[], IReadOnlyList<PluginDialogue>> Import);
