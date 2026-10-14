using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using NLua;

namespace KNE;

/// <summary>Owns a Lua state used only by serialized background operations.</summary>
public sealed class LuaRuntime : IGameSession
{
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
    private StoryFrame story = StoryFrame.Empty;
    private SceneVisuals visuals = SceneVisuals.Empty;
    private readonly List<StoryAction> history = [];
    private bool replaying;
    private int seed = Random.Shared.Next(1, int.MaxValue);
    private string fingerprint = "";
    private GameScripts? scripts;
    private readonly bool preview;
    public TimeSpan ExecutionLimit { get; }

    public LuaRuntime(ProjectInfo project, Action<string> output, TimeSpan? executionLimit = null, bool interactive = false, bool preview = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(output);
        this.project = project;
        this.output = output;
        this.interactive = interactive;
        this.preview = preview;
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

    public Task StartAsync(CancellationToken cancellationToken = default) => RunAsync(StartCore, cancellationToken);

    private void StartCore()
    {
        if (initialized) throw new InvalidOperationException("Игра уже запущена.");
        scripts = GameScripts.Load(project);
        lua?.Dispose();
        lua = new Lua();
        lua.State.Encoding = Encoding.UTF8;
        lock (stateLock) windowSettings = project.Engine.RuntimeDefaults(project.Title);
        Register(nameof(PrintToUI), "print_to_ui");
        Register(nameof(SetWindowTitle), "set_window_title");
        Register(nameof(SetWindowSize), "set_window_size");
        Register(nameof(SetBgColor), "set_bg_color");
        Register(nameof(ShouldStop), "kne_should_stop");
        Register(nameof(SetBackground), "background");
        Register(nameof(ShowCharacter), "show_character");
        Register(nameof(HideCharacter), "hide_character");
        Register(nameof(PlayMusic), "play_music");
        Register(nameof(StopMusic), "stop_music");
        Register(nameof(PlaySound), "play_sound");
        Register(nameof(SceneCodeForLua), "kne_scene_code_result");
        lua["project_dir"] = project.Path;
        lua["kne_interactive"] = interactive;
        lua["kne_seed"] = seed;
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
            function d(speaker, text)
                if kne_interactive and coroutine.isyieldable() then
                    return coroutine.yield({kind = "dialogue", speaker = speaker == nil and "" or tostring(speaker), text = tostring(text)})
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
                return coroutine.yield({kind = "choice", text = tostring(prompt), options = labels})
            end
            function kne_resume(answer)
                local ok, event = coroutine.resume(kne_story, answer)
                if not ok then error(event) end
                return coroutine.status(kne_story), event
            end
            """, "KNE API");
        lua.DoString(SceneRuntimeLua.Source, "KNE scene scheduler");
        // Resolve all paths before executing any project code.
        lua.DoString("state = " + StateLiteral(project.Engine.InitialState), "KNE initial state");
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
        string entryName = preview ? scripts.PreviewFunction(Path.GetRelativePath(project.Path, scenePath)) : "main";
        lua["kne_entry"] = lua[entryName];
        using var main = lua["kne_entry"] as LuaFunction ?? throw new InvalidDataException("Не найдена игровая функция: " + entryName);
        if (interactive)
        {
            lua.DoString("kne_story = kne_scene_thread(function() kne_run_scene(kne_entry) end)", "KNE scene");
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
        if (scripts != null) return scripts.FindFile(relative).ExecutableSource;
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
            function.Call(command);
            if (!replaying) history.Add(new StoryAction("command", before, Command: command));
        }, cancellationToken);
    }

    public Task AdvanceAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!initialized || Story.Kind != "dialogue") throw new InvalidOperationException("Нет реплики для продолжения.");
        InstallHook();
        string before = FrameSignature(Story);
        ResumeStory(null);
        if (!replaying) history.Add(new StoryAction("advance", before));
    }, cancellationToken);

    public Task ChooseAsync(int option, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!initialized || Story.Kind != "choice") throw new InvalidOperationException("Сценарий не ожидает выбора.");
        if (option < 1 || option > Story.Options.Count) throw new ArgumentOutOfRangeException(nameof(option));
        InstallHook();
        string before = FrameSignature(Story);
        ResumeStory(option);
        if (!replaying) history.Add(new StoryAction("choose", before, option));
    }, cancellationToken);

    private void ResumeStory(object? answer)
    {
        using var resume = (LuaFunction)lua!["kne_resume"];
        object[] result = resume.Call(answer);
        StoryFrame frame;
        if ((string)result[0] == "dead") frame = new StoryFrame("completed", "", "История завершена.", Array.Empty<string>());
        else
        {
            using var value = result.Length > 1 ? result[1] as LuaTable : null;
            if (value == null) throw new InvalidDataException("Сцена приостановлена без реплики или выбора.");
            string kind = value["kind"] as string ?? "";
            if (kind is not ("dialogue" or "choice")) throw new InvalidDataException("Неизвестное событие сцены: " + kind);
            var options = new List<string>();
            if (kind == "choice")
            {
                using var table = (LuaTable)value["options"];
                for (int i = 1; i <= 20 && table[i] is string label; i++) options.Add(label);
            }
            frame = new StoryFrame(kind, value["speaker"] as string ?? "", value["text"] as string ?? "", options.AsReadOnly());
            PrintToUI(frame.Speaker.Length == 0 ? frame.Text : frame.Speaker + ": " + frame.Text);
        }
        lock (stateLock) story = frame;
    }

    public async Task<GameCheckpoint> GetCheckpointAsync(CancellationToken cancellationToken = default)
    {
        GameCheckpoint? checkpoint = null;
        await RunAsync(() =>
        {
            if (!initialized || !interactive) throw new InvalidOperationException("Нет запущенной интерактивной сцены.");
            if (history.Count > 10_000) throw new InvalidDataException("История игры слишком длинная для сохранения.");
            using var table = lua!["state"] as LuaTable ?? throw new InvalidDataException("state должен оставаться таблицей.");
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
                    case "advance" when Story.Kind == "dialogue": ResumeStory(null); break;
                    case "choose" when Story.Kind == "choice" && action.Option >= 1 && action.Option <= Story.Options.Count: ResumeStory(action.Option); break;
                    case "command" when !string.IsNullOrWhiteSpace(action.Command):
                        using (var command = lua!["process_command"] as LuaFunction ?? throw new InvalidDataException("Нет обработчика команд.")) command.Call(action.Command);
                        break;
                    default: throw new InvalidDataException("Некорректная последовательность действий в сохранении.");
                }
            }
            if (FrameSignature(Story) != FrameSignature(checkpoint.Frame)) throw new InvalidDataException("Реплика сохранения не совпадает со сценарием.");
            lua!.DoString("for k in pairs(state) do state[k] = nil end; local restored = " + StateLiteral(checkpoint.State) + "; for k,v in pairs(restored) do state[k] = v end", "KNE restore state");
            history.AddRange(checkpoint.History);
            lock (stateLock) visuals = visuals with { Sound = null };
        }
        finally { replaying = false; }
    }, cancellationToken);

    private string ComputeFingerprint(GameScripts? program = null)
    {
        program ??= GameScripts.Load(project);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        string entry = preview ? program.PreviewFunction(Path.GetRelativePath(project.Path, project.MainScenePath)) : "main";
        hash.AppendData(Encoding.UTF8.GetBytes("functions-v1:" + entry));
        // Include the resolved scene identity: previewing another scene must not accept its sibling's saves.
        hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(project.Path, project.MainScenePath)));
        foreach (string relative in program.Files.Select(f => f.Path).Append("project.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string path = PathSafety.ResolveRelative(project.Path, relative);
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(project.Path, path)));
            hash.AppendData(File.ReadAllBytes(path));
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

    private static string FrameSignature(StoryFrame frame) => JsonSerializer.Serialize(frame);

    private static object ReadState(LuaTable table, int depth)
    {
        if (depth > 16) throw new InvalidDataException("state слишком глубокий или содержит цикл.");
        var map = new Dictionary<string, object?>();
        foreach (KeyValuePair<object, object> entry in table)
        {
            if (entry.Key is not string key) throw new InvalidDataException("Ключи state должны быть строками.");
            if (entry.Value is LuaTable nested)
            {
                using (nested) map[key] = ReadState(nested, depth + 1);
            }
            else if (entry.Value is null or string or bool or long or int or double) map[key] = entry.Value;
            else throw new InvalidDataException("state может содержать только числа, строки, логические значения и вложенные таблицы.");
            if (map.Count > 10_000) throw new InvalidDataException("state слишком большой.");
        }
        return map;
    }

    private static string StateLiteral(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(p => "[" + LuaQuote(p.Name) + "]=" + StateLiteral(p.Value))) + "}",
        JsonValueKind.String => LuaQuote(element.GetString()!),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "nil",
        _ => throw new InvalidDataException("В state не поддерживаются массивы.")
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

    private async Task RunAsync(Action action, CancellationToken cancellationToken)
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
            }
            await Task.Run(action, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() >= deadline) throw new TimeoutException("Превышено время выполнения сценария.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ShouldStop())
        {
            if (linked.IsCancellationRequested) throw new OperationCanceledException("Выполнение остановлено.", ex, linked.Token);
            throw new TimeoutException("Превышено время выполнения сценария.", ex);
        }
        finally { gate.Release(); }
    }

    private void Register(string method, string name)
    {
        using var function = lua!.RegisterFunction(name, this, GetType().GetMethod(method)!);
    }

    private void InstallHook() => lua!.DoString("""
        do
            local stop, fail = kne_should_stop, error
            debug.sethook(function()
                if stop() then fail("KNE_EXECUTION_CANCELLED", 0) end
            end, "", 10000)
            if kne_story and coroutine.status(kne_story) ~= "dead" then
                debug.sethook(kne_story, function()
                    if stop() then fail("KNE_EXECUTION_CANCELLED", 0) end
                end, "", 10000)
            end
        end
        """, "KNE execution limit");

    public bool ShouldStop() => executionToken.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline;
    public void PrintToUI(string text) { if (!replaying) output(text); }

    public void SetWindowTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        lock (stateLock) windowSettings = windowSettings with { Title = title };
    }

    public void SetBackground(string? relative)
    {
        string? path = relative == null ? null : ResourcePaths.Resolve(project.Path, relative);
        lock (stateLock) visuals = visuals with { Background = path };
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
    }

    public void StopMusic() { lock (stateLock) visuals = visuals with { Music = null, MusicVersion = visuals.MusicVersion + 1 }; }

    public void PlaySound(string relative)
    {
        string path = ResourcePaths.Resolve(project.Path, relative, audio: true);
        lock (stateLock) visuals = visuals with { Sound = path, SoundVersion = visuals.SoundVersion + 1 };
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
            lua?.Dispose();
            lua = null;
            lifetime.Dispose();
        }
        finally { gate.Release(); }
    }
}

public sealed record RuntimeWindowSettings(string Title, int Width, int Height, string Background);
