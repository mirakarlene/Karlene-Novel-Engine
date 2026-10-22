using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using NLua;

namespace KNE;

/// <summary>Owns a Lua state used only by serialized background operations.</summary>
public sealed partial class LuaRuntime : IGameSession
{
    /// <summary>The loaded game model exposed to trusted extensions.</summary>
    public ProjectInfo Project => project;
    private readonly ProjectInfo project;
    private readonly Action<string> output;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private Lua? lua;
    private CancellationToken executionToken;
    private long deadline;
    private bool initialized;
    private bool stopping;
    private Task? disposal;
    private RuntimeWindowSettings windowSettings;
    private readonly bool interactive;
    private readonly bool pluginsEnabled;
    private StoryFrame story = StoryFrame.Empty;
    private SceneVisuals visuals = SceneVisuals.Empty;
    private readonly List<StoryAction> history = [];
    private bool replaying;
    private int seed = Random.Shared.Next(1, int.MaxValue);
    private string fingerprint = "";
    private GameScripts? scripts;
    private FrameCatalog? frames;
    private FrameIdentity? currentScriptFrame;
    private readonly bool preview;
    private readonly EditorPreviewSource? editorSource;
    private readonly Action<JsonElement>? variablesUpdated;
    private JsonElement variables = JsonSerializer.SerializeToElement(new { Globals = new { }, Frames = new { } });
    private PreviewLocation? location;
    private long nextVariablesAt;
    private bool transitionPending;
    private (string? File, int? Line, string Trace, string Problem, FrameIdentity? Frame)? scriptFailure;
    private bool pluginFailureCaptured;
    public TimeSpan ExecutionLimit { get; }

    public LuaRuntime(ProjectInfo project, Action<string> output, TimeSpan? executionLimit = null, bool interactive = false, bool preview = false,
        EditorPreviewSource? editorSource = null, Action<JsonElement>? variablesUpdated = null, bool? pluginsEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        this.project = project;
        this.output = output;
        this.interactive = interactive;
        this.pluginsEnabled = pluginsEnabled ?? EngineLaunch.PluginsEnabled;
        this.preview = preview;
        this.editorSource = editorSource;
        if (editorSource?.Seed is { } previewSeed) seed = previewSeed;
        this.variablesUpdated = variablesUpdated;
        ExecutionLimit = executionLimit ?? TimeSpan.FromSeconds(project.Engine.Game.ExecutionTimeoutSeconds);
        if (ExecutionLimit <= TimeSpan.Zero || ExecutionLimit > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(executionLimit));
        windowSettings = project.Engine.RuntimeDefaults(project.Title);
    }

    public RuntimeWindowSettings WindowSettings
    {
        get { lock (stateLock) return windowSettings; }
    }

    public StoryFrame Story { get { lock (stateLock) return story; } }
    public SceneVisuals Visuals { get { lock (stateLock) return visuals; } }
    public PreviewLocation? Location { get { lock (stateLock) return location; } }
    public bool PreviewPaused => Story.Kind == "preview";
    public JsonElement Variables { get { lock (stateLock) return variables; } }
    public bool TransitionInProgress { get { lock (stateLock) return transitionPending; } }

    public Task StartAsync(CancellationToken cancellationToken = default) => RunAsync(StartCore, cancellationToken, "Запуск игры");

    private void StartCore()
    {
        if (initialized) throw new InvalidOperationException("Игра уже запущена.");
        transitionPending = false;
        nextVariablesAt = 0;
        lock (stateLock) location = null;
        scripts = GameScripts.Load(project, editorSource, pluginsEnabled);
        frames = FrameCatalog.Load(project, scripts, editorSource);
        lua?.Dispose();
        lua = new Lua();
        shaderTargets.Clear();
        lua.State.Encoding = Encoding.UTF8;
        lock (stateLock) windowSettings = project.Engine.RuntimeDefaults(project.Title);
        Register(nameof(PrintToUI), "print_to_ui");
        Register(nameof(ShouldStop), "kne_should_stop");
        Register(nameof(SceneCodeForLua), "kne_scene_code_result");
        Register(nameof(RecordScriptError), "kne_record_script_error");
        Register(nameof(RecordScriptContext), "kne_record_script_context");
        Register(nameof(InvokeScriptApi), "kne_script_api");
        Register(nameof(ReceiveVariables), "kne_receive_variables");
        Register(nameof(ShouldPublishVariables), "kne_should_publish_variables");
        Register(nameof(TransitionPending), "kne_transition_pending");
        Register(nameof(ErrorSnapshotNeeded), "kne_error_snapshot_needed");
        Register(nameof(ValidateEndAction), "kne_validate_end");
        Register(nameof(SuspendFlowError), "kne_suspend_flow_error");
        Register(nameof(RestoreFlowError), "kne_restore_flow_error");
        Register(nameof(SubsystemCodeForLua), "kne_subsystem_code_result");
        Register(nameof(ValidateTriggerForLua), "kne_validate_trigger");
        Register(nameof(TriggerMatchesForLua), "kne_trigger_matches");
        Register(nameof(TriggerOnceForLua), "kne_trigger_once");
        Register(nameof(ShaderSourceForLua), "kne_shader_source");
        lua["project_dir"] = project.Path;
        lua["kne_interactive"] = interactive;
        lua["kne_editor"] = editorSource?.StepCommands == true;
        lua["kne_scene_test"] = editorSource?.SceneTest == true;
        lua["kne_seed"] = seed;
        lua["kne_max_async_tasks"] = project.Engine.Performance.MaxAsyncTasks;
        fingerprint = ComputeFingerprint(scripts);
        lua.DoString("""
            function print(...)
                local values = {}
                for i = 1, select('#', ...) do values[i] = tostring(select(i, ...)) end
                print_to_ui(table.concat(values, "\t"))
            end
            state = {}
            math.randomseed(kne_seed)
            function init() error("init(приоритет) должен быть первым оператором файла до запуска игры.") end
            for _, name in ipairs({"set_window_title", "set_window_size", "set_bg_color", "background",
                                  "show_character", "hide_character", "play_music", "stop_music", "play_sound",
                                  "show_title", "hide_background", "move_visual", "animate_visual", "screen_shake",
                                  "screen_flash", "camera", "show_screen", "set_audio_channel", "play_audio", "stop_audio", "set_master_volume",
                                  "clear_backgrounds", "clear_sprites", "configure_ui"}) do
                _G[name] = function(...)
                    local problem = kne_script_api(name, table.pack(...))
                    if problem ~= "" then error(problem, 2) end
                    kne_editor_pause()
                end
            end
            function d(speaker, text)
                if kne_interactive and coroutine.isyieldable() then
                    return coroutine.yield(kne_event_location({kind = "dialogue", speaker = speaker == nil and "" or tostring(speaker), text = tostring(text)}))
                end
                if speaker == nil or tostring(speaker) == "" then
                    print_to_ui(tostring(text))
                else
                    print_to_ui(tostring(speaker) .. ": " .. tostring(text))
                end
            end
            function choice(prompt, options)
                if not kne_interactive or not coroutine.isyieldable() then error("choice() доступен только внутри игровой сцены") end
                if type(options) ~= "table" or #options < 1 or #options > 20 then error("choice() требует от 1 до 20 вариантов") end
                local labels = {}
                for i = 1, #options do
                    if type(options[i]) ~= "string" or options[i] == "" then error("Варианты choice() должны быть непустыми строками") end
                    labels[i] = options[i]
                end
                return coroutine.yield(kne_event_location({kind = "choice", text = tostring(prompt), options = labels}))
            end
            function kne_resume(answer, seconds, tick, signal)
                local ok, event = coroutine.resume(kne_story, answer, seconds, tick, signal)
                if not ok then error(tostring(event), 0) end
                return coroutine.status(kne_story), event
            end
            """, "KNE API");
        lua.DoString(RuntimeDiagnosticsLua.Source, "KNE diagnostics");
        lua.DoString(SceneRuntimeLua.Source, "KNE scene scheduler");
        lua.DoString(CalculationRuntimeLua.Source, "KNE calculations");
        lua.DoString("kne_diagnostics_baseline()", "KNE baseline");
        // Resolve all paths before executing any project code.
        lua.DoString("state = " + StateLiteral(project.Engine.InitialState), "KNE initial state");
        InitializePlugins();
        lua.DoString("""
            function apply_shader(path, enabled)
                local target, screen = "", ""
                if enabled ~= false then
                    local result = kne_shader_source(path)
                    if result[1] ~= "" then error(result[1], 2) end
                    local setup, problem = load(result[0], "@" .. path .. " · plugins", "t", _G)
                    if not setup then error(problem, 2) end
                    setup()
                    target, screen = result[2], result[3]
                end
                local problem = kne_script_api("apply_shader", table.pack(path, enabled ~= false, target, screen, kne_shader_flow()))
                if problem ~= "" then error(problem, 2) end
                kne_editor_pause()
            end
            function clear_layer_shaders(target, screen, suppress)
                if screen == nil then screen = "" end
                if suppress ~= nil and type(suppress) ~= "boolean" then error("Ожидается true/false для скрытия шейдеров", 2) end
                local problem = kne_script_api("clear_layer_shaders", table.pack(target, screen, suppress == true, kne_shader_flow()))
                if problem ~= "" then error(problem, 2) end
                kne_editor_pause()
            end
            function clear_shaders(suppress)
                if suppress ~= nil and type(suppress) ~= "boolean" then error("Ожидается true/false для скрытия шейдеров", 2) end
                local problem = kne_script_api("clear_shaders", table.pack(suppress == true, kne_shader_flow()))
                if problem ~= "" then error(problem, 2) end
                kne_editor_pause()
            end
            """, "KNE shader activation");
        InitializeNarrative();
        InitializeTextInput();
        InitializeChoices();
        InitializePluginApi();
        string initPath = project.InitScriptPath;
        string scenePath = project.MainScenePath;
        string scriptModules = Path.GetDirectoryName(initPath)!.Replace('\\', '/') + "/?.lua";
        string root = project.Path.Replace('\\', '/') + "/?.lua";
        lua["package.path"] = scriptModules + ";" + root + ";" + lua["package.path"];
        InstallHook();
        using (var declare = (LuaFunction)lua["kne_declare_function"])
            foreach (var function in scripts.Functions) declare.Call(function.Name, function.Path);
        using (var load = (LuaFunction)lua["kne_load_script"])
        {
            foreach (var script in scripts.Initialization) load.Call(script.Path);
            load.Call(preview ? Path.GetRelativePath(project.Path, scenePath).Replace('\\', '/') : scripts.Main.Path);
        }
        string entryName = editorSource?.EntryFunction ?? (preview ? scripts.PreviewFunction(Path.GetRelativePath(project.Path, scenePath)) : "main");
        if (editorSource?.EntryFunction != null && !scripts.Functions.Any(f => f.Name == entryName &&
            f.Path.Equals(editorSource.RelativePath(project.Path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Функция предпросмотра не объявлена в выбранном файле: " + entryName);
        lua["kne_entry"] = lua[entryName];
        PublishPluginEvent("on_scene_start", new { Scene = entryName });
        using var main = lua["kne_entry"] as LuaFunction ?? throw new InvalidDataException("Не найдена игровая функция: " + entryName);
        if (interactive)
        {
            lua.DoString("kne_story = kne_scene_thread(function() return kne_run_scene(kne_entry) end)", "KNE scene");
            ResumeStory(null);
        }
        else
        {
            using var run = (LuaFunction)lua["kne_run_scene"];
            run.Call(main);
        }
        initialized = true;
    }

    /// <summary>Canonicalizes a project scene path for the coroutine scheduler's per-session cache.</summary>
    public string ResolveScenePath(string relative)
    {
        string path = PathSafety.ResolveRelative(project.Path, relative);
        if (!ScriptDocument.IsCodePath(path) && !ScriptDocument.IsVisualPath(path))
            throw new ArgumentException("Сцена должна быть Lua, KRL или .knegraph.");
        if (!File.Exists(path)) throw new FileNotFoundException("Сцена не найдена: " + relative, path);
        return Path.GetRelativePath(project.Path, path).Replace('\\', '/').ToUpperInvariant();
    }

    /// <summary>Reads a validated scene, compiling a graph into the same Lua API as manual code.</summary>
    public string ReadSceneCode(string relative)
    {
        if (scripts != null)
        {
            var script = scripts.FindFile(relative);
            return script.Graph?.Compile(editorPreview: editorSource?.StepCommands == true) ?? FrameCatalog.Instrument(script.Path, script.Source);
        }
        string path = PathSafety.ResolveRelative(project.Path, ResolveScenePath(relative));
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Сцена больше 8 МБ.");
        string code = File.ReadAllText(path);
        return ScriptDocument.IsVisualPath(path) ? VisualScript.Parse(code).Compile() : LuaSource.Prepare(code);
    }

    // NLua managed exceptions do not reliably cross coroutine.resume. Return errors as data;
    // the Lua scheduler raises them in the active coroutine instead.
    /// <summary>Returns scene source and error text for the Lua coroutine bridge.</summary>
    public string[] SceneCodeForLua(string relative)
    {
        try { return [ReadSceneCode(relative), ""]; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        { return ["", ex.Message]; }
    }

    public Task ProcessCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return RunAsync(() =>
        {
            if (!initialized || lua == null) throw new InvalidOperationException("Игра ещё не запущена.");
            InstallHook();
            using var function = lua["process_command"] as LuaFunction
                ?? throw new InvalidOperationException("Скрипт не объявляет process_command(command).");
            string before = FrameSignature(Story);
            using var invoke = (LuaFunction)lua["kne_invoke_command"];
            invoke.Call(command);
            if (!replaying) history.Add(new StoryAction("command", before, Command: command));
        }, cancellationToken, "Команда отладки");
    }

    public Task AdvanceAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!initialized || !Story.CanAdvance) throw new InvalidOperationException("Сейчас нельзя продолжить реплику или пропустить ожидание.");
        InstallHook();
        string before = FrameSignature(Story);
        ResumeStory(null);
        if (!replaying) history.Add(new StoryAction("advance", before));
    }, cancellationToken, "Продолжение сценария");

    public Task ChooseAsync(int option, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!initialized || Story.Kind != "choice") throw new InvalidOperationException("Сценарий не ожидает выбора.");
        if (option < 1 || option > Story.Options.Count) throw new ArgumentOutOfRangeException(nameof(option));
        if (!IsChoiceEnabled(Story, option)) throw new InvalidOperationException("Этот вариант выбора заблокирован.");
        InstallHook();
        string before = FrameSignature(Story);
        ResumeStory(option);
        if (!replaying) history.Add(new StoryAction("choose", before, option));
    }, cancellationToken, "Выбор ответа");

    public Task TickAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        return RunAsync(() =>
        {
            if (!initialized || !Story.NeedsTick) return;
            InstallHook();
            string before = FrameSignature(Story);
            ResumeStory(null, seconds, tick: true);
            if (!replaying) history.Add(new StoryAction("tick", before, Command: seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        }, cancellationToken, "Фоновая ветка сценария");
    }

    private void ResumeStory(object? answer, double seconds = 0, bool tick = false, string? signal = null)
    {
        using var resume = (LuaFunction)lua!["kne_resume"];
        object[] result = resume.Call(answer, seconds, tick, signal);
        StoryFrame frame;
        if ((string)result[0] == "dead")
        {
            using var ending = result.Length > 1 ? result[1] as LuaTable : null;
            frame = new StoryFrame("completed", "", "История завершена.", Array.Empty<string>())
            { EndAction = ending?["action"] as string, EndScreen = ending?["screen"] as string };
        }
        else
        {
            using var value = result.Length > 1 ? result[1] as LuaTable : null;
            if (value == null) throw new InvalidDataException("Сцена приостановлена без реплики или выбора.");
            string kind = value["kind"] as string ?? "";
            if (kind is not ("dialogue" or "choice" or "input" or "preview" or "wait" or "text_pause" or "clear_text")) throw new InvalidDataException("Неизвестное событие сцены: " + kind);
            lock (stateLock) location = new PreviewLocation(value["file"] as string ?? "", Convert.ToInt32(value["line"] ?? 0),
                value["node"] as string, value["functionline"] == null ? null : Convert.ToInt32(value["functionline"]));
            var options = new List<string>();
            if (kind == "choice")
            {
                using var table = (LuaTable)value["options"];
                for (int i = 1; i <= 20 && table[i] is string label; i++) options.Add(label);
            }
            frame = kind == "preview" ? Story with { Kind = "preview", Options = Array.Empty<string>() } :
                new StoryFrame(kind, value["speaker"] as string ?? "", value["text"] as string ?? "", options.AsReadOnly());
            if (kind != "preview") frame = ReadNarrativeFrame(frame, value);
            frame = ReadInputFrame(frame, value);
            frame = ReadChoiceFrame(frame, value);
            frame = frame with { Triggers = ReadTriggers(value), HasBackgroundTasks = value["background"] as bool? == true,
                Identity = frames?.ResolveKey(value["key"] as string, Location!.File, Location.Line, Location.FunctionLine) ??
                frames?.Resolve(Location!.File, Location.Line, Location.NodeId, Location.FunctionLine) };
            if (kind is "dialogue" or "choice" && !tick) PrintToUI(frame.Speaker.Length == 0 ? frame.Text : frame.Speaker + ": " + frame.Text);
        }
        lock (stateLock) story = frame;
    }

    public Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default) => CapturePluginCheckpointAsync(true, cancellationToken);

    internal async Task<GameCheckpoint> CapturePluginCheckpointAsync(bool saving, CancellationToken cancellationToken = default)
    {
        GameCheckpoint? checkpoint = null;
        await RunAsync(() =>
        {
            if (!initialized || !interactive) throw new InvalidOperationException("Нет запущенной интерактивной сцены.");
            if (history.Count > 10_000) throw new InvalidDataException("История игры слишком длинная для сохранения.");
            using var table = lua!["state"] as LuaTable ?? throw new InvalidDataException("state должен оставаться таблицей.");
            if (saving) PublishPluginEvent("on_save");
            JsonElement state = JsonSerializer.SerializeToElement(ReadState(table, 0));
            checkpoint = new GameCheckpoint(1, project.Path, fingerprint, seed, history.ToArray(), Story, state);
        }, cancellationToken).ConfigureAwait(false);
        return checkpoint!;
    }

    public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (checkpoint.Version != 1 || !project.Path.Equals(checkpoint.ProjectPath, StringComparison.OrdinalIgnoreCase) ||
            checkpoint.Fingerprint != ComputeFingerprint() || checkpoint.History == null || checkpoint.History.Count > 10_000 ||
            checkpoint.State.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Сохранение относится к другому проекту или сценарии изменились.");
        replaying = true;
        initialized = false;
        seed = checkpoint.Seed;
        history.Clear();
        lock (stateLock) { visuals = SceneVisuals.Empty; story = StoryFrame.Empty; }
        try
        {
            StartCore();
            foreach (var action in checkpoint.History)
            {
                executionToken.ThrowIfCancellationRequested();
                if (ShouldStop()) throw new TimeoutException("Превышено время восстановления игры.");
                if (action.ExpectedFrame != FrameSignature(Story)) throw new InvalidDataException("Сценарий изменился или недетерминирован: позиция сохранения не совпадает.");
                InstallHook();
                switch (action.Kind)
                {
                    case "plugin_input" when action.Command != null:
                        var pluginInput = JsonSerializer.Deserialize<PluginInput>(action.Command) ?? throw new InvalidDataException("Пустое событие плагина.");
                        PublishPluginInputEvent(pluginInput);
                        pluginHost!.Api.Input(pluginInput); break;
                    case "plugin_event_input" when action.Command != null:
                        PublishPluginInputEvent(JsonSerializer.Deserialize<PluginInput>(action.Command) ?? throw new InvalidDataException("Пустое событие плагина.")); break;
                    case "advance" when Story.CanAdvance: ResumeStory(null); break;
                    case "choose" when Story.Kind == "choice" && action.Option >= 1 && action.Option <= Story.Options.Count && IsChoiceEnabled(Story, action.Option): ResumeStory(action.Option); break;
                    case "input" when Story.Kind == "input" && action.Command != null: SubmitInputCore(action.Command, action.Cancel); break;
                    case "command" when !string.IsNullOrWhiteSpace(action.Command):
                        using (var command = lua!["kne_invoke_command"] as LuaFunction) command!.Call(action.Command);
                        break;
                    case "trigger" when action.Command != null:
                        var signal = JsonSerializer.Deserialize<TriggerSignal>(action.Command, EngineSettings.JsonOptions) ?? throw new InvalidDataException("Пустое событие сохранения.");
                        signal.Validate();
                        if (!Story.Triggers.Any(trigger => trigger.Matches(signal))) throw new InvalidDataException("Сохранение содержит событие без активного триггера.");
                        ResumeStory(null, signal: action.Command); break;
                    case "tick" when Story.NeedsTick && double.TryParse(action.Command, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0 && seconds <= 60:
                        ResumeStory(null, seconds, tick: true); break;
                    default: throw new InvalidDataException("Некорректная последовательность действий в сохранении.");
                }
            }
            if (FrameSignature(Story) != FrameSignature(checkpoint.Frame)) throw new InvalidDataException("Реплика сохранения не совпадает со сценарием.");
            lua!.DoString("for k in pairs(state) do state[k] = nil end; local restored = " + StateLiteral(checkpoint.State) + "; for k,v in pairs(restored) do state[k] = v end", "KNE restore state");
            history.AddRange(checkpoint.History);
            lock (stateLock) visuals = visuals with
            {
                Sound = null,
                AudioChannels = visuals.AudioChannels.Select(channel => channel.Channel is "sfx" or "voice" && !channel.Loop ? channel with { Path = null } : channel).ToArray()
            };
        }
        finally { replaying = false; }
        if (pluginHost != null) { using var context = new PluginContext(this); pluginHost.Api.RestoreScreens(context); }
        PublishPluginEvent("on_load");
    }, cancellationToken, "Загрузка сохранения");

    private string ComputeFingerprint(GameScripts? program = null)
    {
        program ??= GameScripts.Load(project, pluginsEnabled: pluginsEnabled);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string entry = preview ? program.PreviewFunction(Path.GetRelativePath(project.Path, project.MainScenePath)) : "main";
        hash.AppendData(Encoding.UTF8.GetBytes("functions-v1:" + entry));
        if (!pluginsEnabled) hash.AppendData(Encoding.UTF8.GetBytes("plugins-disabled"));
        // Include the resolved scene identity: previewing another scene must not accept its sibling's saves.
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(project.Path, project.MainScenePath)));
        byte[] hashBuffer = new byte[65536];
        foreach (string relative in program.Files.Select(f => f.Path).Concat(ProjectLoader.EnumerateFiles(project.Path, CancellationToken.None).Files
                     .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".kneplugin.json", StringComparison.OrdinalIgnoreCase)).Select(path => Path.GetRelativePath(project.Path, path).Replace('\\', '/')))
                     .Append("project.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string path = PathSafety.ResolveRelative(project.Path, relative);
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(project.Path, path)));
            using var source = File.OpenRead(path); int read;
            while ((read = source.Read(hashBuffer)) != 0) hash.AppendData(hashBuffer.AsSpan(0, read));
        }
        // Presentation changes should not invalidate a player's progress; initial game state should.
        string statePath = PathSafety.ResolveRelative(project.Path, "engine/state.json");
        if (File.Exists(statePath))
        {
            hash.AppendData(Encoding.UTF8.GetBytes("engine/state.json"));
            hash.AppendData(File.ReadAllBytes(statePath));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Presentation identities may be assigned/reused after an edit; version-1 saves compare the original visible fields.
    private static string FrameSignature(StoryFrame frame)
    {
        string original = JsonSerializer.Serialize(new { frame.Kind, frame.Speaker, frame.Text, frame.Options, frame.Completed });
        if (frame.Character is null && frame.TextEffect is null && frame.RemainingSeconds is null && frame.IsSkippable && frame.EraseProgress == 0 && frame.Input is null && frame.ChoiceOptions is null && frame.ChoiceTimer is null) return original;
        string narrative = original + JsonSerializer.Serialize(new { frame.Character, frame.TextEffect, frame.RemainingSeconds, frame.IsSkippable, frame.EraseProgress });
        if (frame.Input != null) narrative += JsonSerializer.Serialize(new { frame.Input, frame.InputFeedback });
        return frame.ChoiceOptions is null && frame.ChoiceTimer is null ? narrative : narrative + JsonSerializer.Serialize(new { frame.ChoiceOptions, frame.ChoiceTimer });
    }

    private static object ReadState(LuaTable table, int depth, bool allowArray = false)
    {
        if (depth > 16) throw new InvalidDataException("state слишком глубокий или содержит цикл.");
        var map = new Dictionary<string, object?>();
        var array = new SortedDictionary<int, object?>();
        foreach (KeyValuePair<object, object> entry in table)
        {
            object? value;
            if (entry.Value is LuaTable nested)
            {
                using (nested) value = ReadState(nested, depth + 1, allowArray: true);
            }
            else if (entry.Value is null or string or bool or long or int or double) value = entry.Value;
            else throw new InvalidDataException("state может содержать только числа, строки, логические значения и вложенные таблицы.");
            if (entry.Key is string key) map[key] = value;
            else if (allowArray && entry.Key is double or long or int)
            {
                double index = Convert.ToDouble(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsFinite(index) || index != Math.Truncate(index) || index < 1 || index > 10_000)
                    throw new InvalidDataException("Массив state должен иметь последовательные индексы от 1 до 10000.");
                array[(int)index] = value;
            }
            else throw new InvalidDataException("Ключи state должны быть строками; во вложенном массиве — последовательными числами от 1.");
            if (map.Count + array.Count > 10_000) throw new InvalidDataException("state слишком большой.");
        }
        if (array.Count > 0)
        {
            if (map.Count > 0 || array.Keys.Last() != array.Count)
                throw new InvalidDataException("state не поддерживает смешанные таблицы или массивы с пропущенными индексами.");
            return array.Values.ToArray();
        }
        return map;
    }

    private static string StateLiteral(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(p => "[" + LuaQuote(p.Name) + "]=" + StateLiteral(p.Value))) + "}",
        JsonValueKind.Array => "{" + string.Join(",", element.EnumerateArray().Select(StateLiteral)) + "}",
        JsonValueKind.String => LuaQuote(element.GetString()!),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "nil",
        _ => throw new InvalidDataException("Недопустимое значение state.")
    };

    private static string LuaQuote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (char character in text)
        {
            if (character is '\\' or '"') builder.Append('\\').Append(character);
            else if (character < 32) builder.Append('\\').Append(((int)character).ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            else builder.Append(character);
        }
        return builder.Append('"').ToString();
    }

    private async Task RunAsync(Action action, CancellationToken cancellationToken, string operation = "Выполнение сценария")
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (stateLock)
            {
                ObjectDisposedException.ThrowIf(stopping, this);
                executionToken = linked.Token;
                deadline = Stopwatch.GetTimestamp() + (long)(ExecutionLimit.TotalSeconds * Stopwatch.Frequency);
                scriptFailure = null;
                pluginFailureCaptured = false;
                currentScriptFrame = null;
            }
            await Task.Run(() =>
            {
                action();
                if (lua != null) lua.DoString("kne_publish_variables()", "KNE variable snapshot");
            }, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Превышено время выполнения сценария.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ShouldStop())
        {
            if (linked.IsCancellationRequested) throw new OperationCanceledException("Выполнение остановлено.", ex, linked.Token);
            var timeout = new TimeoutException("Превышено время выполнения сценария.", ex);
            AttachScriptReport(timeout, operation);
            throw timeout;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (ex is NLua.Exceptions.LuaScriptException || scriptFailure != null))
        {
            AttachScriptReport(ex, operation);
            throw;
        }
        finally { gate.Release(); }
    }

    private void Register(string method, string name)
    {
        using var function = lua!.RegisterFunction(name, this, GetType().GetMethod(method)!);
    }

    private void AttachScriptReport(Exception error, string operation)
    {
        var report = GameErrorReport.Create(project, error, operation);
        report = report with { TransitionFailure = transitionPending };
        if (scriptFailure is { } failure)
            report = report with { File = failure.File, Line = failure.Line, Details = failure.Trace + "\n\n" + error,
                Frame = failure.Frame ?? frames?.Resolve(failure.File, failure.Line) };
        GameErrorReport.Attach(error, report);
    }

    /// <summary>Captures the original failing Lua frame before coroutine unwinding loses its stack.</summary>
    public void RecordScriptError(string source, int line, string trace, string problem)
    {
        if (pluginFailureCaptured && scriptFailure is { } pluginFailure && problem.EndsWith(pluginFailure.Problem, StringComparison.Ordinal)) return;
        pluginFailureCaptured = false;
        if (scriptFailure is { } previous && previous.Problem == problem) return;
        string? file = string.IsNullOrEmpty(source) ? null : source.Replace('\\', '/');
        if (file != null && Path.IsPathRooted(file))
        {
            string relative = Path.GetRelativePath(project.Path, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal)) file = relative.Replace('\\', '/');
        }
        scriptFailure = (file, line > 0 ? line : null, trace, problem, currentScriptFrame);
    }

    /// <summary>Captures the actual failing coroutine's command before Lua unwinds it.</summary>
    public void RecordScriptContext(string file, int line, int functionLine, string node, string key) =>
        currentScriptFrame = frames?.ResolveKey(key, file, line, functionLine) ?? frames?.Resolve(file, line, node.Length == 0 ? null : node, functionLine);

    /// <summary>Returns managed API validation failures as data so Lua raises them in the calling script frame.</summary>
    public string InvokeScriptApi(string name, LuaTable arguments)
    {
        using var ownedArguments = arguments;
        string? Text(int index, bool optional = false)
        {
            object? value = arguments[index];
            if (value == null && optional) return null;
            return value as string ?? throw new ArgumentException(name + ": аргумент " + index + " должен быть строкой.");
        }
        int Number(int index)
        {
            object? value = arguments[index];
            if (value is not (double or long or int)) throw new ArgumentException(name + ": аргумент " + index + " должен быть целым числом.");
            double number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(number) || number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
                throw new ArgumentException(name + ": аргумент " + index + " должен быть целым числом.");
            return (int)number;
        }
        try
        {
            switch (name)
            {
                case "apply_shader": SetShader(Text(1)!, arguments[2] is not false, Text(3, true), Text(4, true), Text(5, true) ?? "startup"); break;
                case "clear_layer_shaders": RemoveShaders(Text(1)!, Text(2)!, arguments[3] is true, Text(4, true) ?? "startup"); break;
                case "clear_shaders": RemoveShaders(null, "", arguments[1] is true, Text(2, true) ?? "startup"); break;
                case "set_window_title": SetWindowTitle(Text(1)!); break;
                case "set_window_size": SetWindowSize(Number(1), Number(2)); break;
                case "set_bg_color": SetBgColor(Text(1)!); break;
                case "background": SetBackground(Text(1, optional: true)); break;
                case "show_character": ShowCharacter(Text(1)!, Text(2, optional: true) ?? "center"); break;
                case "hide_character": HideCharacter(); break;
                case "play_music":
                    object? loop = arguments[2];
                    if (loop is not (null or bool)) throw new ArgumentException("play_music: второй аргумент должен быть true/false.");
                    PlayMusic(Text(1)!, loop as bool? ?? true); break;
                case "stop_music": StopMusic(); break;
                case "play_sound": PlaySound(Text(1)!); break;
                default:
                    if (TryInvokeUi(name, arguments, out string uiProblem)) return uiProblem;
                    if (!TryInvokeEffect(name, arguments, out string problem)) throw new ArgumentException("Неизвестная команда игры: " + name);
                    return problem;
            }
            return "";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or FormatException or UnauthorizedAccessException)
        { return name + ": " + ex.Message; }
    }

    private void InstallHook() => lua!.DoString("""
        do
            local stop, fail = kne_should_stop, error
            local function hook()
                if stop() then fail("KNE_EXECUTION_CANCELLED", 0) end
                if kne_should_publish_variables() then kne_publish_variables(coroutine.running()) end
            end
            debug.sethook(hook, "", 10000)
            if kne_story and coroutine.status(kne_story) ~= "dead" then
                debug.sethook(kne_story, hook, "", 10000)
            end
        end
        """, "KNE execution limit");

    public bool ShouldStop() => executionToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline;
    public bool ShouldPublishVariables()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < nextVariablesAt) return false;
        nextVariablesAt = now + Stopwatch.Frequency / 10;
        return true;
    }
    public void TransitionPending(bool pending)
    {
        JsonElement value;
        lock (stateLock)
        {
            if (transitionPending == pending) return;
            transitionPending = pending;
            value = variables;
        }
        if (!replaying) variablesUpdated?.Invoke(value);
    }
    public bool ErrorSnapshotNeeded(string problem) => !(pluginFailureCaptured && scriptFailure is { } failure && problem.EndsWith(failure.Problem, StringComparison.Ordinal)) && (scriptFailure == null || scriptFailure.Value.Problem != problem);

    /// <summary>Validates an engine-owned end screen without changing the visible scene.</summary>
    public string ValidateEndAction(string action, string? screen)
    {
        if (action != "screen") return "";
        try { project.Engine.Screens.ResolveReference(screen ?? ""); return ""; }
        catch (ArgumentException error) { return error.Message; }
    }

    public void ReceiveVariables(LuaTable snapshot)
    {
        using (snapshot)
        {
            var value = JsonSerializer.SerializeToElement(ReadDiagnostics(snapshot));
            lock (stateLock) variables = value;
            if (!replaying) variablesUpdated?.Invoke(value);
        }
    }

    private static Dictionary<string, object?> ReadDiagnostics(LuaTable table)
    {
        var result = new Dictionary<string, object?>();
        foreach (KeyValuePair<object, object> entry in table)
        {
            if (entry.Key is not string key) continue;
            if (entry.Value is LuaTable nested)
            {
                using (nested) result[key] = ReadDiagnostics(nested);
            }
            else if (entry.Value is null or bool or string or long or int or double) result[key] = entry.Value;
        }
        return result;
    }
    public void PrintToUI(string text) { if (!replaying) output(text); }

    public void SetWindowTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        lock (stateLock) windowSettings = windowSettings with { Title = title };
    }

    public void SetBackground(string? relative)
    {
        string? path = relative == null ? null : ResourcePaths.Resolve(project.Path, relative);
        lock (stateLock) visuals = visuals with { Background = path, BackgroundsCleared = false };
    }

    public void ShowCharacter(string relative, string position = "center")
    {
        if (position is not ("left" or "center" or "right")) throw new ArgumentException("Позиция: left, center или right.", nameof(position));
        string path = ResourcePaths.Resolve(project.Path, relative);
        lock (stateLock) visuals = visuals with { Character = path, Position = position };
    }

    public void HideCharacter() { lock (stateLock) visuals = visuals with { Character = null }; }

    public void PlayMusic(string relative, bool loop = true)
    {
        string path = ResourcePaths.Resolve(project.Path, relative, audio: true);
        lock (stateLock) visuals = visuals with { Music = path, LoopMusic = loop, MusicVersion = visuals.MusicVersion + 1 };
        UpdateAudio("music", current => current with { Path = path, Loop = loop, Version = current.Version + 1 });
    }

    public void StopMusic()
    {
        lock (stateLock) visuals = visuals with { Music = null, MusicVersion = visuals.MusicVersion + 1 };
        UpdateAudio("music", current => current with { Path = null, Version = current.Version + 1 });
    }

    public void PlaySound(string relative)
    {
        string path = ResourcePaths.Resolve(project.Path, relative, audio: true);
        lock (stateLock) visuals = visuals with { Sound = path, SoundVersion = visuals.SoundVersion + 1 };
        UpdateAudio("sfx", current => current with { Path = path, Loop = false, Version = current.Version + 1 });
    }

    public void SetWindowSize(int width, int height)
    {
        if (width is < 320 or > 7680 || height is < 240 or > 4320)
            throw new ArgumentOutOfRangeException(nameof(width), "Размер окна должен быть от 320×240 до 7680×4320.");
        lock (stateLock) windowSettings = windowSettings with { Width = width, Height = height };
    }

    public void SetBgColor(string color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);
        // Color conversion takes place on the UI thread; validate here as well.
        _ = System.Windows.Media.ColorConverter.ConvertFromString(color)
            ?? throw new ArgumentException("Некорректный цвет.", nameof(color));
        lock (stateLock) windowSettings = windowSettings with { Background = color };
    }

    public ValueTask DisposeAsync()
    {
        lock (stateLock)
        {
            if (disposal != null) return new ValueTask(disposal);
            stopping = true;
            lifetime.Cancel();
            disposal = DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            DisposePlugins();
            lua?.Dispose();
            lua = null;
            lifetime.Dispose();
        }
        finally { gate.Release(); }
    }
}

public sealed record RuntimeWindowSettings(string Title, int Width, int Height, string Background);
