using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Project-owned vector layouts and animations for non-gameplay screens.</summary>
public sealed class ScreenLayouts
{
    public int Version { get; set; } = 1;
    public GameTheme? Theme { get; set; }
    public List<GameScreen> Screens { get; set; } = [];
    public GameScreen? Find(string kind) => Screens.FirstOrDefault(s => s.Kind == kind);
    public string Serialize() => JsonSerializer.Serialize(this, EngineSettings.JsonOptions);
    public ScreenLayouts Clone() => Parse(Serialize());

    public static ScreenLayouts Parse(string json)
    {
        try
        {
            var result = JsonSerializer.Deserialize<ScreenLayouts>(json, EngineSettings.JsonOptions)
                ?? throw new InvalidDataException("Пустое оформление.");
            result.Validate();
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("Ошибка engine/screens.json: " + ex.Message, ex); }
    }

    public void Validate()
    {
        if (Version != 1 || Screens == null || Screens.Count > 32) throw new InvalidDataException("Оформление поддерживает до 32 экранов (версия 1).");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in Screens)
        {
            if (screen == null || string.IsNullOrWhiteSpace(screen.Id) || screen.Id.Length > 80 || !ids.Add(screen.Id))
                throw new InvalidDataException("Экраны должны иметь уникальные ID.");
            if (string.IsNullOrWhiteSpace(screen.Name) || screen.Name.Length > 100 || screen.Elements == null || screen.Elements.Count > 100)
                throw new InvalidDataException("У экрана должно быть имя и не более 100 элементов.");
            if (screen.Kind is not ("main_menu" or "gameplay" or "pause" or "settings" or "saves" or "custom"))
                throw new InvalidDataException("Неизвестный тип экрана.");
            if (screen.Kind == "custom" && screen.Id is "gameplay" or "saves") throw new InvalidDataException("Этот ID зарезервирован служебным экраном.");
            if (screen.Kind != "custom" && !roles.Add(screen.Kind)) throw new InvalidDataException("Служебная роль экрана должна быть уникальной.");
            var elementIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in screen.Elements)
            {
                if (element == null || string.IsNullOrWhiteSpace(element.Id) || !elementIds.Add(element.Id) || element.Id.Length > 80 || element.Text == null || element.Text.Length > 2000)
                    throw new InvalidDataException("Некорректный элемент экрана.");
                if (element.Type is not ("text" or "button" or "panel" or "image" or "toggle" or "slider" or "save_list" or "save_name")) throw new InvalidDataException("Неизвестный тип элемента.");
                if (element.Type is "save_list" or "save_name" && screen.Kind != "saves") throw new InvalidDataException("Поля сохранений доступны на экране сохранений.");
                if (!Actions.Contains(element.Action)) throw new InvalidDataException("Неизвестное действие элемента.");
                Bound(element.X, 0, 1280, "X"); Bound(element.Y, 0, 720, "Y");
                Bound(element.Width, 20, 1280, "width"); Bound(element.Height, 20, 720, "height");
                if (element.X + element.Width > 1280 || element.Y + element.Height > 720) throw new InvalidDataException("Элемент должен помещаться в холст 1280×720.");
                Bound(element.FontSize, 10, 100, "font_size");
                Bound(element.MotionX, -1280, 1280, "motion_x"); Bound(element.MotionY, -720, 720, "motion_y");
                Bound(element.Delay, 0, 60, "delay"); Bound(element.Duration, 0.1, 60, "duration");
                if (element.MotionOrder is < 0 or > 100) throw new InvalidDataException("motion_order должен быть от 0 до 100.");
                if (element.Type == "toggle" && element.Action is not ("fullscreen" or "animation")) throw new InvalidDataException("Переключатель поддерживает fullscreen/animation.");
                if (element.Type == "slider" && element.Action is not ("music_volume" or "sound_volume")) throw new InvalidDataException("Ползунок поддерживает music_volume/sound_volume.");
            }
            if (screen.Elements.Count(e => e.Type == "save_list") > 1 || screen.Elements.Count(e => e.Type == "save_name") > 1)
                throw new InvalidDataException("На экране может быть по одному списку сохранений и полю имени.");
        }
        foreach (var element in Screens.SelectMany(s => s.Elements))
            if (element.Action == "navigate" && (element.Target == null || !Screens.Any(s => s.Id == element.Target && s.Kind != "gameplay")))
                throw new InvalidDataException("Кнопка перехода должна ссылаться на существующий экран вне геймплея.");
    }

    public static readonly string[] Actions = ["none", "start", "continue", "resume", "save", "saves", "settings", "back", "exit", "navigate", "fullscreen", "animation", "music_volume", "sound_volume", "save_slot", "load_slot", "delete_slot"];
    private static void Bound(double value, double min, double max, string name)
    {
        if (!double.IsFinite(value) || value < min || value > max) throw new InvalidDataException("Некорректное значение элемента: " + name);
    }

    public GameScreen AddScreen(string name)
    {
        if (Screens.Count >= 32) throw new InvalidOperationException("Достигнут предел 32 экранов.");
        var screen = new GameScreen { Id = Guid.NewGuid().ToString("N"), Name = name, Kind = "custom" };
        screen.Elements.Add(new() { Text = name, Type = "text", X = 480, Y = 180, Width = 320, Height = 80, FontSize = 32 });
        screen.Elements.Add(new() { Text = "Назад", Action = "back", X = 480, Y = 440, Width = 320, Height = 48 });
        Screens.Add(screen);
        return screen;
    }

    public void RemoveScreen(string id)
    {
        var screen = Screens.Single(s => s.Id == id);
        if (screen.Kind == "gameplay") throw new InvalidOperationException("Игровую сцену нельзя удалить из оформления: она выполняется сценариями.");
        if (Screens.Where(s => s != screen).SelectMany(s => s.Elements).Any(e => e.Action == "navigate" && e.Target == id))
            throw new InvalidOperationException("На экран ссылается кнопка. Сначала измените её переход.");
        Screens.Remove(screen);
    }

    public static ScreenLayouts CreateDefaults(GameTheme? theme = null)
    {
        theme ??= new();
        var result = new ScreenLayouts();
        GameScreen Screen(string kind, string name) { var s = new GameScreen { Id = kind, Kind = kind, Name = name }; result.Screens.Add(s); return s; }
        void Text(GameScreen s, string id, string text, double y, double size) => s.Elements.Add(new() { Id = id, Type = "text", Text = text, X = 440, Y = y, Width = 400, Height = size > 30 ? 120 : 40, FontSize = size });
        void Button(GameScreen s, string action, string text, double y, int order) => s.Elements.Add(new() { Id = action, Text = text, Action = action, X = 490, Y = y, Width = 300, Height = 44, MotionX = 20, MotionOrder = order, Duration = 0.35 });
        var main = Screen("main_menu", "Главное меню");
        Text(main, "title", "{{title}}", 125, theme.Menu.TitleFontSize); Text(main, "subtitle", "{{subtitle}}", 245, theme.Menu.SubtitleFontSize);
        Button(main, "start", "{{start_label}}", 320, 0); Button(main, "continue", "{{continue_label}}", 376, 1);
        Button(main, "settings", "Настройки", 432, 2); Button(main, "saves", "Сохранения", 488, 3); Button(main, "exit", "{{exit_label}}", 544, 4);
        Screen("gameplay", "Сцена · новелла");
        var pause = Screen("pause", "Экран паузы"); Text(pause, "title", "Пауза", 120, 40);
        Button(pause, "resume", "Вернуться к игре", 250, 0); Button(pause, "save", "Сохранить", 306, 1);
        Button(pause, "saves", "Сохранения", 362, 2); Button(pause, "settings", "Настройки", 418, 3);
        Button(pause, "start", "Новая игра", 474, 4); Button(pause, "exit", "Выйти", 530, 5);
        var settings = Screen("settings", "Экран настроек"); Text(settings, "title", "Настройки", 120, 40);
        settings.Elements.Add(new() { Type = "toggle", Text = "Полный экран", Action = "fullscreen", X = 440, Y = 240, Width = 400, Height = 40 });
        settings.Elements.Add(new() { Type = "toggle", Text = "Анимация оформления", Action = "animation", X = 440, Y = 300, Width = 400, Height = 40 });
        settings.Elements.Add(new() { Type = "slider", Text = "Музыка", Action = "music_volume", X = 440, Y = 360, Width = 400, Height = 60 });
        settings.Elements.Add(new() { Type = "slider", Text = "Звуки", Action = "sound_volume", X = 440, Y = 440, Width = 400, Height = 60 });
        Button(settings, "back", "Назад", 540, 0);
        var saves = Screen("saves", "Экран сохранений"); Text(saves, "title", "Сохранения", 100, 36);
        saves.Elements.Add(new() { Type = "save_list", Text = "Список слотов", X = 380, Y = 220, Width = 520, Height = 200 });
        saves.Elements.Add(new() { Type = "save_name", Text = "Имя слота", X = 380, Y = 440, Width = 520, Height = 44 });
        saves.Elements.Add(new() { Text = "Записать слот", Action = "save_slot", X = 380, Y = 505, Width = 165, Height = 44 });
        saves.Elements.Add(new() { Text = "Загрузить", Action = "load_slot", X = 558, Y = 505, Width = 165, Height = 44 });
        saves.Elements.Add(new() { Text = "Удалить", Action = "delete_slot", X = 735, Y = 505, Width = 165, Height = 44 });
        Button(saves, "back", "Назад", 580, 0);
        return result;
    }
}

public sealed class GameScreen
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Экран";
    public string Kind { get; set; } = "custom";
    public List<ScreenElement> Elements { get; set; } = [];
    public override string ToString() => Name;
}

public sealed class ScreenElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "button";
    public string Text { get; set; } = "Кнопка";
    public string Action { get; set; } = "none";
    public string? Target { get; set; }
    public string? Image { get; set; }
    public double X { get; set; } = 490;
    public double Y { get; set; } = 300;
    public double Width { get; set; } = 300;
    public double Height { get; set; } = 44;
    public double FontSize { get; set; } = 18;
    public double MotionX { get; set; }
    public double MotionY { get; set; }
    public double Delay { get; set; }
    public double Duration { get; set; } = 0.4;
    public int MotionOrder { get; set; }
    public bool Loop { get; set; }
    public override string ToString() => Text;
}

/// <summary>Conflict-aware atomic save of both vector layouts and the appearance theme.</summary>
public sealed class AppearanceDocument
{
    private readonly string root;
    private readonly string path;
    private string? baseline;
    private string saved;
    public ScreenLayouts Layout { get; private set; }
    public bool IsDirty => Layout.Serialize() != saved;

    private AppearanceDocument(string root, string? content, ScreenLayouts layout)
    {
        this.root = root; path = PathSafety.ResolveRelative(root, "engine/screens.json");
        baseline = content; Layout = layout; saved = layout.Serialize();
    }

    public static async Task<AppearanceDocument> OpenAsync(ProjectInfo project)
    {
        string path = PathSafety.ResolveRelative(project.Path, "engine/screens.json");
        if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Оформление больше 2 МБ.");
        string? content = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
        var layout = content == null ? project.Engine.Screens.Clone() : ScreenLayouts.Parse(content);
        layout.Theme ??= JsonSerializer.Deserialize<GameTheme>(JsonSerializer.Serialize(project.Engine.Theme, EngineSettings.JsonOptions), EngineSettings.JsonOptions)!;
        return new(project.Path, content, layout);
    }

    public void Restore(string json) => Layout = ScreenLayouts.Parse(json);
    public async Task SaveAsync()
    {
        Layout.Validate();
        EngineSettings.ValidateAppearance(root, Layout.Theme ?? new());
        foreach (var e in Layout.Screens.SelectMany(s => s.Elements))
            if (e.Type == "image") _ = ResourcePaths.Resolve(root, e.Image ?? "");
        string content = Layout.Serialize();
        if (System.Text.Encoding.UTF8.GetByteCount(content) > 2 * 1024 * 1024) throw new InvalidDataException("Оформление больше 2 МБ.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content);
            PathSafety.EnsureInside(root, path);
            string? current = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            if (current != baseline) throw new IOException("Оформление изменено другим приложением. Закройте редактор и откройте его снова.");
            File.Move(temporary, path, overwrite: true);
            baseline = content; saved = content;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
