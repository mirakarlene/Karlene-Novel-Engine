using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KNE;

/// <summary>Versioned source document for a visual scene. Layout is independent of execution order.</summary>
public sealed partial class VisualScript
{
    /// <summary>Shader graphs use data ports and never export a Lua entry point.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsShader { get; set; }
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
            script.SetShaderContext();
            if (!script.IsShader) script.DetachLegacyComments();
            foreach (var node in script.Nodes?.Where(node => node != null && node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene) ?? []) node.Category = "flows";
            // Both legacy categories compared a state value to a literal. Preserve the operation,
            // ports and IDs while moving it to the current narrative branching command.
            foreach (var node in script.Nodes?.Where(node => node != null && node.Kind == ScriptNodeKind.Condition) ?? [])
            { node.Kind = ScriptNodeKind.Branching; node.Category = "narrative"; }
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
        SetShaderContext();
        if (Version != 1) throw new InvalidDataException("Неподдерживаемая версия блок-схемы.");
        if (!LuaSource.IsIdentifier(FunctionName)) throw new InvalidDataException("Имя функции блок-схемы: латинские буквы, цифры и _, без цифры в начале и ключевых слов Lua.");
        if (Nodes == null || Nodes.Count > 500) throw new InvalidDataException("Допускается не более 500 блоков.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Id) || node.Id.Length > 80 || !ids.Add(node.Id))
                throw new InvalidDataException("У каждого блока должен быть уникальный непустой идентификатор.");
            if (!Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.ValueKind) || !Enum.IsDefined(node.Comparison) || !Enum.IsDefined(node.EndAction))
                throw new InvalidDataException("Неизвестный тип блока, значения или сравнения.");
            if (!double.IsFinite(node.X) || !double.IsFinite(node.Y) || Math.Abs(node.X) > 1_000_000_000 ||
                Math.Abs(node.Y) > 1_000_000_000)
                throw new InvalidDataException("Координаты блока должны быть конечными числами от −1000000000 до 1000000000.");
            if (!double.IsFinite(node.Width) || node.Width is < 160 or > 4096 ||
                node.Height is { } height && (!double.IsFinite(height) || height is < 100 or > 4096))
                Fail(node, "размер блока: ширина от 160 до 4096, высота от 100 до 4096");
            if (node.Options == null || node.Options.Count > 20 || node.Options.Any(o => o == null || o.Text == null))
                throw new InvalidDataException("У выбора должно быть не более 20 вариантов.");
            if (node.Text == null || node.Speaker == null || node.Path == null || node.Variable == null ||
                node.Value == null || node.CompareVariable == null || node.Position == null || node.Channel == null || node.Target == null || node.IterationKey == null || node.Category == null || node.ScreenLayer == null || node.UiTarget == null || node.SpeakerId == null)
                throw new InvalidDataException("Свойства блока не должны содержать null.");
            if (node.Category is not ("" or "terminals" or "narrative" or "logic" or "visual" or "audio" or "flows" or "calculations" or "async" or "triggers" or "configurations" or "shaders" or "plugins"))
                throw new InvalidDataException("Неизвестная группа блока.");
            if (!ShaderLibrary.CanPlace(node.Kind, IsShader)) throw new InvalidDataException("Блоки «Шейдеры» размещаются только в .kneshader, кроме контейнера; игровые блоки — в .knegraph.");
            if (ShaderLibrary.IsShader(node.Kind)) ShaderLibrary.ValidateNode(node);
            if (new[] { node.Duration, node.OffsetX, node.OffsetY, node.Amount, node.Minimum, node.Maximum, node.Opacity }.Any(v => !double.IsFinite(v)))
                throw new InvalidDataException("Числовые свойства блока должны быть конечными.");
            if (new[] { node.Text, node.Speaker, node.Path, node.Value }.Any(value => value.Length > 16000))
                throw new InvalidDataException("Свойство блока слишком длинное (максимум 16000 символов).");
            if (node.Options.Any(option => !double.IsFinite(option.Weight) || option.Weight < 0))
                Fail(node, "вес ветки должен быть конечным неотрицательным числом");
            try
            {
                node.Character?.Validate(); node.TextEffect?.Validate(); node.Input?.Validate(); node.Trigger?.Validate();
                if (CalculationCompiler.IsCalculation(node.Kind)) _ = CalculationCompiler.Compile(node.Kind, node.Calculation ?? new CalculationSettings(), node.Variable);
                node.ChoiceSettings?.Validate();
                foreach (var option in node.Options) option.Lock?.Validate();
            }
            catch (ArgumentException error) { Fail(node, error.Message); }
            if (node.SpeakerId.Length > 0 && (node.SpeakerId.Length > 128 || !LuaSource.IsIdentifier(node.SpeakerId)))
                Fail(node, "ID персонажа: латинские буквы, цифры и _, без цифры в начале");
            if (node.Kind == ScriptNodeKind.RandomBranch && (node.Options.Count < 2 || !node.Options.Any(option => option.Weight > 0)))
                Fail(node, "случайная ветка требует от 2 до 20 выходов и хотя бы один положительный вес");
            if (node.Ui != null)
            {
                try { node.Ui.Validate(); }
                catch (ArgumentException error) { Fail(node, error.Message); }
            }
        }
    }

    private void SetShaderContext()
    {
        foreach (var node in Nodes ?? [])
            if (node != null) node.IsShaderInvocation = !IsShader && node.Kind == ScriptNodeKind.ShaderContainer;
    }

    /// <summary>Validates the graph and generates its named Lua function using the KNE API.</summary>
    /// <returns>Lua source for a complete, acyclic scene.</returns>
    public string Compile(bool editorPreview = false, bool subsystem = false) => IsShader ? throw new InvalidDataException("Шейдер нельзя выполнять как сценарий Lua.") : CompileCore(editorPreview, subsystem);

    private void DetachLegacyComments()
    {
        if (Nodes == null || Nodes.Any(node => node == null || node.Id == null) || Nodes.Select(node => node.Id).Distinct().Count() != Nodes.Count) return;
        var map = Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in Nodes.Where(node => node.Kind != ScriptNodeKind.Comment))
        {
            var outputs = node.Outputs().ToArray();
            for (int i = 0; i < outputs.Length; i++)
            {
                string? target = outputs[i].Target; var seen = new HashSet<string>(StringComparer.Ordinal);
                while (target != null && map.TryGetValue(target, out var next) && next.Kind == ScriptNodeKind.Comment)
                {
                    if (!seen.Add(target)) throw new InvalidDataException("Циклическая цепочка комментариев в старой схеме.");
                    target = next.Next;
                }
                if (target != outputs[i].Target) node.SetOutput(i, target);
            }
        }
        foreach (var note in Nodes.Where(node => node.Kind == ScriptNodeKind.Comment)) { note.Next = note.True = note.False = null; note.Options?.Clear(); }
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
    internal static string Quote(string value)
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
    CallScene,
    Comment, Title, Switch, While, ForEach, TryCatch, SetFlag, CheckFlag, Random,
    HideBackground, MoveBackground, MoveCharacter, Animation, Shake, Flash, Camera, ShowScreen,
    AudioChannel, StopSound, Voice, MasterVolume, Parallel, Async, Join, Timer, Pause, WaitCondition,
    IncludeLua, IncludeCs, Script, ConfigureDialogue, ConfigureButton, ClearBackgrounds, ClearSprites, RegisterCharacter, TextPause, ClearText, AutoDialogue, ConsoleWrite, RandomBranch, EffectDialogue, TextInput,
    UpdateCharacter, SetSpeaker, ResetSpeakers, Branching, CompareVariables, TimedChoice,
    Subsystem, TriggerKey, TriggerButton, TriggerHotspot, TriggerTextClosed, TriggerCombination,
    Arithmetic, Modulo, Increment, Decrement, Pow, Sqrt, Trigonometry, Absolute, Clamp, Round, Floor, Ceil, Lerp, Remap, WeightedRandom, Constant, Formula,
    ShaderContainer, ShaderTexture, ShaderUv, ShaderTime, ShaderScreenPosition, ShaderFloat, ShaderVector, ShaderColor, ShaderTilingOffset, ShaderRotate, ShaderPolar, ShaderFlipbook, ShaderTwirl, ShaderSimplex, ShaderVoronoi, ShaderCheckerboard, ShaderLinearGradient, ShaderRadialGradient, ShaderSplit, ShaderCombine, ShaderHueShift, ShaderSaturation, ShaderContrast, ShaderBrightness, ShaderInvert, ShaderGrayscale, ShaderPosterize, ShaderGaussianBlur, ShaderBoxBlur, ShaderMotionBlur, ShaderBloom, ShaderChromatic, ShaderVignette, ShaderScanlines, ShaderLensDistortion, ShaderPixelate, ShaderSharpen, ShaderSobel, ShaderDithering, ShaderMix, ShaderAdd, ShaderSubtract, ShaderMultiply, ShaderScreen, ShaderStep, ShaderSmoothstep, ShaderOutput,
    RemoveLayerShaders, RemoveAllShaders
}

/// <summary>Explicit game termination policy; Return preserves existing subroutine graphs.</summary>
public enum ScriptEndAction { Return, Menu, Screen, Exit }

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
    [JsonIgnore] internal bool IsShaderInvocation { get; set; }
    /// <summary>Hides effects until this execution flow reaches another shader container.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SuppressShadersUntilContainer { get; set; }
    /// <summary>Shader-only values and input connections.</summary>
    public ShaderNodeSettings? Shader { get; set; }
    /// <summary>Operands, weighted rows, or reusable named formula steps.</summary>
    public CalculationSettings? Calculation { get; set; }
    /// <summary>Event block: connected blocks await the event; disconnected roots register an independent handler.</summary>
    [JsonIgnore] public bool IsTrigger => Kind is ScriptNodeKind.TriggerKey or ScriptNodeKind.TriggerButton or ScriptNodeKind.TriggerHotspot or ScriptNodeKind.TriggerTextClosed or ScriptNodeKind.TriggerCombination;
    /// <summary>Wait for a subsystem; false spawns it as a background task.</summary>
    public bool WaitForSubsystem { get; set; } = true;
    /// <summary>Event subscription parameters for trigger roots.</summary>
    public TriggerSettings? Trigger { get; set; }
    /// <summary>Stable document-local identifier used by connections and generated Lua.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The command represented by this block.</summary>
    public ScriptNodeKind Kind { get; set; }

    /// <summary>Catalog group selected by the author; empty keeps the legacy group mapping.</summary>
    public string Category { get; set; } = "";

    /// <summary>Horizontal canvas position in unscaled device-independent pixels.</summary>
    public double X { get; set; }

    /// <summary>Vertical canvas position in unscaled device-independent pixels.</summary>
    public double Y { get; set; }

    /// <summary>Authored card width in device-independent pixels; old graphs retain their original width.</summary>
    public double Width { get; set; } = 210;

    /// <summary>Authored card height, or null for a height fitted to the block's ports.</summary>
    public double? Height { get; set; }

    /// <summary>Dialogue speaker; an empty string means narration.</summary>
    public string Speaker { get; set; } = "";

    /// <summary>Explicit character profile reference; empty uses the manual speaker.</summary>
    public string SpeakerId { get; set; } = "";
    /// <summary>Explicit narration bypasses an inherited default speaker.</summary>
    public bool Narration { get; set; }
    /// <summary>Registration parameters; registration does not display a sprite.</summary>
    public CharacterProfile? Character { get; set; }
    /// <summary>Whether the player may skip a timed dialogue or text pause.</summary>
    public bool Skippable { get; set; } = true;
    /// <summary>Whether text clearing uses an erasing animation.</summary>
    public bool Animated { get; set; }
    /// <summary>Per-letter dialogue animation parameters.</summary>
    public TextEffectSettings? TextEffect { get; set; }

    /// <summary>Player input configuration; null retains the new-block defaults.</summary>
    public TextInputSettings? Input { get; set; }

    /// <summary>Countdown configuration for a timed choice.</summary>
    public ChoiceSettings? ChoiceSettings { get; set; }

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
    /// <summary>Second state key used by a variable-to-variable logic condition.</summary>
    public string CompareVariable { get; set; } = "other";

    /// <summary>Literal text, interpreted according to ValueKind.</summary>
    public string Value { get; set; } = "0";

    /// <summary>The type of an assignment or comparison literal.</summary>
    public ScriptValueKind ValueKind { get; set; } = ScriptValueKind.Number;

    /// <summary>Operator used by condition blocks.</summary>
    public ScriptComparison Comparison { get; set; }

    /// <summary>Seconds for an effect, pause, timer, or condition timeout.</summary>
    public double Duration { get; set; } = 1;
    /// <summary>Horizontal movement or camera offset in logical game pixels.</summary>
    public double OffsetX { get; set; }
    /// <summary>Vertical movement or camera offset in logical game pixels.</summary>
    public double OffsetY { get; set; }
    /// <summary>Effect intensity, camera zoom, or audio volume.</summary>
    public double Amount { get; set; } = 1;
    /// <summary>Inclusive random range minimum.</summary>
    public double Minimum { get; set; }
    /// <summary>Inclusive random range maximum.</summary>
    public double Maximum { get; set; } = 100;
    /// <summary>Audio channel: music, sfx, voice, or ambient.</summary>
    public string Channel { get; set; } = "music";
    /// <summary>Visual effect target: background or sprite.</summary>
    public string Target { get; set; } = "sprite";
    /// <summary>State key receiving the current ForEach array index.</summary>
    public string IterationKey { get; set; } = "index";
    /// <summary>Return from the current graph/body, open menu/screen, or close the game.</summary>
    public ScriptEndAction EndAction { get; set; }
    /// <summary>Transparency of a shown screen, from 0 to 1.</summary>
    public double Opacity { get; set; } = 1;
    /// <summary>Screen drawing layer: main or overlay.</summary>
    public string ScreenLayer { get; set; } = "overlay";
    /// <summary>Named gameplay control affected by a UI configuration.</summary>
    public string UiTarget { get; set; } = "dialogue";
    /// <summary>Typed gameplay appearance; null retains target defaults.</summary>
    public GameplayUiStyle? Ui { get; set; }

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
        ScriptNodeKind.RemoveLayerShaders => "Удалить шейдер с слоя", ScriptNodeKind.RemoveAllShaders => "Удалить все шейдеры",
        _ when ShaderLibrary.IsShader(Kind) => Kind == ScriptNodeKind.ShaderContainer && Path.Length > 0 ? System.IO.Path.GetFileName(Path) : ShaderLibrary.Get(Kind).Title,
        ScriptNodeKind.Start => "Начало", ScriptNodeKind.Dialogue => "Реплика", ScriptNodeKind.Choice => "Выбор", ScriptNodeKind.TimedChoice => "Выбор с таймером",
        ScriptNodeKind.Condition => "Условие (значение, прежний формат)", ScriptNodeKind.Branching => "Ветвление", ScriptNodeKind.CompareVariables => "Условие", ScriptNodeKind.SetVariable => "Переменная",
        ScriptNodeKind.Subsystem => Path.Length == 0 ? "Подсистема" : System.IO.Path.GetFileName(Path),
        ScriptNodeKind.TriggerKey => "При клике на клавишу", ScriptNodeKind.TriggerButton => "При клике на кнопку",
        ScriptNodeKind.TriggerHotspot => "При клике на зону на экране", ScriptNodeKind.TriggerTextClosed => "При закрытии текстового окна", ScriptNodeKind.TriggerCombination => "При комбинации нажатий",
        ScriptNodeKind.Background => "Фон",
        ScriptNodeKind.Character => "Спрайт", ScriptNodeKind.HideCharacter => "Скрыть спрайт",
        ScriptNodeKind.Music => "Музыка",
        ScriptNodeKind.StopMusic => "Остановить музыку", ScriptNodeKind.Sound => "Звук",
        ScriptNodeKind.GotoScene => "Перейти на сцену", ScriptNodeKind.CallScene => "Вызвать сцену",
        ScriptNodeKind.Comment => "Комментарий", ScriptNodeKind.Title => "Титр", ScriptNodeKind.Switch => "Switch / Case",
        ScriptNodeKind.While => "Цикл While", ScriptNodeKind.ForEach => "Цикл For Each", ScriptNodeKind.TryCatch => "Try / Catch",
        ScriptNodeKind.SetFlag => "Установить флаг", ScriptNodeKind.CheckFlag => "Проверить флаг", ScriptNodeKind.Random => "Случайное значение",
        ScriptNodeKind.HideBackground => "Скрыть фон", ScriptNodeKind.MoveBackground => "Передвижение фона", ScriptNodeKind.MoveCharacter => "Передвижение спрайта",
        ScriptNodeKind.Animation => "Анимация", ScriptNodeKind.Shake => "Тряска экрана", ScriptNodeKind.Flash => "Вспышка экрана",
        ScriptNodeKind.Camera => "Камера", ScriptNodeKind.ShowScreen => "Показать экран", ScriptNodeKind.AudioChannel => "Канал аудио",
        ScriptNodeKind.StopSound => "Остановить звук", ScriptNodeKind.Voice => "Озвучка", ScriptNodeKind.MasterVolume => "Общая громкость",
        ScriptNodeKind.Parallel => "Параллель", ScriptNodeKind.Async => "Асинхронность", ScriptNodeKind.Join => "Join",
        ScriptNodeKind.Timer => "Таймер", ScriptNodeKind.Pause => "Пауза", ScriptNodeKind.WaitCondition => "Ожидание условия",
        ScriptNodeKind.IncludeLua => "Подключить .lua", ScriptNodeKind.IncludeCs => "Подключить .cs", ScriptNodeKind.Script => "Скрипт",
        ScriptNodeKind.ConfigureDialogue => "Настроить текстовое окно", ScriptNodeKind.ConfigureButton => "Настроить кнопку",
        ScriptNodeKind.ClearBackgrounds => "Удалить все фоны", ScriptNodeKind.ClearSprites => "Удалить все спрайты",
        ScriptNodeKind.RegisterCharacter => "Персонаж", ScriptNodeKind.UpdateCharacter => "Изменить персонажа",
        ScriptNodeKind.SetSpeaker => "Установить спикера", ScriptNodeKind.ResetSpeakers => "Сбросить спикеров", ScriptNodeKind.TextPause => "Пауза в тексте",
        ScriptNodeKind.ClearText => "Очистить текст", ScriptNodeKind.AutoDialogue => "Авто-пропуск",
        ScriptNodeKind.ConsoleWrite => "Запись в консоль", ScriptNodeKind.RandomBranch => "Случайная ветка",
        ScriptNodeKind.EffectDialogue => "Реплика с эффектом", ScriptNodeKind.TextInput => "Ввод текста",
        ScriptNodeKind.Arithmetic => "Арифметика", ScriptNodeKind.Modulo => "Остаток от деления", ScriptNodeKind.Increment => "Инкремент", ScriptNodeKind.Decrement => "Декремент",
        ScriptNodeKind.Pow => "Степень", ScriptNodeKind.Sqrt => "Корень", ScriptNodeKind.Trigonometry => "Тригонометрия", ScriptNodeKind.Absolute => "Модуль числа",
        ScriptNodeKind.Clamp => "Ограничитель", ScriptNodeKind.Round => "Округление", ScriptNodeKind.Floor => "Floor", ScriptNodeKind.Ceil => "Ceil", ScriptNodeKind.Lerp => "Линейная интерполяция",
        ScriptNodeKind.Remap => "Переназначение диапазона", ScriptNodeKind.WeightedRandom => "Продвинутая случайность", ScriptNodeKind.Constant => "Константа", ScriptNodeKind.Formula => "Формула", _ => "Конец"
    };

    /// <summary>Returns ordered ports, including unconnected ports in an unfinished draft.</summary>
    /// <returns>Labels and optional destination identifiers in display order.</returns>
    public IEnumerable<(string Label, string? Target)> Outputs()
    {
        if (ShaderLibrary.IsData(this)) { foreach (string output in ShaderLibrary.Get(Kind).Outputs) yield return (output, null); yield break; }
        if (Kind is ScriptNodeKind.End or ScriptNodeKind.GotoScene or ScriptNodeKind.Comment) yield break;
        if (Kind == ScriptNodeKind.TextInput)
        {
            yield return (Input?.BranchOnResult == true ? "Успех" : "Далее", Next);
            if (Input is { BranchOnResult: true, HasErrorOutput: true }) yield return ("Ошибка", True);
            if (Input is { BranchOnResult: true, HasCancelOutput: true }) yield return ("Отмена", False);
        }
        else if (Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async or ScriptNodeKind.RandomBranch)
        {
            foreach (var option in Options) yield return (option.Text, option.Target);
            if (Kind == ScriptNodeKind.Switch) yield return ("По умолчанию", False);
            if (Kind is not (ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.RandomBranch)) yield return ("Далее", Next);
        }
        else if (Kind is ScriptNodeKind.Condition or ScriptNodeKind.Branching or ScriptNodeKind.CompareVariables or ScriptNodeKind.CheckFlag)
        {
            yield return ("Да", True);
            yield return ("Нет", False);
        }
        else if (Kind is ScriptNodeKind.While or ScriptNodeKind.ForEach or ScriptNodeKind.TryCatch)
        {
            yield return ("Тело", True);
            if (Kind == ScriptNodeKind.TryCatch) yield return ("Ошибка", False);
            yield return ("Далее", Next);
        }
        else yield return ("Далее", Next);
    }

    /// <summary>Connects or disconnects a port by its displayed index.</summary>
    /// <param name="index">Zero-based output port index.</param>
    /// <param name="target">Destination identifier, or null to disconnect.</param>
    public void SetOutput(int index, string? target)
    {
        if (index < 0 || index >= Outputs().Count()) throw new ArgumentOutOfRangeException(nameof(index));
        if (Kind == ScriptNodeKind.TextInput)
        {
            if (index == 0) Next = target;
            else if (index == 1 && Input?.HasErrorOutput == true) True = target;
            else False = target;
        }
        else if (Kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.Switch or ScriptNodeKind.Parallel or ScriptNodeKind.Async or ScriptNodeKind.RandomBranch)
        {
            if (index < Options.Count) Options[index].Target = target;
            else if (Kind == ScriptNodeKind.Switch && index == Options.Count) False = target;
            else Next = target;
        }
        else if (Kind is ScriptNodeKind.Condition or ScriptNodeKind.Branching or ScriptNodeKind.CompareVariables or ScriptNodeKind.CheckFlag)
        {
            if (index == 0) True = target;
            else False = target;
        }
        else if (Kind is ScriptNodeKind.While or ScriptNodeKind.ForEach or ScriptNodeKind.TryCatch)
        {
            if (index == 0) True = target;
            else if (Kind == ScriptNodeKind.TryCatch && index == 1) False = target;
            else Next = target;
        }
        else Next = target;
    }
}

/// <summary>A choice label and the destination for its one-based runtime answer.</summary>
public sealed class ScriptOption
{
    /// <summary>Optional state predicate and disabled-option presentation.</summary>
    public OptionLockSettings? Lock { get; set; }
    /// <summary>Answer shown to the player.</summary>
    public string Text { get; set; } = "";

    /// <summary>Relative nonnegative probability weight for a random branch.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Block to execute when this answer is selected.</summary>
    public string? Target { get; set; }
}
