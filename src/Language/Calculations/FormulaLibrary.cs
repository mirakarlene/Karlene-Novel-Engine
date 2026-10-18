using System.IO;
using System.Text.Json;

namespace KNE;

public sealed record FormulaTemplate(string Label, CalculationSettings Settings);
/// <summary>Copies named formula recipes without sharing mutable steps or executing project scripts.</summary>
public static class FormulaLibrary
{
    public static string Encode(CalculationSettings settings)
    {
        _ = CalculationCompiler.Compile(ScriptNodeKind.Formula, settings, "result");
        return "KNE-FORMULA-1\n" + JsonSerializer.Serialize(settings, VisualScript.JsonOptions);
    }
    public static CalculationSettings Decode(string text)
    {
        const string prefix = "KNE-FORMULA-1\n";
        if (text == null || text.Length > 200000 || !text.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException("В буфере нет формулы KNE.");
        CalculationSettings settings;
        try
        {
            using var document = JsonDocument.Parse(text[prefix.Length..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("FormulaName", out var name) || name.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("Steps", out var steps) || steps.ValueKind != JsonValueKind.Array) throw new ArgumentException("В рецепте нужны название и шаги формулы.");
            settings = JsonSerializer.Deserialize<CalculationSettings>(text[prefix.Length..], VisualScript.JsonOptions) ?? throw new ArgumentException("Пустая формула.");
        }
        catch (JsonException error) { throw new ArgumentException("Повреждённая формула KNE.", error); }
        _ = CalculationCompiler.Compile(ScriptNodeKind.Formula, settings, "result"); return settings;
    }
    public static async Task<IReadOnlyList<FormulaTemplate>> LoadAsync(string root, CancellationToken token = default)
    {
        var project = await ProjectLoader.LoadProjectAsync(root, token); var result = new List<FormulaTemplate>();
        foreach (var file in project.Files.Where(file => ScriptDocument.IsVisualPath(file.FilePath)))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var doc = await ScriptDocument.OpenAsync(root, file.FilePath, token);
                foreach (var node in doc.Graph!.Nodes.Where(node => node.Kind == ScriptNodeKind.Formula && node.Calculation != null))
                    result.Add(new(node.Calculation!.FormulaName + " · " + Path.GetRelativePath(root, file.FilePath) + " · " + node.Id, Decode(Encode(node.Calculation))));
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException) { /* Unfinished unrelated graphs are not formula templates. */ }
        }
        return result;
    }
}
