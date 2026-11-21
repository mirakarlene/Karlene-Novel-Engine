using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KNE;

internal sealed record ProjectFileFormat(string Extension, string Title, string Symbol, string Color, string? Folder = null)
{
    internal bool IsDirectory { get; init; }
    internal bool IsPlaceholder { get; init; }
    internal IReadOnlyList<string>? Extensions { get; init; }
    internal string Filter
    {
        get
        {
            if (Extensions is { } extensions) return $"{Title}|{string.Join(';', extensions.Select(extension => "*" + extension))}";
            return Extension.Length == 0 ? "Все файлы (*.*)|*.*" : $"{Title} (*{Extension})|*{Extension}";
        }
    }

    internal bool Accepts(string extension) => Extensions is { } extensions
        ? extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
        : Extension.Length == 0 || Extension.Equals(extension, StringComparison.OrdinalIgnoreCase);
}

internal static class ProjectFileFormats
{
    internal static readonly ProjectFileFormat Krl = new(".krl", "Сценарий KRL", "‹›", "#7A4ACA", "game/scenes");
    internal static readonly ProjectFileFormat Graph = new(".knegraph", "Блок-схема KNE", "◇", "#9D4FC0", "game/scenes");
    internal static readonly ProjectFileFormat Lua = new(".lua", "Скрипт Lua", "", "#2856AD", "game/scripts");
    internal static readonly ProjectFileFormat VisualLua = new(".vlua", "VLua-модуль", "", "#228350", "game/scripts");
    private static readonly ProjectFileFormat Text = new(".txt", "Текст", "≡", "#53647B");
    private static readonly ProjectFileFormat Json = new(".json", "JSON", "{}", "#916219");
    private static readonly ProjectFileFormat Csv = new(".csv", "Таблица CSV", "", "#A2544E");
    private static readonly ProjectFileFormat Shader = new(".kneshader", "Шейдерная схема", "◈", "#AD3361", "game/shaders");
    private static readonly ProjectFileFormat RenPy = new(".rpy", "Сценарий Ren’Py", "RP", "#A83868");
    private static readonly ProjectFileFormat Word = new(".docx", "Документ Word", "W", "#2856AD");
    private static readonly ProjectFileFormat Other = new("", "Другое", "?", "#53647B");
    private static readonly ProjectFileFormat Directory = new("", "Папка", "", "#AE7924") { IsDirectory = true };
    private static readonly ProjectFileFormat CSharp = new(".cs", "Скрипт C#", "C#", Krl.Color, "game/scripts");
    private static readonly ProjectFileFormat Image = new("", "Изображения", "▧", "#C15B18")
    { Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".tif", ".tiff", ".svg", ".avif"] };
    private static readonly ProjectFileFormat Audio = new("", "Аудио", "♪", "#168775")
    { Extensions = [".wav", ".mp3", ".ogg", ".flac", ".aac", ".m4a", ".wma", ".opus", ".aiff"] };
    private static readonly ProjectFileFormat Video = new("", "Видео", "▶", "#337C9E")
    { Extensions = [".mp4", ".m4v", ".webm", ".mov", ".mkv", ".avi", ".wmv", ".mpeg", ".mpg"] };
    private static readonly ProjectFileFormat Markdown = new(".md", "Markdown", "M↓", "#7166B5");
    private static readonly ProjectFileFormat Placeholder = new("", "…", "", "#53647B") { IsPlaceholder = true };

    internal static IReadOnlyList<ProjectFileFormat[]> CreateGroups { get; } = [[Text, Json, Csv], [Krl, Lua, CSharp], [Graph, Shader, VisualLua], [Directory, Other, Placeholder]];
    internal static IReadOnlyList<ProjectFileFormat[]> ImportGroups { get; } = [[Text, Json, Krl, Lua], [Graph, Shader, VisualLua, RenPy], [Word, Other, Image, Audio], [Video, Directory, Markdown, Placeholder]];

    private const string Page = "M5,1 H19 L27,9 V31 H5 Z M19,1 V9 H27";
    private const string FolderShape = "M2,7 H13 L16,11 H30 V27 H2 Z";
    internal const string LuaMark = "M11,20 A5,5 0 1 0 21,20 A5,5 0 1 0 11,20 M21,14 A2,2 0 1 0 25,14 A2,2 0 1 0 21,14";

    internal static FrameworkElement Icon(ProjectFileFormat format)
    {
        var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(format.Color));
        var canvas = new System.Windows.Controls.Canvas { Width = 32, Height = 32 };
        canvas.Children.Add(new Path { Data = Geometry.Parse(format.IsDirectory ? FolderShape : Page), Stroke = color, StrokeThickness = 1.5 });
        if (format == Csv)
        {
            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse("M9,14 H23 V25 H9 Z M9,18 H23 M9,21.5 H23 M16,14 V25"),
                Stroke = color, StrokeThickness = 1
            });
        }
        else if (format.Extension is ".lua" or ".vlua")
        {
            var mark = new Path { Data = Geometry.Parse(LuaMark), Fill = color };
            if (format == VisualLua) mark.RenderTransform = new RotateTransform(180, 16, 19);
            canvas.Children.Add(mark);
        }
        else if (!format.IsDirectory)
        {
            var symbol = new System.Windows.Controls.TextBlock { Text = format.Symbol, Foreground = color, FontWeight = FontWeights.Bold, Width = 32, TextAlignment = TextAlignment.Center };
            System.Windows.Controls.Canvas.SetTop(symbol, 12);
            canvas.Children.Add(symbol);
        }
        return canvas;
    }

    internal static string HtmlIcon(ProjectFileFormat format)
    {
        string mark = format.Extension is ".lua" or ".vlua"
            ? $"""<path fill="currentColor" d="{LuaMark}"{(format == VisualLua ? " transform=\"rotate(180 16 19)\"" : "")}/>"""
            : $"""<text x="16" y="25" text-anchor="middle" fill="currentColor" font-size="13" font-weight="bold">{System.Net.WebUtility.HtmlEncode(format.Symbol)}</text>""";
        return $"""<svg aria-hidden="true" width="32" height="32" viewBox="0 0 32 32" style="vertical-align:middle;color:{format.Color}"><path fill="none" stroke="currentColor" stroke-width="1.5" d="{Page}"/>{mark}</svg>""";
    }
}
