namespace KNE;

internal sealed record SystemBlock(string Function, string Title, string[] Parameters, string Result);

internal static class SystemBlocks
{
    internal static readonly IReadOnlyDictionary<ScriptNodeKind, SystemBlock> All = new Dictionary<ScriptNodeKind, SystemBlock>
    {
        [ScriptNodeKind.PersistentSet] = new("persistent_set", "Установить глобальную переменную", ["key", "value"], ""),
        [ScriptNodeKind.PersistentGet] = new("persistent_get", "Получить глобальную переменную", ["key"], "state"),
        [ScriptNodeKind.PersistentHas] = new("persistent_has", "Проверить глобальную переменную", ["key"], "state"),
        [ScriptNodeKind.PersistentClear] = new("persistent_clear", "Очистить все глобальные переменные", [], ""),
        [ScriptNodeKind.PersistentDelete] = new("persistent_delete", "Очистить глобальную переменную", ["key"], ""),
        [ScriptNodeKind.SaveNamed] = new("save_named", "Принудительное сохранение", ["slot"], ""),
        [ScriptNodeKind.SaveExists] = new("save_exists", "Проверить слот сохранения", ["slot"], "state"),
        [ScriptNodeKind.SaveRead] = new("save_read", "Получить данные из сохранения", ["slot", "key"], "state"),
        [ScriptNodeKind.SaveDelete] = new("save_delete", "Удалить сохранение", ["slot"], ""),
        [ScriptNodeKind.SaveList] = new("save_list", "Получить список сохранений", [], "list"),
        [ScriptNodeKind.RealTime] = new("real_time", "Получить реальное время", [], "state"),
        [ScriptNodeKind.RealDate] = new("real_date", "Получить реальную дату", ["format"], "state"),
        [ScriptNodeKind.PlayTime] = new("play_time", "Получить время игры", [], "state"),
        [ScriptNodeKind.BuildVersion] = new("build_version", "Получить версию билда", [], "state"),
        [ScriptNodeKind.LaunchCount] = new("launch_count", "Получить количество запусков", [], "state"),
        [ScriptNodeKind.RestartGame] = new("restart_game", "Перезапустить игру", [], ""),
        [ScriptNodeKind.ExitGame] = new("exit_game", "Выход на рабочий стол", [], ""),
        [ScriptNodeKind.MainMenu] = new("return_to_menu", "Вернуться в главное меню", [], ""),
        [ScriptNodeKind.MinimizeGame] = new("minimize_game", "Свернуть окно", [], ""),
        [ScriptNodeKind.FullscreenGame] = new("fullscreen_game", "Развернуть на весь экран", [], ""),
        [ScriptNodeKind.WindowedGame] = new("windowed_game", "Вернуться в оконный режим", [], ""),
        [ScriptNodeKind.OpenBrowser] = new("open_browser", "Открыть ссылку в браузере", ["url"], ""),
        [ScriptNodeKind.ClipboardWrite] = new("clipboard_write", "Копировать в буфер обмена", ["text"], ""),
        [ScriptNodeKind.ClipboardRead] = new("clipboard_read", "Получить текст из буфера обмена", [], "state"),
        [ScriptNodeKind.Screenshot] = new("take_screenshot", "Сделать скриншот", ["path"], "state"),
        [ScriptNodeKind.FileRead] = new("read_text_file", "Прочитать текстовый файл", ["path"], "state"),
        [ScriptNodeKind.FileWrite] = new("write_text_file", "Записать / Дописать в файл", ["path", "text", "mode"], ""),
        [ScriptNodeKind.PathExists] = new("path_exists", "Проверить существование файла/папки", ["path"], "state"),
        [ScriptNodeKind.FileSize] = new("file_size", "Получить размер файла", ["path", "unit"], "state"),
        [ScriptNodeKind.FileList] = new("list_files", "Получить список файлов в папке", ["path"], "list"),
        [ScriptNodeKind.DirectoryCreate] = new("create_folder", "Создать папку", ["path"], ""),
        [ScriptNodeKind.ProjectPath] = new("project_path", "Получить путь к папке проекта", [], "state"),
        [ScriptNodeKind.FreezeScan] = new("freeze_and_scan", "Заморозить и сканировать", ["unfreeze", "seconds"], "state"),
    };

    internal static string Default(string parameter) => parameter switch
    {
        "key" => "player_name",
        "slot" => "BeforeChapter3",
        "format" => "dd.MM.yyyy",
        "mode" => "write",
        "unit" => "bytes",
        "unfreeze" => "left",
        "seconds" => "5",
        "path" => ".",
        _ => ""
    };

    internal static string Compile(ScriptNode node)
    {
        var block = All[node.Kind];
        var arguments = block.Parameters.Select(key =>
        {
            var value = node.SystemArguments?.GetValueOrDefault(key) ?? new(key == "seconds" ? "number" : "string", Default(key));
            if (value.Value is { Length: > 16000 }) throw new ArgumentException("Аргумент блока длиннее 16 000 символов.");
            return LogicCompiler.Operand(value);
        });
        string call = block.Function + "(" + string.Join(", ", arguments) + ")";
        if (block.Result == "") return call;
        if (!LuaSource.IsIdentifier(node.Variable) || node.Variable.Length > 80)
            throw new ArgumentException("Укажите корректное имя переменной результата.");
        string name = VisualScript.Quote(node.Variable);
        return block.Result == "list" ? $"kne_system_list({name}, {call})" : $"state[{name}] = {call}";
    }

    internal static readonly HashSet<string> FileFunctions = new(All.Where(pair =>
        pair.Key is >= ScriptNodeKind.FileRead and <= ScriptNodeKind.DirectoryCreate).Select(pair => pair.Value.Function), StringComparer.Ordinal);
    internal static bool UsesFileSystem(string function) => FileFunctions.Contains(function) || function is "project_path" or "freeze_and_scan";
    internal static bool EndsSession(ScriptNodeKind kind) => kind is ScriptNodeKind.RestartGame or ScriptNodeKind.ExitGame or ScriptNodeKind.MainMenu;
    internal static readonly HashSet<string> FunctionNames = new(All.Values.Select(block => block.Function), StringComparer.Ordinal);
}
