using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace KNE;

public sealed record ScriptImportWarning(int Line, string Message);
public sealed record ScriptImportResult(string Original, string Code, IReadOnlyList<ScriptImportWarning> Warnings);

/// <summary>Conservative, non-executing adaptation of a subset of Ren'Py and Word text.</summary>
public static class ScriptImportConverter
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Label = new("^label ([A-Za-z_][A-Za-z0-9_]*):$", RegexOptions.CultureInvariant);
    private static readonly Regex Dialogue = new("^(?:([A-Za-z_][A-Za-z0-9_]*) +)?(\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StatementWords = new(["scene", "show", "hide", "play", "queue", "stop", "voice", "jump", "call", "label", "menu", "python", "init", "define", "default", "if", "elif", "else", "while", "for", "with", "pause", "return", "window", "transform", "image", "screen", "style", "translate"], StringComparer.Ordinal);

    public static Task<ScriptImportResult> FromPathAsync(string path, string functionPrefix = "imported", CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        PathSafety.RejectLinks(path);
        var info = new FileInfo(path);
        if (info.Length > MaximumBytes) throw new InvalidDataException("Импортируемый файл превышает 8 МБ.");
        ScriptImportResult result;
        if (string.Equals(info.Extension, ".rpy", StringComparison.OrdinalIgnoreCase))
            result = FromRenPy(File.ReadAllText(path, new UTF8Encoding(false, true)), functionPrefix);
        else if (string.Equals(info.Extension, ".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            result = FromDocx(stream, functionPrefix);
        }
        else throw new ArgumentException("Поддерживается импорт .rpy и .docx.");
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }, cancellationToken);

    public static ScriptImportResult FromRenPy(string source, string functionPrefix = "imported")
    {
        Validate(source, functionPrefix);
        var parser = new RenPyParser(source, functionPrefix);
        return parser.Convert();
    }

    public static ScriptImportResult FromDocx(Stream stream, string functionPrefix = "imported")
    {
        Validate("", functionPrefix);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 2048 || archive.Entries.Sum(e => e.Length) > MaximumBytes * 4L)
            throw new InvalidDataException("Распакованный документ слишком велик.");
        var documents = archive.Entries.Where(e => e.FullName == "word/document.xml").ToArray();
        if (documents.Length != 1 || documents[0].Length > MaximumBytes)
            throw new InvalidDataException("В DOCX отсутствует единственный допустимый word/document.xml.");
        using var input = documents[0].Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes });
        var document = XDocument.Load(reader);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        if (document.Root?.Name != w + "document" || document.Root.Element(w + "body") is not XElement body)
            throw new InvalidDataException("Не найден текст документа WordprocessingML.");
        var original = new StringBuilder();
        var code = Header("DOCX", functionPrefix + "_document");
        code.AppendLine("function " + functionPrefix + "_document()");
        foreach (var paragraph in body.Descendants(w + "p"))
        {
            string text = string.Concat(paragraph.Descendants().Where(e => !e.Ancestors(w + "del").Any()).Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : e.Name == w + "br" || e.Name == w + "cr" ? "\n" : ""));
            original.AppendLine(text);
            if (!string.IsNullOrWhiteSpace(text)) code.AppendLine("    d(nil, " + Quote(text) + ")");
        }
        code.AppendLine("end");
        return new(original.ToString(), code.ToString(), [new(0, "DOCX: импортирован только текст основного документа. Форматирование, изображения, колонтитулы, сноски и связи не переносятся; распределите реплики и персонажей вручную.")]);
    }

    private static void Validate(string source, string prefix)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (Encoding.UTF8.GetByteCount(source) > MaximumBytes) throw new InvalidDataException("Текст превышает 8 МБ.");
        if (prefix.Length > 64 || !Identifier.IsMatch(prefix)) throw new ArgumentException("Префикс должен быть идентификатором Lua.", nameof(prefix));
    }

    private static StringBuilder Header(string format, string entry) => new StringBuilder()
        .AppendLine("-- ЭКСПЕРИМЕНТАЛЬНЫЙ импорт " + format + ": проверьте адаптацию перед запуском.")
        .AppendLine("-- Вызов из main(): call(" + entry + ")");

    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char c in value) result.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => "\\" + ((int)c).ToString("D3", System.Globalization.CultureInfo.InvariantCulture), _ => c.ToString() });
        return result.Append('"').ToString();
    }

    private static string Decode(string literal)
    {
        var result = new StringBuilder();
        for (int i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] != '\\') { result.Append(literal[i]); continue; }
            char next = literal[++i];
            result.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"', '\'' => '\'', _ => '\\' });
            if (next is not ('n' or 'r' or 't' or '\\' or '"' or '\'')) result.Append(next);
        }
        return result.ToString();
    }

    private sealed class RenPyParser
    {
        private readonly string original, prefix;
        private readonly string[] lines;
        private readonly List<ScriptImportWarning> warnings = [];
        private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
        private readonly StringBuilder output = new();
        private int choiceNumber;
        public RenPyParser(string source, string functionPrefix) { original = source; prefix = functionPrefix; lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'); }

        public ScriptImportResult Convert()
        {
            var labels = new List<(int Line, string Name)>();
            for (int i = 0; i < lines.Length; i++)
            {
                var match = Label.Match(lines[i]);
                if (!match.Success) continue;
                string name = match.Groups[1].Value;
                if (!names.TryAdd(name, prefix + "_" + name)) throw new InvalidDataException($"Строка {i + 1}: метка {name} объявлена повторно.");
                labels.Add((i, name));
            }
            if (labels.Count == 0) throw new InvalidDataException("Не найдено ни одной поддерживаемой метки label name:.");
            output.Append(Header("Ren'Py", names[labels.Any(l => l.Name == "start") ? "start" : labels[0].Name]));
            warnings.Add(new(0, "Переменные, Python, экраны, определения персонажей и ресурсов, ATL, условия и интерполяция не переносятся. Имена персонажей сохранены как текст. Пути к ресурсам проверьте вручную."));
            for (int i = 0; i < labels[0].Line; i++) Unsupported(i, 0);
            for (int labelIndex = 0; labelIndex < labels.Count; labelIndex++)
            {
                var label = labels[labelIndex];
                int end = labelIndex + 1 < labels.Count ? labels[labelIndex + 1].Line : lines.Length;
                output.AppendLine("function " + names[label.Name] + "()");
                int current = label.Line + 1;
                Statements(ref current, end, FindIndent(current, end), 1);
                if (labelIndex + 1 < labels.Count) output.AppendLine("    goto(" + names[labels[labelIndex + 1].Name] + ") -- переход к следующей метке Ren'Py");
                output.AppendLine("end").AppendLine();
            }
            return new(original, output.ToString(), warnings);
        }

        private int FindIndent(int start, int end)
        {
            for (int i = start; i < end; i++) if (!string.IsNullOrWhiteSpace(lines[i]) && !lines[i].TrimStart().StartsWith('#')) return Indent(lines[i]);
            return 4;
        }

        private static int Indent(string line) => line.TakeWhile(char.IsWhiteSpace).Sum(c => c == '\t' ? 4 : 1);
        private void Emit(int depth, string text) => output.Append(' ', depth * 4).AppendLine(text);

        private void Statements(ref int current, int end, int indent, int depth)
        {
            while (current < end)
            {
                string text = lines[current].Trim();
                if (text.Length == 0 || text.StartsWith('#')) { if (text.StartsWith('#')) Emit(depth, "--" + text[1..]); current++; continue; }
                int actualIndent = Indent(lines[current]);
                if (actualIndent < indent) return;
                int line = current++;
                if (actualIndent != indent) { Unsupported(line, depth); continue; }
                if (text == "menu:") { Menu(ref current, end, indent, depth); continue; }
                if (text == "return") { Emit(depth, "do return end"); continue; }
                if (text == "pass") { Emit(depth, "-- pass"); continue; }
                var dialogue = Dialogue.Match(text);
                if (dialogue.Success && !StatementWords.Contains(dialogue.Groups[1].Value))
                {
                    string value = Decode(dialogue.Groups[2].Value);
                    if (value.Contains('[') || value.Contains('{')) warnings.Add(new(line + 1, "Теги текста и интерполяция Ren'Py сохранены буквально."));
                    Emit(depth, "d(" + (dialogue.Groups[1].Success ? Quote(dialogue.Groups[1].Value) : "nil") + ", " + Quote(value) + ")"); continue;
                }
                var transfer = Regex.Match(text, "^(jump|call) ([A-Za-z_][A-Za-z0-9_]*)$");
                if (transfer.Success && names.TryGetValue(transfer.Groups[2].Value, out string? target))
                { Emit(depth, (transfer.Groups[1].Value == "jump" ? "goto(" : "call(") + target + ")"); continue; }
                var resource = Regex.Match(text, "^(scene|show|play music) (\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')$");
                if (resource.Success)
                {
                    string path = Quote(Decode(resource.Groups[2].Value));
                    Emit(depth, resource.Groups[1].Value switch { "scene" => "background(" + path + ")", "show" => "show_character(" + path + ", \"center\")", _ => "play_music(" + path + ", true)" }); continue;
                }
                if (text == "stop music") { Emit(depth, "stop_music()"); continue; }
                Unsupported(line, depth);
                if (text.EndsWith(':'))
                {
                    // Never promote a nested unsupported conditional/Python body into unconditional live code.
                    while (current < end && (string.IsNullOrWhiteSpace(lines[current]) || Indent(lines[current]) > indent)) Unsupported(current++, depth);
                }
            }
        }

        private void Menu(ref int current, int end, int parentIndent, int depth)
        {
            int menuIndent = FindIndent(current, end);
            if (menuIndent <= parentIndent) { warnings.Add(new(current + 1, "Пустое menu: пропущено.")); return; }
            var options = new List<(string Text, int Start, int End, int Line)>();
            string caption = "Выбор";
            while (current < end)
            {
                string text = lines[current].Trim();
                if (text.Length == 0 || text.StartsWith('#')) { current++; continue; }
                if (Indent(lines[current]) <= parentIndent) break;
                var option = Dialogue.Match(text.EndsWith(':') ? text[..^1].TrimEnd() : "");
                if (Indent(lines[current]) == menuIndent && text.EndsWith(':') && option.Success && !option.Groups[1].Success)
                {
                    int line = current++, start = current;
                    while (current < end && (string.IsNullOrWhiteSpace(lines[current]) || Indent(lines[current]) > menuIndent)) current++;
                    options.Add((Decode(option.Groups[2].Value), start, current, line)); continue;
                }
                var title = Dialogue.Match(text);
                if (Indent(lines[current]) == menuIndent && title.Success && !title.Groups[1].Success) caption = Decode(title.Groups[2].Value);
                else Unsupported(current, depth);
                current++;
            }
            if (options.Count == 0) { warnings.Add(new(current + 1, "menu: без поддерживаемых вариантов пропущено.")); return; }
            string variable = "__import_choice_" + ++choiceNumber;
            Emit(depth, "local " + variable + " = choice(" + Quote(caption) + ", {" + string.Join(", ", options.Select(o => Quote(o.Text))) + "})");
            for (int i = 0; i < options.Count; i++)
            {
                Emit(depth, (i == 0 ? "if " : "elseif ") + variable + " == " + (i + 1) + " then");
                int start = options[i].Start;
                Statements(ref start, options[i].End, FindIndent(start, options[i].End), depth + 1);
            }
            Emit(depth, "end");
        }

        private void Unsupported(int line, int depth)
        {
            if (string.IsNullOrWhiteSpace(lines[line])) return;
            Emit(depth, "-- [Ren'Py строка " + (line + 1) + "] " + lines[line].Trim());
            if (!lines[line].TrimStart().StartsWith('#')) warnings.Add(new(line + 1, "Не перенесено: " + lines[line].Trim()));
        }
    }
}
