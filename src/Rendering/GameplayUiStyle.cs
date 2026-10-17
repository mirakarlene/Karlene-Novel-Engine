using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace KNE;

/// <summary>A complete, immutable gameplay UI configuration. Coordinates are offsets from the chosen anchors.</summary>
public sealed record GameplayUiStyle
{
    public double X { get; init; } = 28;
    public double Y { get; init; } = 28;
    public double Width { get; init; } = 1224;
    public double Height { get; init; } = 240;
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 24;
    public string FontWeight { get; init; } = "Normal";
    public string TextColor { get; init; } = "#FFF5EEFF";
    public string BackgroundColor { get; init; } = "#ED21162F";
    public double Opacity { get; init; } = 1;
    public double CornerRadius { get; init; } = 16;
    public string? Texture { get; init; }
    public double PaddingLeft { get; init; } = 24;
    public double PaddingTop { get; init; } = 24;
    public double PaddingRight { get; init; } = 24;
    public double PaddingBottom { get; init; } = 24;
    public string HorizontalAlignment { get; init; } = "left";
    public string VerticalAlignment { get; init; } = "bottom";
    public string TextAlignment { get; init; } = "left";

    public static readonly JsonSerializerOptions JsonOptions = new(EngineSettings.JsonOptions)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static bool IsTarget(string target) => target is "dialogue" or "choice" or "next" or "save" or "load" or "auto_save" or "pause";

    public static GameplayUiStyle ForTarget(string target)
    {
        if (!IsTarget(target)) throw new ArgumentException("Элемент UI: dialogue, choice, next, save, load, auto_save или pause.");
        if (target == "dialogue") return new();
        return new()
        {
            X = target switch { "load" => 190, "auto_save" => 356, "pause" => 522, _ => 24 },
            Y = target is "choice" ? 160 : 24,
            Width = target is "choice" ? 600 : 158,
            Height = 44,
            FontSize = 16,
            BackgroundColor = "#EB3C2654",
            CornerRadius = 8,
            PaddingLeft = 14, PaddingRight = 14, PaddingTop = 7, PaddingBottom = 7,
            HorizontalAlignment = target is "choice" ? "center" : "right",
            VerticalAlignment = target is "choice" or "next" ? "bottom" : "top",
            TextAlignment = "center"
        };
    }

    public void Validate(string? projectRoot = null)
    {
        static void Range(double value, double min, double max, string field)
        {
            if (!double.IsFinite(value) || value < min || value > max)
                throw new ArgumentException($"{field}: допустимо от {min} до {max}.");
        }
        Range(X, -100000, 100000, "X"); Range(Y, -100000, 100000, "Y");
        Range(Width, 1, 16384, "Ширина"); Range(Height, 1, 16384, "Высота");
        Range(FontSize, 1, 512, "Размер шрифта"); Range(Opacity, 0, 1, "Прозрачность");
        Range(CornerRadius, 0, 4096, "Скругление");
        Range(PaddingLeft, 0, 4096, "Отступ слева"); Range(PaddingTop, 0, 4096, "Отступ сверху");
        Range(PaddingRight, 0, 4096, "Отступ справа"); Range(PaddingBottom, 0, 4096, "Отступ снизу");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 256 || FontFamily.Any(char.IsControl) || FontFamily.Contains("://") || FontFamily.Contains('#') || FontFamily.Contains('/') || FontFamily.Contains('\\'))
            throw new ArgumentException("Нужно локальное семейство шрифта, например Segoe UI.");
        if (FontWeight is not ("Thin" or "ExtraLight" or "Light" or "Normal" or "Medium" or "SemiBold" or "Bold" or "ExtraBold" or "Black"))
            throw new ArgumentException("Насыщенность: Thin, ExtraLight, Light, Normal, Medium, SemiBold, Bold, ExtraBold или Black.");
        foreach (string color in new[] { TextColor, BackgroundColor })
        {
            if (string.IsNullOrWhiteSpace(color) || color.Length > 40) throw new ArgumentException("Нужен цвет UI.");
            try { _ = (Color)ColorConverter.ConvertFromString(color); }
            catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
            { throw new ArgumentException("Некорректный цвет UI: " + color, error); }
        }
        if (HorizontalAlignment is not ("left" or "center" or "right" or "stretch") || VerticalAlignment is not ("top" or "center" or "bottom" or "stretch"))
            throw new ArgumentException("Положение: left/center/right/stretch и top/center/bottom/stretch.");
        if (TextAlignment is not ("left" or "center" or "right" or "justify")) throw new ArgumentException("Выравнивание текста: left, center, right или justify.");
        if (Texture is not null)
        {
            if (string.IsNullOrWhiteSpace(Texture)) throw new ArgumentException("Для стандартной текстуры оставьте null.");
            if (projectRoot is not null)
            {
                string path = ResourcePaths.Resolve(projectRoot, Texture);
                try
                {
                    using var input = System.IO.File.OpenRead(path);
                    var image = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                        System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    if (image.Frames.Count == 0) throw new ArgumentException("В текстуре UI нет изображения.");
                }
                catch (Exception error) when (error is System.IO.IOException or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
                { throw new ArgumentException("Не удалось прочитать текстуру UI: " + Texture, error); }
            }
        }
    }
}

public sealed record GameplayUiConfiguration(string Target, GameplayUiStyle Style);
