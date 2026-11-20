using System.IO;
using System.Windows.Media;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void SetWatermark(string source, string position, object? percent)
    {
        if (position is not ("top_left" or "top_center" or "top_right" or "center_left" or "center" or "center_right" or "bottom_left" or "bottom_center" or "bottom_right"))
            throw new ArgumentException("Выберите одно из девяти положений водяного знака.");
        double opacity = percent == null ? 100 : NumericValue(percent);
        if (opacity is < 0 or > 100) throw new ArgumentException("Непрозрачность должна быть от 0 до 100.");
        if (source.Length > 16000) throw new ArgumentException("Допускается до 16 000 символов.");
        string markup = InlineText.ToMarkup(ResolveInline(source));
        lock (stateLock) visuals = visuals with { Watermark = markup.Length == 0 ? null : new(markup, position, opacity / 100) };
    }

    private void SetGlobalTimer(string action, string format, object? elapsed)
    {
        if (format is not ("HH:mm:ss" or "mm:ss:fff")) throw new ArgumentException("Выберите формат HH:mm:ss или mm:ss:fff.");
        double seconds = NumericValue(elapsed);
        lock (stateLock)
        {
            var previous = visuals.GlobalTimer;
            visuals = visuals with
            {
                GlobalTimer = action switch
                {
                    "show" => new(true, format, previous?.StartSeconds ?? seconds),
                    "hide" => previous is null ? null : previous with { Visible = false },
                    "reset" => new(previous?.Visible ?? true, format, seconds),
                    _ => throw new ArgumentException("Действие с таймером: show, hide или reset.")
                }
            };
        }
    }

    private void ConnectFont(string relative, string alias)
    {
        if (alias.Length > 80 || !LuaSource.IsIdentifier(alias))
            throw new ArgumentException("Имя шрифта должно быть идентификатором Lua длиной до 80 символов.");
        string path = PathSafety.ResolveRelative(project.Path, relative);
        if (Path.GetExtension(path).ToLowerInvariant() is not (".ttf" or ".otf") || !File.Exists(path))
            throw new ArgumentException("Выберите существующий файл TTF или OTF внутри проекта.");
        var typeface = new GlyphTypeface(new Uri(path));
        if (typeface.FamilyNames.Count == 0) throw new ArgumentException("У файла шрифта отсутствует имя семейства.");
        lock (stateLock)
        {
            if (visuals.Fonts.Count >= 128 && !visuals.Fonts.ContainsKey(alias))
                throw new ArgumentException("Допускается до 128 подключённых шрифтов.");
            var fonts = new Dictionary<string, string>(visuals.Fonts, StringComparer.Ordinal) { [alias] = Path.GetRelativePath(project.Path, path) };
            visuals = visuals with { Fonts = fonts };
        }
    }

    private static double NumericValue(object? value)
    {
        if (value is long or int or double && double.IsFinite(Convert.ToDouble(value)))
            return Convert.ToDouble(value);
        throw new ArgumentException("Ожидается конечное число.");
    }
}
