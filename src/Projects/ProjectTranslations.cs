using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

internal enum GameTextCategory { Interface, Notifications, MainMenu, Dialogue, Other }

internal sealed record GameTextEntry(GameTextCategory Category, string Text, string Location)
{
    internal string Key => ProjectTranslations.Key(Category, Text);
}

internal sealed class ProjectTranslations
{
    private const string Folder = "translations";
    private const int FileLimit = 16 * 1024 * 1024;
    private readonly IReadOnlyDictionary<string, string> values;
    private ProjectTranslations(IReadOnlyDictionary<string, string> values) => this.values = values;
    internal static ProjectTranslations Empty { get; } = new(new Dictionary<string, string>());
    internal IReadOnlyDictionary<string, string> Values => values;
    internal static string OriginalLanguage(ProjectInfo project) => LanguageCode(project.Configuration.Metadata?.DefaultLanguage) ?? "ru";

    internal static string? LanguageCode(string? value)
    {
        string? code = value?.Trim().ToLowerInvariant();
        return code is { Length: >= 2 and <= 40 } && code.Split('-').All(part => part.Length is >= 1 and <= 8 &&
            part.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))) && char.IsAsciiLetterLower(code[0]) ? code : null;
    }

    internal static string LanguageLabel(string code)
    {
        string name;
        try { name = CultureInfo.GetCultureInfo(code).NativeName; }
        catch (CultureNotFoundException) { return code; }
        if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        return $"{name} - {code}";
    }

    internal static string CategoryLabel(GameTextCategory category) => category switch
    {
        GameTextCategory.Interface => "Текст интерфейса",
        GameTextCategory.Notifications => "Уведомления",
        GameTextCategory.MainMenu => "Главное меню",
        GameTextCategory.Dialogue => "Игровой текст",
        _ => "Прочий текст"
    };

    internal static string Key(GameTextCategory category, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(category + "\n" + text.ReplaceLineEndings("\n"))));

    internal string Translate(GameTextCategory category, string text) =>
        text.Length > 0 && values.Count > 0 && values.TryGetValue(Key(category, text), out var translation) && translation.Length > 0 ? translation : text;

    internal static string[] Languages(ProjectInfo project)
    {
        string source = OriginalLanguage(project);
        string directory = PathSafety.ResolveRelative(project.Path, Folder);
        if (!Directory.Exists(directory)) return [source];
        return Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).Select(LanguageCode)
            .OfType<string>().Where(code => code != source).Distinct().Order(StringComparer.Ordinal).Prepend(source).ToArray();
    }

    internal static ProjectTranslations Load(ProjectInfo project, string? language)
    {
        if (LanguageCode(language) is not { } code || code == OriginalLanguage(project)) return Empty;
        string path = TranslationPath(project, code);
        if (!File.Exists(path)) return Empty;
        if (new FileInfo(path).Length > FileLimit) throw new InvalidDataException("Файл перевода больше 16 МБ.");
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("В файле перевода ожидается объект JSON.");
        Validate(entries);
        return new(entries);
    }

    internal static async Task SaveAsync(ProjectInfo project, string language, IReadOnlyDictionary<string, string> values, CancellationToken token)
    {
        string code = LanguageCode(language) ?? throw new InvalidDataException("Укажите код языка, например en или pt-br.");
        if (code == OriginalLanguage(project)) throw new InvalidDataException("Выберите язык перевода, отличный от исходного.");
        Validate(values);
        string path = TranslationPath(project, code);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(values, ProjectConfiguration.JsonOptions);
            if (data.Length > FileLimit) throw new InvalidDataException("Файл перевода больше 16 МБ.");
            await File.WriteAllBytesAsync(temporary, data, token);
            token.ThrowIfCancellationRequested();
            PathSafety.RejectLinks(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string TranslationPath(ProjectInfo project, string language) =>
        PathSafety.ResolveRelative(project.Path, Folder + "/" + language + ".json");

    private static void Validate(IReadOnlyDictionary<string, string> entries)
    {
        if (entries.Count > 10000 || entries.Any(pair => pair.Key.Length != 64 || !pair.Key.All(char.IsAsciiHexDigit) ||
                pair.Value is null || pair.Value.Length > 16000))
            throw new InvalidDataException("Неверные ключи или размер перевода.");
    }

    internal string ScreenText(ProjectInfo project, GameTextCategory category, string text)
    {
        var game = project.Engine.Game;
        return Translate(category, text)
            .Replace("{{title}}", Translate(GameTextCategory.MainMenu, project.Title))
            .Replace("{{subtitle}}", Translate(GameTextCategory.MainMenu, game.Subtitle))
            .Replace("{{start_label}}", Translate(GameTextCategory.MainMenu, game.StartLabel))
            .Replace("{{continue_label}}", Translate(GameTextCategory.MainMenu, game.ContinueLabel))
            .Replace("{{exit_label}}", Translate(GameTextCategory.MainMenu, game.ExitLabel));
    }
}
