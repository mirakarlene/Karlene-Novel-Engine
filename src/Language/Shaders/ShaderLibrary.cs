using System.IO;
using System.Text.Json.Serialization;

namespace KNE;

/// <summary>A typed data connection; one output may feed many inputs.</summary>
public sealed record ShaderConnection(string Node, int Output = 0);
/// <summary>Persisted shader block arguments and named input links.</summary>
public sealed class ShaderNodeSettings
{
    public Dictionary<string, ShaderConnection> Inputs { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> Values { get; set; } = new(StringComparer.Ordinal);
    public string Name { get; set; } = "parameter";
    public string Target { get; set; } = "scene";
    public string Screen { get; set; } = "";
}
/// <summary>One externally adjustable numeric shader argument.</summary>
public sealed record ShaderParameter(string Key, string Label, double Default, double Min = -10000, double Max = 10000);
/// <summary>Ports and description of a shader operation.</summary>
public sealed record ShaderDefinition(ScriptNodeKind Kind, string Title, string Description, string[] Inputs)
{
    public string[] Outputs => Kind == ScriptNodeKind.ShaderOutput ? [] : Kind == ScriptNodeKind.ShaderSplit ? ["R", "G", "B", "A"] : ["Значение"];
}
/// <summary>The shader-only data-flow library, independent of Lua commands.</summary>
public static class ShaderLibrary
{
    public static IReadOnlyList<ShaderDefinition> Definitions { get; } = [
        new(ScriptNodeKind.ShaderContainer, "Контейнер GLSL/HLSL", "Вложенный .kneshader; двойной клик открывает или создаёт файл.", ["UV"]),
        new(ScriptNodeKind.ShaderTexture, "Texture · Текстура", "Цель шейдера: фон, спрайт, UI, именованный экран или вся сцена.", ["UV"]),
        new(ScriptNodeKind.ShaderUv, "UV", "Нормализованные координаты пикселя 0…1.", []),
        new(ScriptNodeKind.ShaderTime, "Time · Время", "Время в секундах для анимации.", []),
        new(ScriptNodeKind.ShaderScreenPosition, "Screen Position", "Координаты пикселя на мониторе.", []),
        new(ScriptNodeKind.ShaderFloat, "Exposed Float", "Числовой параметр, доступный снаружи.", []),
        new(ScriptNodeKind.ShaderVector, "Exposed Vector", "Четырёхкомпонентный внешний параметр.", []),
        new(ScriptNodeKind.ShaderColor, "Exposed Color", "Внешняя палитра RGBA.", []),
        new(ScriptNodeKind.ShaderTilingOffset, "Tiling & Offset", "Масштаб и сдвиг UV.", ["UV"]),
        new(ScriptNodeKind.ShaderRotate, "Rotate", "Вращение UV вокруг центра.", ["UV"]),
        new(ScriptNodeKind.ShaderPolar, "Polar Coordinates", "Радиус и угол относительно центра.", ["UV"]),
        new(ScriptNodeKind.ShaderFlipbook, "Flipbook", "Кадры спрайт-листа по времени.", ["UV", "Time"]),
        new(ScriptNodeKind.ShaderTwirl, "Twirl", "Закручивание UV около центра.", ["UV"]),
        new(ScriptNodeKind.ShaderSimplex, "Simplex Noise", "Плавный двумерный симплексный шум.", ["UV"]),
        new(ScriptNodeKind.ShaderVoronoi, "Voronoi", "Расстояния до центров ячеек.", ["UV"]),
        new(ScriptNodeKind.ShaderCheckerboard, "Checkerboard", "Шахматный узор.", ["UV"]),
        new(ScriptNodeKind.ShaderLinearGradient, "Gradient · Linear", "Линейный градиент двух цветов.", ["UV", "A", "B"]),
        new(ScriptNodeKind.ShaderRadialGradient, "Gradient · Radial", "Радиальный градиент двух цветов.", ["UV", "A", "B"]),
        new(ScriptNodeKind.ShaderSplit, "Split · RGBA", "Разделить цвет на четыре канала.", ["Color"]),
        new(ScriptNodeKind.ShaderCombine, "Combine · RGBA", "Собрать цвет из каналов.", ["R", "G", "B", "A"]),
        new(ScriptNodeKind.ShaderHueShift, "Hue Shift", "Сдвиг цветового тона.", ["Color"]),
        new(ScriptNodeKind.ShaderSaturation, "Saturation", "Насыщенность цвета.", ["Color"]),
        new(ScriptNodeKind.ShaderContrast, "Contrast", "Контрастность цвета.", ["Color"]),
        new(ScriptNodeKind.ShaderBrightness, "Brightness", "Яркость цвета.", ["Color"]),
        new(ScriptNodeKind.ShaderInvert, "Invert", "Негатив RGB с сохранением альфа-канала.", ["Color"]),
        new(ScriptNodeKind.ShaderGrayscale, "Grayscale", "Чёрно-белый фильтр.", ["Color"]),
        new(ScriptNodeKind.ShaderPosterize, "Posterize", "Ограничение количества цветовых уровней.", ["Color"]),
        new(ScriptNodeKind.ShaderGaussianBlur, "Gaussian Blur", "Гауссово размытие: радиус и качество.", ["Color"]),
        new(ScriptNodeKind.ShaderBoxBlur, "Box Blur", "Усредняющее размытие: радиус и качество.", ["Color"]),
        new(ScriptNodeKind.ShaderMotionBlur, "Motion Blur", "Смазывание вдоль вектора скорости.", ["Color"]),
        new(ScriptNodeKind.ShaderBloom, "Bloom / Glow", "Свечение ярких участков.", ["Color"]),
        new(ScriptNodeKind.ShaderChromatic, "Chromatic Aberration", "Раздельный сдвиг RGB-каналов.", ["Color"]),
        new(ScriptNodeKind.ShaderVignette, "Vignette", "Затемнение краёв.", ["Color"]),
        new(ScriptNodeKind.ShaderScanlines, "Scanlines", "Анимированные полосы телевизора.", ["Color"]),
        new(ScriptNodeKind.ShaderLensDistortion, "Lens Distortion", "Выпуклая или вогнутая линза.", ["Color"]),
        new(ScriptNodeKind.ShaderPixelate, "Pixelate", "Пикселизация с заданным размером ячейки.", ["Color"]),
        new(ScriptNodeKind.ShaderSharpen, "Sharpen", "Повышение резкости.", ["Color"]),
        new(ScriptNodeKind.ShaderSobel, "Edge Detection · Sobel", "Выделение контуров.", ["Color"]),
        new(ScriptNodeKind.ShaderDithering, "Dithering", "Упорядоченный точечный узор.", ["Color"]),
        new(ScriptNodeKind.ShaderMix, "Mix · Lerp", "Смешивание цветов или текстур.", ["A", "B", "T"]),
        new(ScriptNodeKind.ShaderAdd, "Add", "Сложение цветов.", ["A", "B"]),
        new(ScriptNodeKind.ShaderSubtract, "Subtract", "Вычитание цветов.", ["A", "B"]),
        new(ScriptNodeKind.ShaderMultiply, "Multiply", "Умножение цветов.", ["A", "B"]),
        new(ScriptNodeKind.ShaderScreen, "Screen", "Осветляющий режим наложения.", ["A", "B"]),
        new(ScriptNodeKind.ShaderStep, "Step", "Резкое разделение по порогу.", ["Value", "Edge"]),
        new(ScriptNodeKind.ShaderSmoothstep, "Smoothstep", "Плавное разделение по двум порогам.", ["Value", "Min", "Max"]),
        new(ScriptNodeKind.ShaderOutput, "Fragment Output", "Финальный цвет; компиляция GLSL/HLSL для OpenGL/Vulkan.", ["Color"])
    ];
    public static bool IsShader(ScriptNodeKind kind) => kind >= ScriptNodeKind.ShaderContainer && kind <= ScriptNodeKind.ShaderOutput;
    public static ShaderDefinition Get(ScriptNodeKind kind) => Definitions.Single(entry => entry.Kind == kind);
    public static IReadOnlyList<ShaderParameter> Parameters(ScriptNodeKind kind) => kind switch
    {
        ScriptNodeKind.ShaderFloat => [new("x", "Значение", .5), new("min", "Минимум слайдера", 0), new("max", "Максимум слайдера", 1)],
        ScriptNodeKind.ShaderVector => [new("x", "X", 1), new("y", "Y", 1), new("z", "Z", 0), new("w", "W", 0)],
        ScriptNodeKind.ShaderColor => [new("x", "R", 1, 0, 1), new("y", "G", .5, 0, 1), new("z", "B", 1, 0, 1), new("w", "A", 1, 0, 1)],
        ScriptNodeKind.ShaderTilingOffset => [new("x", "Tiling X", 1), new("y", "Tiling Y", 1), new("z", "Offset X", 0), new("w", "Offset Y", 0)],
        ScriptNodeKind.ShaderRotate => [new("amount", "Угол, радианы", 0)],
        ScriptNodeKind.ShaderFlipbook => [new("columns", "Столбцы", 4, 1, 64), new("rows", "Строки", 4, 1, 64), new("speed", "Кадров в секунду", 8, 0, 144)],
        ScriptNodeKind.ShaderTwirl => [new("amount", "Сила закручивания", 2)],
        ScriptNodeKind.ShaderSimplex or ScriptNodeKind.ShaderVoronoi or ScriptNodeKind.ShaderCheckerboard => [new("scale", "Масштаб", 8, .001, 1000)],
        ScriptNodeKind.ShaderPosterize or ScriptNodeKind.ShaderDithering => [new("levels", "Уровни цвета", 8, 2, 256)],
        ScriptNodeKind.ShaderGaussianBlur or ScriptNodeKind.ShaderBoxBlur or ScriptNodeKind.ShaderBloom => [new("amount", "Радиус / сила", 3, 0, 64), new("quality", "Качество (радиус выборки)", 2, 1, 4), new("threshold", "Порог свечения", .7, 0, 1)],
        ScriptNodeKind.ShaderMotionBlur => [new("x", "Скорость X, пиксели", 8, -128, 128), new("y", "Скорость Y, пиксели", 0, -128, 128), new("quality", "Количество выборок", 8, 2, 16)],
        ScriptNodeKind.ShaderChromatic => [new("amount", "Сдвиг, пиксели", 2, 0, 64)],
        ScriptNodeKind.ShaderPixelate => [new("amount", "Размер пикселя", 8, 1, 256)],
        ScriptNodeKind.ShaderSaturation or ScriptNodeKind.ShaderContrast or ScriptNodeKind.ShaderBrightness => [new("amount", "Множитель", 1, 0, 8)],
        ScriptNodeKind.ShaderHueShift => [new("amount", "Сдвиг тона (полный круг = 1)", .2, -10, 10)],
        ScriptNodeKind.ShaderVignette or ScriptNodeKind.ShaderScanlines or ScriptNodeKind.ShaderSharpen or ScriptNodeKind.ShaderLensDistortion => [new("amount", "Сила", .4, -4, 4)],
        _ => []
    };
    internal static double Value(ScriptNode node, string key) => node.Shader?.Values.GetValueOrDefault(key, Parameters(node.Kind).First(p => p.Key == key).Default) ?? Parameters(node.Kind).First(p => p.Key == key).Default;
    internal static void ValidateNode(ScriptNode node)
    {
        var settings = node.Shader ?? throw new InvalidDataException("У шейдерного блока отсутствуют параметры.");
        if (settings.Inputs == null || settings.Values == null || settings.Name == null || settings.Screen == null || settings.Target is not ("scene" or "background" or "sprite" or "ui" or "screen")) throw new InvalidDataException("Некорректные параметры шейдера.");
        if (settings.Name.Length > 80 || settings.Screen.Length > 128 || settings.Inputs.Any(entry => !Get(node.Kind).Inputs.Contains(entry.Key) || entry.Value == null || string.IsNullOrEmpty(entry.Value.Node) || entry.Value.Output is < 0 or > 3)) throw new InvalidDataException("Некорректный вход шейдера.");
        foreach (var pair in settings.Values)
        {
            var parameter = Parameters(node.Kind).FirstOrDefault(value => value.Key == pair.Key);
            if (parameter == null || !double.IsFinite(pair.Value) || pair.Value < parameter.Min || pair.Value > parameter.Max) throw new InvalidDataException("Недопустимый параметр шейдера: " + pair.Key);
            if (pair.Key is "quality" or "columns" or "rows" or "levels" && pair.Value != Math.Truncate(pair.Value)) throw new InvalidDataException("Параметр " + pair.Key + " должен быть целым числом.");
        }
        if (node.Kind == ScriptNodeKind.ShaderFloat && Value(node, "max") <= Value(node, "min")) throw new InvalidDataException("Максимум слайдера должен быть больше минимума.");
        if (node.Kind == ScriptNodeKind.ShaderFloat && (Value(node, "x") < Value(node, "min") || Value(node, "x") > Value(node, "max"))) throw new InvalidDataException("Значение внешнего Float должно находиться в диапазоне слайдера.");
    }
}
