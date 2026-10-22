using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace KNE;

/// <summary>Session-owned extension registry. Registrations disappear when the project or worker closes.</summary>
public sealed class PluginApi : IDisposable
{
    private readonly List<IDisposable> resources = [];
    private bool disposed;
    private readonly Dictionary<string, Func<IPluginContext, JsonObject, int>> executors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<PluginExecution>>> hooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Action<PluginEvent>>> events = new(StringComparer.Ordinal);
    private readonly List<Action<PluginInput>> input = [];
    private readonly Dictionary<string, Type> structures = new(StringComparer.Ordinal);
    private IReadOnlyList<BlockDefinition>? libraryDefinitions;
    private IReadOnlyList<BlockGroup>? libraryGroups;
    private PluginPalette? activePalette;
    internal readonly List<BlockGroup> groups = [];
    internal readonly List<PluginBlock> blocks = [];
    internal readonly List<PluginPalette> palettes = [];
    internal readonly List<PluginEditorTab> tabs = [];
    internal readonly List<PluginEditorCommand> commands = [];
    internal readonly List<PluginGameScreen> screens = [];
    internal readonly List<Func<VisualScript, IEnumerable<PluginDiagnostic>>> validators = [];
    internal readonly List<PluginDialogueFormat> formats = [];
    /// <summary>Gets the custom block definitions registered in this session.</summary>
    public IReadOnlyList<PluginBlock> Blocks => blocks.AsReadOnly();
    /// <summary>Gets the additional groups; built-in groups remain available through BlockLibrary.</summary>
    public IReadOnlyList<BlockGroup> Groups => groups.AsReadOnly();
    /// <summary>Gets project extension palettes.</summary>
    public IReadOnlyList<PluginPalette> Palettes => palettes.AsReadOnly();
    /// <summary>Gets resource and HTTP services owned by this session.</summary>
    public PluginAssets Assets { get; }
    /// <summary>Gets independent plugin audio channels and Windows speech synthesis.</summary>
    public PluginMedia Media { get; }
    /// <summary>Gets the canonical project directory.</summary>
    public string ProjectDirectory { get; }
    /// <summary>Whether registrations are being collected for the editor.</summary>
    public bool IsEditor { get; }
    /// <summary>Whether registrations are being collected for the game's WPF presentation process.</summary>
    public bool IsPlayer => Engine is GameWindow;
    /// <summary>Whether the runtime is replaying history to restore a save. Skip external effects during replay.</summary>
    public bool IsReplaying => Engine is LuaRuntime runtime && runtime.IsReplaying;
    internal bool HasInputListeners => input.Count > 0 || events.ContainsKey("on_key_press") || events.ContainsKey("on_mouse_click");
    internal bool HasNodeHooks => hooks.Keys.Any(kind => kind != "Dialogue");
    internal bool HasDialogueListeners => hooks.ContainsKey("Dialogue") || hooks.ContainsKey("*") || events.ContainsKey("on_text_displayed");
    internal Action? BindingsChanged { get; set; }
    internal IReadOnlyList<string> ActiveScreens { get; private set; } = [];
    internal PluginPalette? ActivePalette { get => activePalette; set { activePalette = value; libraryDefinitions = null; libraryGroups = null; } }
    internal IReadOnlyList<BlockDefinition> ExtendDefinitions(IReadOnlyList<BlockDefinition> builtIn) => libraryDefinitions ??= builtIn
        .Concat(blocks.Select(b => new BlockDefinition(ScriptNodeKind.Plugin, b.Group, b.Title, b.Description, b.Color, b)))
        .Select(b => b.Kind is not (ScriptNodeKind.Start or ScriptNodeKind.End) && ActivePalette?.Groups?.TryGetValue(b.Group, out string? color) == true ? b with { Color = color } : b).ToArray();
    internal IReadOnlyList<BlockGroup> ExtendGroups(IReadOnlyList<BlockGroup> builtIn) => libraryGroups ??= builtIn.Where(g => g.Id != "plugins").Concat(groups).Append(builtIn[^1])
        .Select(g => ActivePalette?.Groups?.TryGetValue(g.Id, out string? color) == true ? g with { Color = color } : g).ToArray();
    /// <summary>Direct engine object for trusted extensions: MainWindow in the editor, LuaRuntime in a worker.</summary>
    public object? Engine { get; internal set; }
    /// <summary>Gets the optional absolute editor workspace PNG path.</summary>
    public string? WorkspaceTexture { get; private set; }
    internal System.Windows.Media.Imaging.BitmapSource? WorkspaceImage { get; private set; }
    /// <summary>Gets the optional project-relative initial game texture.</summary>
    public string? GameTexture { get; private set; }

    /// <summary>Creates a registry and its owned services.</summary>
    /// <param name="projectDirectory">Existing project root.</param><param name="isEditor">Whether this registry is for editor extensions.</param>
    public PluginApi(string projectDirectory, bool isEditor = false)
    {
        ProjectDirectory = PathSafety.NormalizeDirectory(projectDirectory); IsEditor = isEditor;
        Assets = new(ProjectDirectory); Media = new();
    }

    /// <summary>Adds an additional group before the built-in Plugins group.</summary>
    /// <param name="group">Unique ID, title, color and Unicode icon.</param>
    public void RegisterGroup(BlockGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        Guard(); Id(group.Id); Color(group.Color);
        if (group.Id is "favorites" or "recent") throw new ArgumentException("ID группы зарезервирован каталогом: " + group.Id);
        if (string.IsNullOrWhiteSpace(group.Title) || group.Title.Length > 100 || group.Icon == null || group.Icon.Length > 32) throw new ArgumentException("Название группы: до 100 символов, иконка: до 32.");
        if (BlockLibrary.BuiltInGroups.Any(g => g.Id == group.Id) || groups.Any(g => g.Id == group.Id)) throw new ArgumentException("Группа уже зарегистрирована: " + group.Id);
        groups.Add(group); libraryGroups = null;
    }

    /// <summary>Adds a custom execution block and validates its parameter/port schema.</summary>
    /// <param name="block">Stable definition, placed in an existing or custom group.</param>
    /// <param name="execute">Synchronous worker callback returning a one-based output index, or zero for a terminal.</param>
    public void RegisterBlock(PluginBlock block, Func<IPluginContext, JsonObject, int> execute)
    {
        ArgumentNullException.ThrowIfNull(block);
        Guard(); ArgumentNullException.ThrowIfNull(execute); Id(block.Id); Color(block.Color);
        if (string.IsNullOrWhiteSpace(block.Title) || block.Title.Length > 200) throw new ArgumentException("Название блока: от 1 до 200 символов.");
        if (!BlockLibrary.BuiltInGroups.Concat(groups).Any(g => g.Id == block.Group)) throw new ArgumentException("Неизвестная группа: " + block.Group);
        if (blocks.Any(b => b.Id == block.Id)) throw new ArgumentException("Блок уже зарегистрирован: " + block.Id);
        ValidatePorts(block.Inputs ?? ["Вход"]); ValidatePorts(block.Outputs ?? ["Далее"]);
        var parameters = block.Parameters ?? [];
        if (parameters.Length > 32 || parameters.Select(p => p.Id).Distinct().Count() != parameters.Length) throw new ArgumentException("Не более 32 уникальных параметров.");
        foreach (var p in parameters)
        {
            Id(p.Id);
            if (p.Kind is not ("text" or "slider" or "choice" or "boolean") || !double.IsFinite(p.Minimum) || !double.IsFinite(p.Maximum) || p.Minimum > p.Maximum)
                throw new ArgumentException("Некорректный тип или диапазон параметра: " + p.Id);
            _ = ParameterDefault(p);
            if (p.Kind == "choice" && (p.Choices is not { Length: > 0 and <= 100 } || !p.Choices.Contains(p.Default))) throw new ArgumentException("Выпадающий список требует варианты и допустимое значение по умолчанию.");
        }
        block = block with { Inputs = block.Inputs?.ToArray(), Outputs = block.Outputs?.ToArray(), Parameters = parameters.Select(p => p with { Choices = p.Choices?.ToArray() }).ToArray() };
        blocks.Add(block); libraryDefinitions = null; executors.Add(block.Id, execute);
    }

    /// <summary>Register a Lua-backed block. Its function receives the parameters as JSON and returns a 1-based output index.</summary>
    /// <param name="block">Block schema.</param><param name="functionName">Existing global Lua function name.</param>
    public void RegisterLuaBlock(PluginBlock block, string functionName)
    {
        if (!LuaSource.IsIdentifier(functionName)) throw new ArgumentException("Требуется имя Lua-функции.");
        RegisterBlock(block, (context, values) => context.InvokeLua(functionName, values.ToJsonString()));
    }

    /// <summary>Intercepts a block kind or all kinds using '*'; dialogue hooks may replace text and speaker.</summary>
    /// <param name="kind">ScriptNodeKind name, Dialogue, or '*'.</param><param name="hook">Synchronous worker callback.</param>
    public void BeforeExecute(string kind, Action<PluginExecution> hook) { Guard(); Add(hooks, kind, hook); BindingsChanged?.Invoke(); }
    /// <summary>Subscribes to runtime events, including save/load, scene, text and input notifications.</summary>
    /// <param name="name">Event name, such as on_save.</param><param name="handler">Callback receiving a lifetime-bound execution context.</param>
    public void On(string name, Action<PluginEvent> handler) { Guard(); Add(events, name, handler); BindingsChanged?.Invoke(); }
    /// <summary>Intercepts player input synchronously before built-in WPF/engine handling.</summary>
    /// <param name="handler">Sets Handled to consume an input event.</param>
    public void InterceptInput(Action<PluginInput> handler) { Guard(); ArgumentNullException.ThrowIfNull(handler); input.Add(handler); }
    /// <summary>Registers the JSON contract of a plugin-defined .NET structure.</summary>
    /// <typeparam name="T">Serializable data type.</typeparam><param name="id">Unique structure ID.</param>
    public void RegisterStructure<T>(string id)
    {
        Guard(); Id(id);
        if (!structures.TryAdd(id, typeof(T))) throw new ArgumentException("Структура уже зарегистрирована: " + id);
    }
    /// <summary>Deserializes through the registered type and returns detached JSON suitable for SetJson.</summary>
    /// <param name="id">Structure ID.</param><param name="json">Payload up to 1 MiB.</param><returns>Typed JSON data.</returns>
    public JsonElement CreateStructure(string id, string json)
    {
        Guard();
        if (json.Length > 1024 * 1024) throw new ArgumentException("Структура плагина больше 1 МБ.");
        if (!structures.TryGetValue(id, out var type)) throw new ArgumentException("Неизвестная структура: " + id);
        return JsonSerializer.SerializeToElement(JsonSerializer.Deserialize(json, type) ?? throw new ArgumentException("Структура не может быть null."), type);
    }
    /// <summary>Registers an editor palette, including optional category color overrides.</summary>
    /// <param name="palette">Unique ID, display title and hexadecimal colors.</param>
    public void RegisterPalette(PluginPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Guard(); Id(palette.Id); foreach (string color in new[] { palette.Background, palette.Surface, palette.Text, palette.Accent, palette.Muted }.Concat(palette.Groups?.Values.AsEnumerable() ?? [])) Color(color);
        if (EditorTheme.BuiltInThemes.Any(p => p.Id == palette.Id) || palettes.Any(p => p.Id == palette.Id)) throw new ArgumentException("Палитра уже существует: " + palette.Id);
        palettes.Add(palette with { Groups = palette.Groups == null ? null : new(palette.Groups, StringComparer.Ordinal) });
    }
    /// <summary>Copies a registered palette into authored game settings; the persisted custom palette needs no plugin to load.</summary>
    /// <param name="id">Palette ID.</param><param name="theme">Game settings to modify; saving remains the caller's choice.</param>
    public void ApplyGamePalette(string id, GameTheme theme)
    {
        Guard(); ArgumentNullException.ThrowIfNull(theme);
        var palette = palettes.SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("Неизвестная палитра: " + id);
        theme.Palette = "custom"; theme.BackgroundColor = palette.Background; theme.PatternColor = palette.Muted;
        theme.AccentColor = palette.Accent; theme.TextColor = palette.Text; theme.ButtonColor = palette.Surface;
        theme.Dialogue.BackgroundColor = palette.Surface; theme.Dialogue.TextColor = palette.Text; theme.Dialogue.SpeakerColor = palette.Accent; theme.Dialogue.BorderColor = palette.Muted;
    }
    /// <summary>Sets an editor workspace texture.</summary><param name="path">Local PNG path, absolute or relative to the project.</param>
    public void SetWorkspaceTexture(string path) { Guard(); string full = Assets.Local(path, ".png"); var image = Assets.LoadImage(full); WorkspaceTexture = full; WorkspaceImage = image; }
    /// <summary>Imports a PNG into the project and uses it as the initial scene background.</summary><param name="path">Local PNG path.</param>
    public void SetGameTexture(string path) { Guard(); GameTexture = Assets.ImportLocal(Assets.Local(path, ".png")); }
    /// <summary>Registers an openable editor tab; its factory runs on the editor Dispatcher.</summary><param name="tab">Unique tab and view factory.</param>
    public void RegisterTab(PluginEditorTab tab) { Guard(); Unique(tabs.Select(t => t.Id), tab.Id); tabs.Add(tab); }
    /// <summary>Registers a toolbar/context command, including undo-aware graph generators.</summary><param name="command">Command definition.</param>
    public void RegisterCommand(PluginEditorCommand command)
    {
        Guard(); Unique(commands.Select(c => c.Id), command.Id);
        if (command.Location is not ("toolbar" or "context")) throw new ArgumentException("Расположение команды: toolbar или context.");
        commands.Add(command);
    }
    /// <summary>Registers a game overlay factory; use ShowScreen to activate it.</summary><param name="screen">Unique screen and WPF factory.</param>
    public void RegisterScreen(PluginGameScreen screen) { Guard(); Unique(screens.Select(s => s.Id), screen.Id); screens.Add(screen); }
    /// <summary>Changes overlay visibility in persistent runtime state.</summary>
    /// <param name="context">Current runtime callback context.</param><param name="id">Screen ID.</param><param name="visible">Whether to display it.</param>
    public void ShowScreen(IPluginContext context, string id, bool visible = true)
    {
        Guard(); if (!screens.Any(s => s.Id == id)) throw new ArgumentException("Неизвестный экран плагина: " + id);
        var current = context.GetJson("__kne_plugin_screens");
        var ids = current.ValueKind == JsonValueKind.Array ? current.EnumerateArray().Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        if (visible) ids.Add(id); else ids.Remove(id);
        ActiveScreens = ids.Order(StringComparer.Ordinal).ToArray(); context.SetJson("__kne_plugin_screens", JsonSerializer.SerializeToElement(ActiveScreens));
    }
    internal void RestoreScreens(IPluginContext context)
    {
        var value = context.GetJson("__kne_plugin_screens");
        ActiveScreens = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(v => v.GetString()!).Where(id => screens.Any(s => s.Id == id)).Distinct().ToArray() : [];
    }
    /// <summary>Reads namespaced plugin save data.</summary><param name="context">Runtime context.</param><param name="pluginId">Owner ID.</param><returns>Detached JSON or null.</returns>
    public JsonElement GetSaveData(IPluginContext context, string pluginId) { Guard(); Id(pluginId); return context.GetJson("__plugin_" + pluginId); }
    /// <summary>Stores plugin data in state for the next checkpoint.</summary><typeparam name="T">Serializable type.</typeparam><param name="context">Runtime context.</param><param name="pluginId">Owner ID.</param><param name="data">Payload.</param>
    public void SetSaveData<T>(IPluginContext context, string pluginId, T data) { Guard(); Id(pluginId); context.SetJson("__plugin_" + pluginId, JsonSerializer.SerializeToElement(data)); }
    /// <summary>Adds a static graph validator to project analysis.</summary><param name="validator">Receives a detached graph; return actionable diagnostics.</param>
    public void RegisterValidator(Func<VisualScript, IEnumerable<PluginDiagnostic>> validator) { Guard(); ArgumentNullException.ThrowIfNull(validator); validators.Add(validator); }
    /// <summary>Adds or overrides a dialogue interchange format.</summary><param name="format">Extension and paired import/export callbacks.</param>
    public void RegisterDialogueFormat(PluginDialogueFormat format)
    {
        Guard();
        if (!Regex.IsMatch(format.Extension, "^\\.[a-z0-9]{1,12}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Расширение формата: .json, .docx и т. п.");
        if (formats.Any(f => f.Extension == format.Extension)) throw new ArgumentException("Формат уже зарегистрирован.");
        formats.Add(format);
    }
    /// <summary>Exports dialogue using a custom format or built-in JSON/DOCX/PDF.</summary><param name="graph">Source graph.</param><param name="extension">Dot-prefixed format.</param><returns>File bytes.</returns>
    public byte[] ExportDialogues(VisualScript graph, string extension)
    {
        Guard(); var rows = PluginDialogues.Read(graph); var format = formats.FirstOrDefault(f => f.Extension == extension.ToLowerInvariant());
        return format == null ? PluginDialogues.Export(rows, extension) : format.Export(rows);
    }
    /// <summary>Applies a translation to a detached graph, leaving the original untouched on error.</summary><param name="graph">Source graph.</param><param name="data">Translation bytes.</param><param name="extension">Format extension.</param><returns>Translated graph.</returns>
    public VisualScript ImportDialogues(VisualScript graph, byte[] data, string extension)
    {
        Guard(); if (data.Length > 16 * 1024 * 1024) throw new ArgumentException("Перевод больше 16 МБ.");
        var format = formats.FirstOrDefault(f => f.Extension == extension.ToLowerInvariant());
        return PluginDialogues.Apply(graph, format == null ? PluginDialogues.Import(data, extension) : format.Import(data));
    }
    /// <summary>Transfers disposal responsibility to this session.</summary><typeparam name="T">Disposable type.</typeparam><param name="resource">Owned resource.</param><returns>The same resource.</returns>
    public T Own<T>(T resource) where T : IDisposable { Guard(); ArgumentNullException.ThrowIfNull(resource); resources.Add(resource); return resource; }
    /// <summary>Runs every registered validator on an independent graph copy.</summary><param name="graph">Draft to analyze.</param><returns>At most 1000 diagnostics per validator.</returns>
    public IEnumerable<PluginDiagnostic> Validate(VisualScript graph)
    {
        Guard();
        foreach (var validator in validators)
            foreach (var diagnostic in validator(VisualScript.Parse(graph.Serialize())).Take(1000)) yield return diagnostic;
    }
    internal int Execute(string id, IPluginContext context, string json)
    {
        Guard();
        if (!executors.TryGetValue(id, out var execute)) throw new InvalidOperationException("Не найден блок плагина: " + id + ". Установите его расширение или отключите этот блок.");
        var values = JsonNode.Parse(json)?.AsObject() ?? new(); var definition = blocks.Single(b => b.Id == id);
        var parameters = definition.Parameters ?? [];
        if (values.Any(p => !parameters.Any(d => d.Id == p.Key))) throw new ArgumentException("Блок содержит неизвестный параметр.");
        foreach (var parameter in parameters)
        {
            if (!values.ContainsKey(parameter.Id)) values[parameter.Id] = ParameterDefault(parameter);
            string value = values[parameter.Id]?.ToString() ?? "";
            if (parameter.Kind == "slider" && (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number) || number < parameter.Minimum || number > parameter.Maximum)) throw new ArgumentException("Параметр вне диапазона: " + parameter.Id);
            if (parameter.Kind == "boolean" && !bool.TryParse(value, out _)) throw new ArgumentException("Требуется bool: " + parameter.Id);
            if (parameter.Kind == "choice" && !parameter.Choices!.Contains(value)) throw new ArgumentException("Неизвестный вариант: " + parameter.Id);
            if (value.Length > 16000) throw new ArgumentException("Параметр длиннее 16000 символов.");
        }
        return execute(context, values);
    }
    internal static JsonNode? ParameterDefault(PluginParameter parameter)
    {
        if (parameter.Kind == "slider")
        {
            double value = parameter.Minimum;
            if (parameter.Default.Length > 0 && (!double.TryParse(parameter.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || !double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)) throw new ArgumentException("Недопустимое значение ползунка по умолчанию.");
            return JsonValue.Create(value);
        }
        if (parameter.Kind == "boolean")
        {
            if (parameter.Default.Length > 0 && !bool.TryParse(parameter.Default, out _)) throw new ArgumentException("Недопустимый bool по умолчанию.");
            return JsonValue.Create(bool.TryParse(parameter.Default, out bool enabled) && enabled);
        }
        return JsonValue.Create(parameter.Default);
    }
    internal void Hook(PluginExecution execution)
    {
        Guard();
        foreach (string key in new[] { "*", execution.Kind }) if (hooks.TryGetValue(key, out var callbacks)) foreach (var callback in callbacks.ToArray()) callback(execution);
    }
    internal void Publish(string name, JsonElement data, IPluginContext context)
    {
        Guard(); if (events.TryGetValue(name, out var callbacks)) foreach (var callback in callbacks.ToArray()) callback(new(name, data, context));
    }
    internal bool Input(PluginInput value) { Guard(); foreach (var handler in input.ToArray()) { handler(value); if (value.Handled) break; } return value.Handled; }
    private void Guard() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void Add<T>(Dictionary<string, List<T>> map, string id, T value) { ArgumentException.ThrowIfNullOrWhiteSpace(id); ArgumentNullException.ThrowIfNull(value); if (!map.TryGetValue(id, out var list)) map[id] = list = []; list.Add(value); }
    internal static void Id(string id) { ArgumentException.ThrowIfNullOrWhiteSpace(id); if (id.Length > 160 || !Regex.IsMatch(id, "^[A-Za-z_][A-Za-z0-9_.:-]*$", RegexOptions.CultureInvariant)) throw new ArgumentException("ID: латиница, цифры, _, ., :, -; без пробелов."); }
    private static void Unique(IEnumerable<string> ids, string id) { Id(id); if (ids.Contains(id, StringComparer.Ordinal)) throw new ArgumentException("ID уже зарегистрирован: " + id); }
    private static void Color(string value) { if (value.Length is not (7 or 9) || value[0] != '#' || value.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF")) throw new ArgumentException("Цвет: #RRGGBB или #AARRGGBB."); }
    private static void ValidatePorts(string[] ports) { if (ports.Length > 20 || ports.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 80) || ports.Distinct().Count() != ports.Length) throw new ArgumentException("До 20 уникальных непустых портов."); }
    /// <summary>Releases delegates, owned resources, HTTP services and media.</summary>
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var resource in resources.AsEnumerable().Reverse()) try { resource.Dispose(); } catch (Exception e) { EngineDiagnostics.Trace("Освобождение ресурса плагина", e.Message); }
        resources.Clear(); hooks.Clear(); events.Clear(); input.Clear(); executors.Clear(); structures.Clear();
        groups.Clear(); blocks.Clear(); palettes.Clear(); tabs.Clear(); commands.Clear(); screens.Clear(); validators.Clear(); formats.Clear();
        Assets.Dispose(); Media.Dispose(); Engine = null;
        libraryGroups = null; libraryDefinitions = null; activePalette = null; ActiveScreens = [];
        BindingsChanged = null;
        WorkspaceImage = null; WorkspaceTexture = GameTexture = null;
    }
}

/// <summary>The active editor registry. Game workers own an independent registry.</summary>
internal static class PluginCatalog
{
    internal static PluginApi? Current { get; set; }
}
