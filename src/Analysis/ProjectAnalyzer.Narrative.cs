using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KNE;

public static partial class ProjectAnalyzer
{
    private static readonly Regex DialogueWord = new(@"[\p{L}\p{N}\p{M}]+(?:['’\-][\p{L}\p{N}\p{M}]+)*", RegexOptions.NonBacktracking);

    private static int CountLines(string text)
    {
        using var reader = new StringReader(text);
        int count = 0;
        while (reader.ReadLine() != null) count++;
        return count;
    }

    private sealed class NarrativeAnalysis(List<ProjectDiagnostic> findings)
    {
        private readonly Dictionary<string, string> profiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> speakerNames = new(StringComparer.Ordinal);
        internal HashSet<string> InlineVariables { get; } = new(StringComparer.Ordinal);
        internal int Dialogues { get; private set; }
        internal int Words { get; private set; }
        internal int CharacterCount => profiles.Count + speakerNames.Except(profiles.Values, StringComparer.Ordinal).Count();

        internal void ReadBlock(string path, ScriptNode node)
        {
            if (node.Kind is ScriptNodeKind.RegisterCharacter or ScriptNodeKind.UpdateCharacter && node.Character is { } profile)
                AddProfile(profile.Id, profile.Name);
            if (node.Kind is ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue or ScriptNodeKind.ReverseDialogue)
            {
                if (!node.Narration) AddSpeaker(node.Speaker, node.SpeakerId);
                ReadDialogue(node.Text, path, null, node.Id);
            }
        }

        internal void ReadSource(Source source, bool generatedCode)
        {
            var tokens = source.Tokens;
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Text is not ("d" or "kne_say" or "register_character" or "update_character") || !IsGlobalCall(tokens, i)) continue;
                var arguments = LiteralArguments(tokens, i + 1);
                if (tokens[i].Text is "register_character" or "update_character")
                {
                    if (arguments.ElementAtOrDefault(0) is not { } json) continue;
                    try
                    {
                        var profile = JsonSerializer.Deserialize<CharacterProfile>(json, EngineSettings.JsonOptions);
                        if (profile != null) AddProfile(profile.Id, profile.Name);
                    }
                    catch (JsonException) { }
                    continue;
                }
                AddSpeaker(arguments.ElementAtOrDefault(0), arguments.ElementAtOrDefault(2));
                ReadDialogue(arguments.ElementAtOrDefault(1), source.Path, generatedCode ? null : tokens[i].Line, null);
            }
        }

        private void AddProfile(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (!string.IsNullOrWhiteSpace(name) || !profiles.ContainsKey(id)) profiles[id] = name;
        }

        private void AddSpeaker(string? name, string? id)
        {
            if (!string.IsNullOrWhiteSpace(id)) AddProfile(id, name ?? "");
            else if (!string.IsNullOrWhiteSpace(name)) speakerNames.Add(name);
        }

        private void ReadDialogue(string? text, string path, int? line, string? block)
        {
            Dialogues++;
            if (text == null) return; // Computed text is only known at runtime.
            string plain;
            try
            {
                plain = InlineText.Parse(text, name =>
                {
                    InlineVariables.Add(name);
                    return "_";
                }).Text;
            }
            catch (InvalidDataException) { plain = text; }
            Words += DialogueWord.Count(plain);
            if (string.IsNullOrWhiteSpace(plain))
                findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE305", path, line, "Пустая реплика.", block));
        }
    }

    private static List<string?> LiteralArguments(IReadOnlyList<Lexeme> tokens, int opening)
    {
        var arguments = new List<string?>();
        if (tokens[opening].Token.Partner is not int end) return arguments;
        int start = opening + 1;
        for (int i = start; i < tokens.Count && tokens[i].Token.Start <= end; i++)
        {
            if (tokens[i].Token.Start == end || tokens[i].Text == ",")
            {
                if (start < i) arguments.Add(i == start + 1 ? Literal(tokens[start]) : null);
                start = i + 1;
            }
            else if (tokens[i].Text is "(" or "[" or "{" && tokens[i].Token.Partner is int nested && nested < end)
                while (i + 1 < tokens.Count && tokens[i + 1].Token.Start <= nested) i++;
        }
        return arguments;
    }
}
