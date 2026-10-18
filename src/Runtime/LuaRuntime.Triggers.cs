using System.IO;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    /// <summary>Returns a private subsystem function without replacing the graph's public scene function.</summary>
    public string[] SubsystemCodeForLua(string relative)
    {
        try
        {
            string path = PathSafety.ResolveRelative(project.Path, relative);
            if (!ScriptDocument.IsVisualPath(path)) throw new InvalidDataException("Подсистема должна быть .knegraph.");
            var source = scripts!.FindFile(Path.GetRelativePath(project.Path, path));
            var graph = VisualScript.Parse(source.Source);
            string code = graph.Compile(editorPreview: editorSource?.StepCommands == true, subsystem: true);
            string declaration = "function " + graph.FunctionName + "()";
            int index = code.IndexOf(declaration, StringComparison.Ordinal);
            return [code.Remove(index, declaration.Length).Insert(index, "return function()"), ""];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        { return ["", error.Message]; }
    }

    public string ValidateTriggerForLua(string json)
    {
        try { ReadTrigger(json).Validate(); return ""; }
        catch (Exception error) when (error is JsonException or ArgumentException) { return error.Message; }
    }
    public bool TriggerMatchesForLua(string specification, string signal)
    {
        var input = JsonSerializer.Deserialize<TriggerSignal>(signal, EngineSettings.JsonOptions)!;
        return ReadTrigger(specification).Matches(input);
    }
    private static TriggerSettings ReadTrigger(string json) => JsonSerializer.Deserialize<TriggerSettings>(json, EngineSettings.JsonOptions)
        ?? throw new ArgumentException("Пустая конфигурация триггера.");
    public bool TriggerOnceForLua(string specification) => ReadTrigger(specification).Once;

    /// <summary>Delivers a UI event without consuming the currently displayed dialogue.</summary>
    public Task TriggerAsync(TriggerSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal); signal.Validate();
        return RunAsync(() =>
        {
            if (!initialized || Story.Completed || !Story.Triggers.Any(trigger => trigger.Matches(signal))) return;
            InstallHook();
            string before = FrameSignature(Story);
            string json = JsonSerializer.Serialize(signal, EngineSettings.JsonOptions);
            ResumeStory(null, signal: json);
            if (!replaying) history.Add(new StoryAction("trigger", before, Command: json));
        }, cancellationToken, "Событие триггера");
    }

    private static IReadOnlyList<TriggerSettings> ReadTriggers(LuaTable value)
    {
        using var entries = value["triggers"] as LuaTable;
        var result = new List<TriggerSettings>();
        if (entries != null)
            for (int i = 1; i <= 512 && entries[i] is string json; i++) result.Add(ReadTrigger(json));
        return result;
    }
}
