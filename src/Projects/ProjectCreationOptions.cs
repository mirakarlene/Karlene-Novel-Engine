using System.IO;

namespace KNE;

/// <summary>A named startup resolution with a description for its authoring thumbnail.</summary>
public sealed record ProjectResolution(string Id, string Title, int Width, int Height, string Description);

/// <summary>Validated choices persisted when a new project is created; legacy engine defaults stay compatible.</summary>
public sealed record ProjectCreationOptions
{
    /// <summary>Initial window width, from 640 to 3840 pixels.</summary>
    public int Width { get; init; } = 1920;
    /// <summary>Initial window height, from 480 to 2160 pixels.</summary>
    public int Height { get; init; } = 1080;
    /// <summary>Automatic selection or an explicitly selected OpenGL/Vulkan backend.</summary>
    public string GraphicsApi { get; init; } = "auto";
    /// <summary>Frame limit: 30, 60, 144, or zero for unlimited.</summary>
    public int MaxFps { get; init; } = 60;
    /// <summary>One of the predefined game palettes, independent of the editor theme.</summary>
    public string Palette { get; init; } = "violet";
    /// <summary>Startup size presets, ordered with the recommended Full HD format first.</summary>
    public static IReadOnlyList<ProjectResolution> Resolutions { get; } = [
        new("fhd", "1920×1080 · Full HD", 1920, 1080, "Рекомендуется · для большинства современных мониторов"),
        new("hd", "1280×720 · HD", 1280, 720, "Для слабых ПК и менее крупных ресурсов"),
        new("qhd", "2560×1440 · QHD", 2560, 1440, "Высокая чёткость на современных мониторах"),
        new("classic", "1024×768 · Классика", 1024, 768, "Формат ранних визуальных новелл"),
        new("retro", "800×600 · Ретро", 800, 600, "Олдскульный вид и пиксель-арт"),
        new("wide", "2560×1080 · UltraWide", 2560, 1080, "Широкие кинематографичные панорамы"),
        new("portrait", "1080×1920 · Вертикально", 1080, 1920, "Для чтения с телефона")];
    /// <summary>Palettes with complete, predictable initial game colors.</summary>
    public static IReadOnlyList<GraphicsOption<string>> Palettes { get; } = [new("violet", "Фиолетовая"), new("ocean", "Океан"), new("rose", "Розовая"), new("forest", "Лес"), new("amber", "Янтарь")];

    /// <summary>Rejects invalid choices before creating directories or writing project files.</summary>
    public void Validate()
    {
        if (Width is < 640 or > 3840 || Height is < 480 or > 2160) throw new InvalidDataException("Размер окна: ширина от 640 до 3840, высота от 480 до 2160 пикселей.");
        new GraphicsSettings { Api = GraphicsApi, MaxFps = MaxFps }.Validate();
        if (!Palettes.Any(palette => palette.Value == Palette)) throw new InvalidDataException("Выберите палитру проекта.");
    }

    /// <summary>Creates consistent startup, graphics, theme and editable screen settings from the wizard choices.</summary>
    public EngineSettings CreateEngineSettings()
    {
        Validate();
        var theme = new GameTheme();
        if (Palette != "violet") ThemePalettes.Apply(theme, Palette);
        return new EngineSettings { Window = new() { Width = Width, Height = Height }, Theme = theme,
            Screens = ScreenLayouts.CreateDefaults(theme), Performance = new() { Graphics = new() { Api = GraphicsApi, MaxFps = MaxFps } } };
    }
}
