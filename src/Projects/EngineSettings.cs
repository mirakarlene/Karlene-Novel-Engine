using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace KNE;

/// <summary>Editable project-owned presentation and runtime defaults from engine/*.json.</summary>
public sealed class EngineSettings
{
    public WindowDefaults Window { get; init; } = new();
    public GameTheme Theme { get; set; } = new();
    public ScreenLayouts Screens { get; set; } = ScreenLayouts.CreateDefaults();
    public GameDefaults Game { get; init; } = new();
    public ProjectPerformanceSettings Performance { get; init; } = new();
    public ScenePerformanceDocument ScenePerformance { get; init; } = new();
    public JsonElement InitialState { get; init; } = JsonSerializer.SerializeToElement(new Dictionary<string, object>());
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>Loads validated settings; missing files retain compatible defaults.</summary>
    public static EngineSettings Load(string root)
    {
        var settings = new EngineSettings
        {
            Window = Read<WindowDefaults>(root, "window.json"),
            Theme = Read<GameTheme>(root, "theme.json"),
            Game = Read<GameDefaults>(root, "game.json"),
            Performance = PerformanceSettingsStore.LoadProject(root),
            ScenePerformance = PerformanceSettingsStore.LoadScenes(root),
            InitialState = Read<JsonElement>(root, "state.json", JsonSerializer.SerializeToElement(new Dictionary<string, object>()))
        };
        string screensPath = PathSafety.ResolveRelative(root, "engine/screens.json");
        if (File.Exists(screensPath) && new FileInfo(screensPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Оформление больше 2 МБ.");
        settings.Screens = File.Exists(screensPath) ? ScreenLayouts.Parse(File.ReadAllText(screensPath)) : ScreenLayouts.CreateDefaults(settings.Theme);
        settings.Theme = settings.Screens.Theme ?? settings.Theme;
        foreach (var e in settings.Screens.Screens.SelectMany(s => s.Elements))
            if (e.Type == "image") _ = ResourcePaths.Resolve(root, e.Image ?? "");
        settings.Validate(root);
        return settings;
    }

    /// <summary>Creates a complete editable engine template inside the new project's staging directory.</summary>
    public static void WriteTemplate(string root)
    {
        string directory = PathSafety.ResolveRelative(root, "engine");
        Directory.CreateDirectory(directory);
        var settings = new EngineSettings();
        foreach (var item in new (string Name, object Value)[]
        {
            ("window.json", settings.Window), ("theme.json", settings.Theme),
            ("game.json", settings.Game), ("state.json", settings.InitialState), ("screens.json", settings.Screens),
            ("performance.json", settings.Performance), ("scene-performance.json", settings.ScenePerformance)
        })
            File.WriteAllText(Path.Combine(directory, item.Name), JsonSerializer.Serialize(item.Value, JsonOptions));
        File.WriteAllText(Path.Combine(directory, "README.txt"), """
            engine — настройки игры. Изменения применяются при следующем запуске окна игры.
            Кнопка «Оформление» визуально редактирует engine/screens.json: экраны, элементы и тему.
            Секция theme в screens.json имеет приоритет над theme.json после визуального сохранения.
            window.json: width/height (640..7680 / 480..4320), position center или manual,
            left/top для manual (координаты экрана в DIP), mode windowed/maximized/fullscreen, resizable.
            theme.json: фон, узор, цвета, шрифт, кнопки, размер персонажа и окно диалога.
            menu: width, title_font_size, subtitle_font_size, alignment left/center/right, overlay_color.
            background_image: null или путь к PNG/JPG/BMP/GIF относительно проекта.
            Узор рисуется поверх background_color; pattern=false убирает узор.
            Диалог: height, margin, padding, corner_radius, font_size, speaker_font_size и цвета.
            game.json: main_menu, подписи кнопок, subtitle, auto_save, execution_timeout_seconds,
            debug_enabled (F3), начальный запуск и продолжение из quick/auto.
            state.json: начальные переменные state (объект; строки, числа, true/false, вложенные объекты и массивы).
            Затем gamelogic.lua может переопределить начальные переменные и настройки окна.
            Escape — меню паузы; F11 — полноэкранный режим; F3 — журнал и команды отладки.
            В fullscreen системная рамка скрыта; F11 возвращает оконный режим.
            Предпросмотр выбранной сцены пропускает начальное меню.
            performance.json: graphics (api auto/vulkan/opengl, v_sync, max_fps 30/60/144/0,
            msaa_samples 0/2/4, texture_quality original/half/quarter, limit_resource_cache true/false,
            resource_cache_limit_mb 16/32/64/128/256/512), fps_counter, fps_corner, statistics_overlay,
            loading_indicator, debug_mode, preloading, unload_previous_scene, max_async_tasks 32/64/128,
            audio_buffer low_latency/balanced/stable, saves_path. Кнопка «Параметры проекта» редактирует эти значения.
            texture_quality уменьшает размеры декодированных изображений по каждой оси, без изменения файлов.
            unload_previous_scene убирает из кэша ресурсы прошлой сцены, сохраняя показанные изображения.
            max_async_tasks включает основную ветку и применяется при новом запуске сценария.
            audio_buffer задаёт два буфера по 25/75/150 мс при следующем открытии аудиофайла.
            Название проекта меняется в project.json; папка проекта не переименовывается.
            Danger Zone: сброс сохранений или удаление папки проекта после подтверждения и 72 нажатий.
            scene-performance.json: scenes с путями .krl или .knegraph; effects_quality high/medium/low,
            post_processing, hide_shaders (не отображать шейдеры, по умолчанию false).
            Кнопка «Параметры сцены» редактирует отдельные сцены.
            .kneshader — отдельный граф значений для GPU; Texture задаёт цель, Fragment Output — итог.
            Корневые шейдеры применяются автоматически; подключённые контейнеры отдельно не запускаются.
            Изменение .kneshader применяется при новом открытии окна игры / предпросмотра.
            Сохранения находятся в saves или папке saves_path; смена пути не переносит старые файлы.
            Графический API применяется к слоям KNE; WPF-контролы используют системный рендерер.
            Изменение state.json отменяет совместимость старых сохранений; изменение темы — нет.
            """);
    }

    /// <summary>Returns initial settings shared by the UI and the isolated Lua worker.</summary>
    public RuntimeWindowSettings RuntimeDefaults(string title) => new(title, Window.Width, Window.Height, Theme.BackgroundColor);

    public static void ValidateAppearance(string root, GameTheme theme) => new EngineSettings { Theme = theme }.Validate(root);

    private static T Read<T>(string root, string name, T? fallback = default) where T : new()
    {
        string path = PathSafety.ResolveRelative(root, "engine/" + name);
        if (!File.Exists(path)) return fallback is null ? new T() : fallback;
        try
        {
            if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Файл настроек больше 256 КБ.");
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Настройки не должны быть null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { throw new InvalidDataException("Ошибка engine/" + name + ": " + ex.Message, ex); }
    }

    private void Validate(string root)
    {
        if (Window == null || Theme == null || Game == null || Theme.Dialogue == null || Theme.Menu == null)
            throw new InvalidDataException("Секции настроек engine не должны быть null.");
        Range(Window.Width, 640, 7680, "window.width"); Range(Window.Height, 480, 4320, "window.height");
        if (Window.Position is not ("center" or "manual") || Window.Mode is not ("windowed" or "maximized" or "fullscreen"))
            throw new InvalidDataException("engine/window.json: неизвестные position или mode.");
        Range(Window.Left, -32768, 32768, "window.left"); Range(Window.Top, -32768, 32768, "window.top");
        Range(Game.ExecutionTimeoutSeconds, 1, 3600, "game.execution_timeout_seconds");
        Range(Theme.CharacterWidth, 100, 2000, "theme.character_width");
        if (Theme.PatternStyle is not ("waves" or "ornament" or "grid" or "dots" or "none")) throw new InvalidDataException("Неизвестный узор оформления.");
        if (!ThemePalettes.Names.Contains(Theme.Palette)) throw new InvalidDataException("Неизвестная палитра оформления.");
        Range(Theme.AnimationSpeed, 0.1, 3, "theme.animation_speed");
        Range(Theme.WaveAmplitude, 0, 80, "theme.wave_amplitude");
        Range(Theme.Menu.Width, 240, 560, "menu.width");
        Range(Theme.Menu.TitleFontSize, 20, 72, "menu.title_font_size");
        Range(Theme.Menu.SubtitleFontSize, 12, 32, "menu.subtitle_font_size");
        if (Theme.Menu.Alignment is not ("left" or "center" or "right"))
            throw new InvalidDataException("engine/theme.json: menu.alignment должен быть left, center или right.");
        Text(Theme.FontFamily, "theme.font_family"); Text(Game.StartLabel, "game.start_label");
        Text(Game.ContinueLabel, "game.continue_label"); Text(Game.ExitLabel, "game.exit_label");
        Text(Game.Subtitle, "game.subtitle", allowEmpty: true);
        foreach (string color in new[] { Theme.BackgroundColor, Theme.PatternColor, Theme.AccentColor, Theme.TextColor,
            Theme.ButtonColor, Theme.Dialogue.BackgroundColor, Theme.Dialogue.BorderColor,
            Theme.Dialogue.TextColor, Theme.Dialogue.SpeakerColor, Theme.Menu.OverlayColor })
        {
            try { if (ColorConverter.ConvertFromString(color ?? "") is not Color) throw new FormatException("Пустой цвет."); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException)
            { throw new InvalidDataException("engine/theme.json: некорректный цвет «" + color + "».", ex); }
        }
        var dialogue = Theme.Dialogue;
        Range(dialogue.Height, 120, 600, "dialogue.height"); Range(dialogue.Margin, 0, 100, "dialogue.margin");
        Range(dialogue.Padding, 0, 80, "dialogue.padding"); Range(dialogue.CornerRadius, 0, 80, "dialogue.corner_radius");
        Range(dialogue.FontSize, 12, 60, "dialogue.font_size"); Range(dialogue.SpeakerFontSize, 12, 60, "dialogue.speaker_font_size");
        if (dialogue.Height < 2 * dialogue.Padding + 100)
            throw new InvalidDataException("engine/theme.json: dialogue.height должен оставлять минимум 100 пикселей после padding.");
        if (Theme.BackgroundImage != null) _ = ResourcePaths.Resolve(root, Theme.BackgroundImage);
        ValidateState(InitialState, 0);
        if (InitialState.ValueKind != JsonValueKind.Object) throw new InvalidDataException("engine/state.json должен содержать объект.");
    }

    private static void ValidateState(JsonElement value, int depth)
    {
        if (depth > 16) throw new InvalidDataException("engine/state.json: глубина больше 16.");
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new InvalidDataException("engine/state.json: повтор ключа «" + property.Name + "».");
                    ValidateState(property.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > 10_000) throw new InvalidDataException("engine/state.json: массив больше 10000 элементов.");
                foreach (var item in value.EnumerateArray()) ValidateState(item, depth + 1);
                break;
            case JsonValueKind.String: case JsonValueKind.True: case JsonValueKind.False: break;
            case JsonValueKind.Number when value.TryGetDouble(out double number) && double.IsFinite(number): break;
            default: throw new InvalidDataException("engine/state.json: разрешены объекты, массивы, строки, конечные числа и логические значения.");
        }
    }

    private static void Range(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max)
            throw new InvalidDataException($"engine: {name} должен быть от {min} до {max}.");
    }

    private static void Text(string value, string name, bool allowEmpty = false)
    {
        if (value == null || value.Length > 200 || (!allowEmpty && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException("engine: некорректное значение " + name + ".");
    }
}

/// <summary>Default window size, initial placement and display mode.</summary>
public sealed class WindowDefaults
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public string Position { get; set; } = "center";
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public string Mode { get; set; } = "windowed";
    public bool Resizable { get; set; } = true;
}

/// <summary>Theme parameters for the scene, menu, buttons and dialogue.</summary>
public sealed class GameTheme
{
    public string FontFamily { get; set; } = "Segoe UI";
    public string BackgroundColor { get; set; } = "#FF201134";
    public bool Pattern { get; set; } = true;
    public string PatternStyle { get; set; } = "waves";
    public string Palette { get; set; } = "violet";
    public bool AnimationEnabled { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1;
    public double WaveAmplitude { get; set; } = 24;
    public string PatternColor { get; set; } = "#FF704697";
    public string? BackgroundImage { get; set; }
    public string AccentColor { get; set; } = "#FFD3ACFF";
    public string TextColor { get; set; } = "#FFF5EEFF";
    public string ButtonColor { get; set; } = "#EB3C2654";
    public int CharacterWidth { get; set; } = 420;
    public DialogueTheme Dialogue { get; set; } = new();
    public MenuTheme Menu { get; set; } = new();
}

/// <summary>Editable main and pause menu layout, typography and overlay color.</summary>
public sealed class MenuTheme
{
    public int Width { get; set; } = 420;
    public int TitleFontSize { get; set; } = 44;
    public int SubtitleFontSize { get; set; } = 16;
    public string Alignment { get; set; } = "center";
    public string OverlayColor { get; set; } = "#AA160D24";
}

/// <summary>Editable dialogue panel layout and typography.</summary>
public sealed class DialogueTheme
{
    public int Height { get; set; } = 240;
    public int Margin { get; set; } = 28;
    public int Padding { get; set; } = 24;
    public int CornerRadius { get; set; } = 16;
    public int FontSize { get; set; } = 24;
    public int SpeakerFontSize { get; set; } = 20;
    public string BackgroundColor { get; set; } = "#ED21162F";
    public string BorderColor { get; set; } = "#FF8E60B5";
    public string TextColor { get; set; } = "#FFF5EEFF";
    public string SpeakerColor { get; set; } = "#FFD3ACFF";
}

/// <summary>Menu and technical game behavior defaults.</summary>
public sealed class GameDefaults
{
    public bool MainMenu { get; set; } = true;
    public string Subtitle { get; set; } = "Ваша история начинается здесь";
    public string StartLabel { get; set; } = "Начать игру";
    public string ContinueLabel { get; set; } = "Продолжить";
    public string ExitLabel { get; set; } = "Выйти";
    public bool AutoSave { get; set; } = true;
    public bool DebugEnabled { get; set; } = true;
    public int ExecutionTimeoutSeconds { get; set; } = 30;
}
