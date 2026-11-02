using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace KNE;

internal sealed record EditorAction(string Id, string Category, string Title, string DefaultKeys = "");

/// <summary>Stable action identifiers and validated, portable shortcut profiles.</summary>
internal static class EditorHotkeys
{
    internal static readonly string[] Categories = ["Файл", "Редактирование", "Навигация", "Блоки", "Отладка", "Общие"];
    internal static readonly EditorAction[] Actions =
    [
        new("file.save", "Файл", "Сохранить текущий файл", "Ctrl+S"), new("file.save_all", "Файл", "Сохранить все файлы"),
        new("file.open", "Файл", "Открыть проект"), new("file.new", "Файл", "Создать файл"), new("file.close", "Файл", "Закрыть вкладку"),
        new("edit.undo", "Редактирование", "Отменить", "Ctrl+Z"), new("edit.redo", "Редактирование", "Повторить", "Ctrl+X;Ctrl+Y"),
        new("edit.copy", "Редактирование", "Копировать", "Ctrl+C"), new("edit.paste", "Редактирование", "Вставить", "Ctrl+V"),
        new("navigation.files", "Навигация", "Список файлов"), new("navigation.scenes", "Навигация", "Навигатор сцен"), new("navigation.refresh", "Навигация", "Обновить проект"),
        new("blocks.search", "Блоки", "Поиск блока на схеме", "Tab"), new("blocks.library", "Блоки", "Библиотека блоков"),
        new("blocks.duplicate", "Блоки", "Дублировать выделенные блоки", "Ctrl+D"), new("blocks.delete", "Блоки", "Удалить выделенные блоки", "Delete"),
        new("debug.run", "Отладка", "Запустить проект"), new("debug.test", "Отладка", "Тестировать сцену"), new("debug.analyze", "Отладка", "Анализ проекта"), new("debug.build", "Отладка", "Собрать релиз"),
        new("general.settings", "Общие", "Настройки программы"), new("general.help", "Общие", "Помощь")
    ];
    internal static readonly DependencyProperty ProfileProperty = DependencyProperty.RegisterAttached("Profile", typeof(Dictionary<string, string>), typeof(EditorHotkeys), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    internal static Dictionary<string, string>? GetProfile(DependencyObject owner) => (Dictionary<string, string>?)owner.GetValue(ProfileProperty);
    internal static void SetProfile(DependencyObject owner, Dictionary<string, string> value) => owner.SetValue(ProfileProperty, value);
    internal static string Keys(EditorAction action, IReadOnlyDictionary<string, string>? profile) => profile?.TryGetValue(action.Id, out string? value) == true ? value : action.DefaultKeys;

    internal static string? Match(Key key, ModifierKeys modifiers, IReadOnlyDictionary<string, string>? profile) =>
        Actions.FirstOrDefault(action => Keys(action, profile).Split(';', StringSplitOptions.RemoveEmptyEntries).Any(text => Parse(text) == (key, modifiers)))?.Id;

    internal static string Format(Key key, ModifierKeys modifiers)
    {
        if (key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            throw new InvalidDataException("Нажмите основную клавишу сочетания.");
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24) && key is not (Key.Tab or Key.Delete or Key.Insert or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Escape))
            throw new InvalidDataException("Для этой клавиши добавьте Ctrl, Alt или Shift.");
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
            (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + key;
    }

    private static (Key, ModifierKeys) Parse(string text)
    {
        var parts = text.Split('+'); ModifierKeys modifiers = ModifierKeys.None;
        if (parts.Length > 5 || !Enum.TryParse<Key>(parts[^1], true, out var key) || !Enum.IsDefined(key)) throw new InvalidDataException("Некорректная клавиша: " + text);
        foreach (string part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch { "ctrl" => ModifierKeys.Control, "alt" => ModifierKeys.Alt, "shift" => ModifierKeys.Shift, "win" => ModifierKeys.Windows, _ => throw new InvalidDataException("Неизвестный модификатор: " + part) };
            if (modifiers.HasFlag(modifier)) throw new InvalidDataException("Повторяющийся модификатор.");
            modifiers |= modifier;
        }
        _ = Format(key, modifiers); return (key, modifiers);
    }

    internal static Dictionary<string, string> Validate(IReadOnlyDictionary<string, string> profile)
    {
        if (profile.Count > Actions.Length || profile.Keys.Any(id => !Actions.Any(action => action.Id == id))) throw new InvalidDataException("В профиле есть неизвестные действия.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in profile)
        {
            if (pair.Value == null || pair.Value.Length > 300) throw new InvalidDataException("Некорректное сочетание.");
            result[pair.Key] = string.Join(';', pair.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(text => { var (key, modifiers) = Parse(text); return Format(key, modifiers); }).Distinct());
        }
        var used = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in Actions)
            foreach (string chord in Keys(action, result).Split(';', StringSplitOptions.RemoveEmptyEntries))
                if (!used.TryAdd(chord, action.Title)) throw new InvalidDataException($"Сочетание {chord} уже назначено: {used[chord]}.");
        return result;
    }

    private sealed record ProfileDocument(int Version, Dictionary<string, string> Bindings);
    internal static string Export(IReadOnlyDictionary<string, string> profile) => JsonSerializer.Serialize(new ProfileDocument(1, Actions.ToDictionary(action => action.Id, action => Keys(action, Validate(profile)))), ProjectConfiguration.JsonOptions);
    internal static Dictionary<string, string> Import(string json)
    {
        if (json.Length > 32768) throw new InvalidDataException("Профиль больше 32 КБ.");
        try
        {
            var value = JsonSerializer.Deserialize<ProfileDocument>(json) ?? throw new InvalidDataException("Пустой профиль.");
            if (value.Version != 1 || value.Bindings == null) throw new InvalidDataException("Неподдерживаемый формат профиля.");
            return Validate(value.Bindings);
        }
        catch (JsonException error) { throw new InvalidDataException("Ошибка JSON профиля.", error); }
    }
}
