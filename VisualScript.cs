using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KNE;

/// <summary>Versioned source document for a visual scene. Layout is independent of execution order.</summary>
public sealed class VisualScript
{
    /// <summary>The persisted schema version; this editor supports version 1.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The global function exported by this graph; main is the unique game entry point.</summary>
    public string FunctionName { get; set; } = "main";

    /// <summary>Blocks in arbitrary storage order. Connections determine execution order.</summary>
    public List<ScriptNode> Nodes { get; set; } = [];

    /// <summary>Readable JSON with named enums and rejection of unsupported fields.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Creates a connected, runnable scene suitable for a new document.</summary>
    /// <returns>A scene with start, dialogue, and end blocks.</returns>
    public static VisualScript Create() => new()
    {
        Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, X = 40, Y = 60, Next = "dialogue" },
            new()
            {
                Id = "dialogue", Kind = ScriptNodeKind.Dialogue, X = 330, Y = 60, Text = "Добро пожаловать!",
                Next = "end"
            },
            new() { Id = "end", Kind = ScriptNodeKind.End, X = 620, Y = 60 }
        ]
    };

    /// <summary>Reads a document, rejecting unknown versions and malformed structural data.</summary>
    /// <param name="json">The source document in JSON format.</param>
    /// <returns>A structurally valid graph, which may still be an unfinished draft.</returns>
    public static VisualScript Parse(string json)
    {
        try
        {
            var script = JsonSerializer.Deserialize<VisualScript>(json, JsonOptions)
                         ?? throw new InvalidDataException("Блок-схема не должна содержать null.");
            script.ValidateStructure();
            return script;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректная блок-схема: " + ex.Message, ex);
        }
    }

    /// <summary>Serializes an editable draft. Unconnected blocks are permitted until compilation.</summary>
    /// <returns>Versioned JSON containing commands, connections, and layout.</returns>
    public string Serialize()
    {
        ValidateStructure();
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    private void ValidateStructure()
    {
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия блок-схемы.");
        if (!LuaSource.IsIdentifier(FunctionName)) throw new InvalidDataException("Имя функции блок-схемы: латинские буквы, цифры и _, без цифры в начале и ключевых слов Lua.");
        if (Nodes == null || Nodes.Count > 500) throw new InvalidDataException("Допускается не более 500 блоков.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 80 || !ids.Add(node.Id))
                throw new InvalidDataException("У каждого блока должен быть уникальный непустой идентификатор.");
            if (!Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.ValueKind) || !Enum.IsDefined(node.Comparison))
                throw new InvalidDataException("Неизвестный тип блока, значения или сравнения.");
            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y) || node.X is < 0 or > 10000 ||
                node.Y is < 0 or > 10000)
                throw new InvalidDataException("Координаты блока должны быть от 0 до 10000.");
            if (node.Options == null || node.Options.Count > 20 || node.Options.Any(o => o == null || o.Text == null))
                throw new InvalidDataException("У выбора должно быть не более 20 вариантов.");
            if (node.Text == null || node.Speaker == null || node.Path == null || node.Variable == null ||
                node.Value == null || node.Position == null)
                throw new InvalidDataException("Свойства блока не должны содержать null.");
            if (new[] { node.Text, node.Speaker, node.Path, node.Value }.Any(value => value.Length > 16000))
                throw new InvalidDataException("Свойство блока слишком длинное (максимум 16000 символов).");
        }
    }

    /// <summary>Validates the graph and generates its named Lua function using the KNE API.</summary>
    /// <returns>Lua source for a complete, acyclic scene.</returns>
    public string Compile(bool editorPreview = false)
    {
        ValidateStructure();
        var starts = Nodes.Where(n => n.Kind == ScriptNodeKind.Start).ToArray();
        if (starts.Length != 1) throw new InvalidDataException("Нужен ровно один блок «Начало».");
        var map = Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (node.Kind == ScriptNodeKind.Choice &&
                (node.Options.Count == 0 || node.Options.Any(o => string.IsNullOrWhiteSpace(o.Text))))
                Fail(node, "укажите от 1 до 20 непустых вариантов выбора");
            if (node.Kind is ScriptNodeKind.Condition or ScriptNodeKind.SetVariable)
            {
                if (node.Variable.Length is < 1 or > 80 ||
                    !node.Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ||
                    char.IsAsciiDigit(node.Variable[0]))
                    Fail(node, "имя переменной: латинские буквы, цифры и _, без цифры в начале");
                _ = Literal(node);
                if (node.Kind == ScriptNodeKind.Condition &&
                    node.Comparison is not (ScriptComparison.Equal or ScriptComparison.NotEqual) &&
                    node.ValueKind != ScriptValueKind.Number)
                    Fail(node, "сравнения >, <, >=, <= требуют числового значения");
            }

            if (node.Kind is ScriptNodeKind.Character or ScriptNodeKind.Music or ScriptNodeKind.Sound &&
                string.IsNullOrWhiteSpace(node.Path))
                Fail(node, "укажите путь к ресурсу");
            if (node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene && string.IsNullOrWhiteSpace(node.Path))
                Fail(node, "укажите имя функции");
            if (node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene && !LuaSource.IsIdentifier(node.Path))
                Fail(node, "цель перехода должна быть именем функции, например chapter, без пути к файлу");
            if (node.Kind == ScriptNodeKind.GotoScene && node.Next != null)
                Fail(node, "у постоянного перехода нет продолжения в этой сцене");
            if (node.Kind == ScriptNodeKind.Character && node.Position is not ("left" or "center" or "right"))
                Fail(node, "позиция персонажа: left, center или right");
            foreach (var (_, target) in node.Outputs())
            {
                if (string.IsNullOrEmpty(target) || !map.ContainsKey(target))
                    Fail(node, "соедините все выходы с существующими блоками");
                if (map[target!].Kind == ScriptNodeKind.Start) Fail(node, "нельзя соединять выход с блоком «Начало»");
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        void Visit(ScriptNode node)
        {
            if (active.Contains(node.Id)) Fail(node, "циклы в первой версии не поддерживаются");
            if (!visited.Add(node.Id)) return;
            active.Add(node.Id);
            foreach (var (_, target) in node.Outputs()) Visit(map[target!]);
            active.Remove(node.Id);
        }

        Visit(starts[0]);
        if (visited.Count != Nodes.Count)
            throw new InvalidDataException("Есть блоки, недостижимые из «Начала». Соедините или удалите их.");

        var code = new StringBuilder("-- Сгенерировано из блок-схемы KNE.\nfunction ").Append(FunctionName).Append("()\n");
        code.Append("    local __kne_node = ").Append(Quote(starts[0].Id)).Append('\n');
        code.Append("    while __kne_node do\n");
        bool first = true;
        foreach (var node in Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            code.Append(first ? "        if " : "        elseif ").Append("__kne_node == ").Append(Quote(node.Id))
                .Append(" then\n");
            first = false;
            if (editorPreview) code.Append("            kne_editor_node(").Append(Quote(node.Id)).Append(")\n");
            string command = node.Kind switch
            {
                ScriptNodeKind.Dialogue =>
                    $"d({(node.Speaker.Length == 0 ? "nil" : Quote(node.Speaker))}, {Quote(node.Text)})",
                ScriptNodeKind.SetVariable => $"state[{Quote(node.Variable)}] = {Literal(node)}",
                ScriptNodeKind.Background => $"background({(node.Path.Length == 0 ? "nil" : Quote(node.Path))})",
                ScriptNodeKind.Character => $"show_character({Quote(node.Path)}, {Quote(node.Position)})",
                ScriptNodeKind.HideCharacter => "hide_character()",
                ScriptNodeKind.Music => $"play_music({Quote(node.Path)}, {(node.Loop ? "true" : "false")})",
                ScriptNodeKind.StopMusic => "stop_music()",
                ScriptNodeKind.Sound => $"play_sound({Quote(node.Path)})",
                ScriptNodeKind.GotoScene => $"goto_scene({Quote(node.Path)})",
                ScriptNodeKind.CallScene => $"call_scene({Quote(node.Path)})",
                _ => ""
            };
            if (command.Length > 0) code.Append("            ").Append(command).Append('\n');
            if (editorPreview && node.Kind is ScriptNodeKind.Start or ScriptNodeKind.End or ScriptNodeKind.SetVariable or ScriptNodeKind.Condition)
                code.Append("            kne_editor_pause()\n");
            if (node.Kind == ScriptNodeKind.Choice)
            {
                code.Append("            local answer = choice(").Append(Quote(node.Text)).Append(", {")
                    .Append(string.Join(", ", node.Options.Select(o => Quote(o.Text)))).Append("})\n");
                code.Append("            __kne_node = ({")
                    .Append(string.Join(", ", node.Options.Select(o => Quote(o.Target!)))).Append("})[answer]\n");
            }
            else if (node.Kind == ScriptNodeKind.Condition)
            {
                string op = node.Comparison switch
                {
                    ScriptComparison.Equal => "==", ScriptComparison.NotEqual => "~=", ScriptComparison.Greater => ">",
                    ScriptComparison.Less => "<", ScriptComparison.GreaterOrEqual => ">=", _ => "<="
                };
                code.Append("            if state[").Append(Quote(node.Variable)).Append("] ").Append(op).Append(' ')
                    .Append(Literal(node)).Append(" then\n")
                    .Append("                __kne_node = ").Append(Quote(node.True!)).Append("\n            else\n")
                    .Append("                __kne_node = ").Append(Quote(node.False!)).Append("\n            end\n");
            }
            else
                code.Append("            __kne_node = ")
                    .Append(node.Kind is ScriptNodeKind.End or ScriptNodeKind.GotoScene ? "nil" : Quote(node.Next!)).Append('\n');
        }

        code.Append("        else\n            error('Неизвестный блок')\n        end\n    end\nend\n");
        return code.ToString();
    }

    private static string Literal(ScriptNode node)
    {
        if (node.ValueKind == ScriptValueKind.String) return Quote(node.Value);
        if (node.ValueKind == ScriptValueKind.Boolean && bool.TryParse(node.Value, out bool boolean))
            return boolean ? "true" : "false";
        if (node.ValueKind == ScriptValueKind.Number &&
            double.TryParse(node.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) &&
            double.IsFinite(number))
            return number.ToString("R", CultureInfo.InvariantCulture);
        Fail(node, "значение должно быть конечным числом (точка — разделитель) или true/false для логического типа");
        return "";
    }

    // Escape bytes that Lua treats specially; JSON escaping is not Lua escaping (e.g. \uXXXX).
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c is '\\' or '"') result.Append('\\').Append(c);
            else if (char.IsControl(c))
                result.Append('\\').Append(((int)c).ToString("D3", CultureInfo.InvariantCulture));
            else result.Append(c);
        }

        return result.Append('"').ToString();
    }

    private static void Fail(ScriptNode node, string message) =>
        throw new InvalidDataException($"Блок «{node.Title}» ({node.Id}): {message}.");
}

/// <summary>Supported scene commands. Connections represent execution flow.</summary>
public enum ScriptNodeKind
{
    Start,
    Dialogue,
    Choice,
    Condition,
    SetVariable,
    Background,
    Character,
    HideCharacter,
    Music,
    StopMusic,
    Sound,
    End,
    GotoScene,
    CallScene
}

/// <summary>Literal types accepted by variable assignment and comparison blocks.</summary>
public enum ScriptValueKind
{
    String,
    Number,
    Boolean
}

/// <summary>Supported equality and numeric comparisons.</summary>
public enum ScriptComparison
{
    Equal,
    NotEqual,
    Greater,
    Less,
    GreaterOrEqual,
    LessOrEqual
}

/// <summary>An editable command and its explicitly ordered output ports.</summary>
public sealed class ScriptNode
{
    /// <summary>Stable document-local identifier used by connections and generated Lua.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The command represented by this block.</summary>
    public ScriptNodeKind Kind { get; set; }

    /// <summary>Horizontal canvas position in unscaled device-independent pixels.</summary>
    public double X { get; set; }

    /// <summary>Vertical canvas position in unscaled device-independent pixels.</summary>
    public double Y { get; set; }

    /// <summary>Dialogue speaker; an empty string means narration.</summary>
    public string Speaker { get; set; } = "";

    /// <summary>Dialogue text or the question displayed by a choice.</summary>
    public string Text { get; set; } = "";

    /// <summary>Resource path or target function name for a transition. Empty backgrounds clear the scene.</summary>
    public string Path { get; set; } = "";

    /// <summary>Character placement: left, center, or right.</summary>
    public string Position { get; set; } = "center";

    /// <summary>Whether a music block repeats its track.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Play the project's visual animations when this block is selected in the editor.</summary>
    public bool PreviewAnimation { get; set; }

    /// <summary>Flat key in the runtime state table.</summary>
    public string Variable { get; set; } = "score";

    /// <summary>Literal text, interpreted according to ValueKind.</summary>
    public string Value { get; set; } = "0";

    /// <summary>The type of an assignment or comparison literal.</summary>
    public ScriptValueKind ValueKind { get; set; } = ScriptValueKind.Number;

    /// <summary>Operator used by condition blocks.</summary>
    public ScriptComparison Comparison { get; set; }

    /// <summary>Destination of a command's single continuation port.</summary>
    public string? Next { get; set; }

    /// <summary>Destination when a condition is satisfied.</summary>
    public string? True { get; set; }

    /// <summary>Destination when a condition is not satisfied.</summary>
    public string? False { get; set; }

    /// <summary>Ordered answers and destinations for a choice block.</summary>
    public List<ScriptOption> Options { get; set; } = [];

    /// <summary>The user-facing Russian name of this command.</summary>
    [JsonIgnore]
    public string Title => Kind switch
    {
        ScriptNodeKind.Start => "Начало", ScriptNodeKind.Dialogue => "Реплика", ScriptNodeKind.Choice => "Выбор",
        ScriptNodeKind.Condition => "Условие", ScriptNodeKind.SetVariable => "Переменная",
        ScriptNodeKind.Background => "Фон",
        ScriptNodeKind.Character => "Персонаж", ScriptNodeKind.HideCharacter => "Скрыть персонажа",
        ScriptNodeKind.Music => "Музыка",
        ScriptNodeKind.StopMusic => "Остановить музыку", ScriptNodeKind.Sound => "Звук",
        ScriptNodeKind.GotoScene => "Перейти на сцену", ScriptNodeKind.CallScene => "Вызвать сцену", _ => "Конец"
    };

    /// <summary>Returns ordered ports, including unconnected ports in an unfinished draft.</summary>
    /// <returns>Labels and optional destination identifiers in display order.</returns>
    public IEnumerable<(string Label, string? Target)> Outputs()
    {
        if (Kind is ScriptNodeKind.End or ScriptNodeKind.GotoScene) yield break;
        if (Kind == ScriptNodeKind.Choice)
        {
            foreach (var option in Options) yield return (option.Text, option.Target);
        }
        else if (Kind == ScriptNodeKind.Condition)
        {
            yield return ("Да", True);
            yield return ("Нет", False);
        }
        else yield return ("Далее", Next);
    }

    /// <summary>Connects or disconnects a port by its displayed index.</summary>
    /// <param name="index">Zero-based output port index.</param>
    /// <param name="target">Destination identifier, or null to disconnect.</param>
    public void SetOutput(int index, string? target)
    {
        if (index < 0 || index >= Outputs().Count()) throw new ArgumentOutOfRangeException(nameof(index));
        if (Kind == ScriptNodeKind.Choice) Options[index].Target = target;
        else if (Kind == ScriptNodeKind.Condition)
        {
            if (index == 0) True = target;
            else False = target;
        }
        else Next = target;
    }
}

/// <summary>A choice label and the destination for its one-based runtime answer.</summary>
public sealed class ScriptOption
{
    /// <summary>Answer shown to the player.</summary>
    public string Text { get; set; } = "";

    /// <summary>Block to execute when this answer is selected.</summary>
    public string? Target { get; set; }
}
