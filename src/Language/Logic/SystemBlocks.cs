namespace KNE;

internal sealed record SystemBlock(string Function, string Title, string[] Parameters, string Result, string Description);

internal static class SystemBlocks
{
    internal static readonly IReadOnlyDictionary<ScriptNodeKind, SystemBlock> All = new Dictionary<ScriptNodeKind, SystemBlock>
    {
        [ScriptNodeKind.PersistentSet] = new("persistent_set", "Установить глобальную переменную", ["key", "value"], "", "Записать значение, которое будет доступно во всех сохранениях и следующих запусках игры."),
        [ScriptNodeKind.PersistentGet] = new("persistent_get", "Получить глобальную переменную", ["key"], "state", "Считать глобальное значение и записать его в переменную сценария."),
        [ScriptNodeKind.PersistentHas] = new("persistent_has", "Проверить глобальную переменную", ["key"], "state", "Проверить, существует ли глобальная переменная. Возвращает «да» и для сохранённого значения false."),
        [ScriptNodeKind.PersistentClear] = new("persistent_clear", "Очистить все глобальные переменные", [], "", "После подтверждения игроком очистить все глобальные переменные и счётчик запусков."),
        [ScriptNodeKind.PersistentDelete] = new("persistent_delete", "Очистить глобальную переменную", ["key"], "", "Удалить одну глобальную переменную по имени."),
        [ScriptNodeKind.SaveNamed] = new("save_named", "Принудительное сохранение", ["slot"], "", "Сохранить игру в указанный слот. После загрузки сценарий продолжится со следующего блока."),
        [ScriptNodeKind.SaveExists] = new("save_exists", "Проверить слот сохранения", ["slot"], "state", "Проверить, существует ли указанный слот сохранения."),
        [ScriptNodeKind.SaveRead] = new("save_read", "Получить данные из сохранения", ["slot", "key"], "state", "Считать значение переменной из указанного слота без загрузки сцены."),
        [ScriptNodeKind.SaveDelete] = new("save_delete", "Удалить сохранение", ["slot"], "", "Удалить указанный слот сохранения этой игры."),
        [ScriptNodeKind.SaveList] = new("save_list", "Получить список сохранений", [], "list", "Записать имена доступных слотов в список."),
        [ScriptNodeKind.RealTime] = new("real_time", "Получить реальное время", [], "state", "Получить текущий час, минуту и день недели по системным часам."),
        [ScriptNodeKind.RealDate] = new("real_date", "Получить реальную дату", ["format"], "state", "Получить текущую дату и строку в выбранном формате."),
        [ScriptNodeKind.PlayTime] = new("play_time", "Получить время игры", [], "state", "Получить число активных минут в текущем прохождении."),
        [ScriptNodeKind.BuildVersion] = new("build_version", "Получить версию билда", [], "state", "Получить версию игры из метаданных проекта."),
        [ScriptNodeKind.LaunchCount] = new("launch_count", "Получить количество запусков", [], "state", "Получить число запусков игры из глобального хранилища."),
        [ScriptNodeKind.RestartGame] = new("restart_game", "Перезапустить игру", [], "", "Перезапустить игру и заново загрузить её настройки."),
        [ScriptNodeKind.ExitGame] = new("exit_game", "Выход на рабочий стол", [], "", "Закрыть игровое окно и завершить текущую сессию."),
        [ScriptNodeKind.MainMenu] = new("return_to_menu", "Вернуться в главное меню", [], "", "Завершить текущую сессию и открыть главное меню."),
        [ScriptNodeKind.MinimizeGame] = new("minimize_game", "Свернуть окно", [], "", "Свернуть игровое окно в панель задач."),
        [ScriptNodeKind.FullscreenGame] = new("fullscreen_game", "Развернуть на весь экран", [], "", "Перевести игру в полноэкранный режим."),
        [ScriptNodeKind.WindowedGame] = new("windowed_game", "Вернуться в оконный режим", [], "", "Вернуть игру из полноэкранного режима в окно."),
        [ScriptNodeKind.OpenBrowser] = new("open_browser", "Открыть ссылку в браузере", ["url"], "", "После разрешения игрока открыть ссылку HTTP или HTTPS в браузере."),
        [ScriptNodeKind.ClipboardWrite] = new("clipboard_write", "Копировать в буфер обмена", ["text"], "", "После разрешения игрока скопировать текст в системный буфер обмена."),
        [ScriptNodeKind.ClipboardRead] = new("clipboard_read", "Получить текст из буфера обмена", [], "state", "После разрешения игрока считать текст из системного буфера обмена."),
        [ScriptNodeKind.Screenshot] = new("take_screenshot", "Сделать скриншот", ["path"], "state", "После разрешения игрока сохранить снимок игрового окна в PNG внутри проекта."),
        [ScriptNodeKind.FileRead] = new("read_text_file", "Прочитать текстовый файл", ["path"], "state", "Считать текст из файла UTF-8 в переменную. Действуют разрешения и границы доступа проекта."),
        [ScriptNodeKind.FileWrite] = new("write_text_file", "Записать / Дописать в файл", ["path", "text", "mode"], "", "Создать файл UTF-8, заменить его содержимое или дописать текст."),
        [ScriptNodeKind.PathExists] = new("path_exists", "Проверить существование файла/папки", ["path"], "state", "Проверить, существует ли файл или папка по указанному пути."),
        [ScriptNodeKind.FileSize] = new("file_size", "Получить размер файла", ["path", "unit"], "state", "Получить размер файла в байтах или выбранной единице."),
        [ScriptNodeKind.FileList] = new("list_files", "Получить список файлов в папке", ["path"], "list", "Записать имена файлов указанной папки в список."),
        [ScriptNodeKind.DirectoryCreate] = new("create_folder", "Создать папку", ["path"], "", "Создать папку по указанному пути."),
        [ScriptNodeKind.ProjectPath] = new("project_path", "Получить путь к папке проекта", [], "state", "Получить абсолютный путь к корневой папке игры."),
        [ScriptNodeKind.FreezeScan] = new("freeze_and_scan", "Заморозить и сканировать", ["unfreeze", "seconds"], "state", "Приостановить игру, проверить проект и показать отчёт до выбранного сигнала продолжения."),
        [ScriptNodeKind.DeviceInfo] = new("device_info", "Инфо о устройстве", [], "state", "Получить название операционной системы и записать его в переменную сценария."),
        [ScriptNodeKind.ShowFps] = new("show_fps", "Скрыть/Показать FPS", ["visible"], "", "Показать или скрыть счётчик частоты кадров в игровом окне."),
        [ScriptNodeKind.Watermark] = new("watermark", "Водяной знак", ["text", "position", "opacity"], "", "Показать текст поверх игры в выбранном положении и с заданной непрозрачностью. Пустой текст скрывает знак."),
        [ScriptNodeKind.ShowInput] = new("show_input", "Отображение ввода", ["visible"], "", "Показать или скрыть последнюю нажатую клавишу или кнопку мыши в игровом окне."),
        [ScriptNodeKind.GlobalTimer] = new("global_timer", "Глобальный таймер", ["action", "clock_format"], "", "Показать, скрыть или обнулить счётчик активного времени игры в выбранном формате."),
        [ScriptNodeKind.BuildInfo] = new("build_info", "Инфо о билде", [], "state", "Записать название, версию, автора, команду и формат сборки в переменную сценария."),
        [ScriptNodeKind.ConnectFont] = new("connect_font", "Подключить шрифт", ["font_path", "alias"], "", "Подключить файл TTF или OTF из проекта под именем для использования в репликах."),
        [ScriptNodeKind.ShowVideo] = new("show_video", "Показать видео", ["video_path", "video_mode", "video_x", "video_y", "video_width", "video_height", "video_allow_skip", "video_skip_key"], "",
            "Воспроизвести видеоролик из проекта на всём игровом экране или в выбранной области."),
        [ScriptNodeKind.EnableConsole] = new("enable_console", "Включить консоль", [], "", "Открыть игровую консоль и разрешить её вызов настроенным сочетанием клавиш."),
        [ScriptNodeKind.DisableConsole] = new("disable_console", "Отключить консоль", [], "", "Закрыть игровую консоль и отключить её вызов до следующего включения."),
        [ScriptNodeKind.GlobalPause] = new("global_pause", "Глобальная пауза", ["seconds", "allow_test_resume"], "", "Приостановить выполнение и ввод в игре на указанное время. Значение −1 задаёт бессрочную паузу."),
    };

    internal static string? Note(ScriptNodeKind kind) => kind switch
    {
        ScriptNodeKind.EnableConsole or ScriptNodeKind.DisableConsole => "Этот блок игнорирует правило безопасности в настройках проекта",
        ScriptNodeKind.GlobalPause => "Ни в коем случае не ставьте -1 если не знаете как разморозить",
        ScriptNodeKind.ShowVideo => "Координаты области задаются для экрана 1280×720. Доступные форматы зависят от установленных кодеков Windows.",
        _ => null
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
        "visible" or "video_allow_skip" => "true",
        "allow_test_resume" => "false",
        "video_skip_key" => "Escape",
        "opacity" => "100",
        "position" => "top_right",
        "action" => "show",
        "clock_format" => "HH:mm:ss",
        "alias" => "dialogue_font",
        "path" => ".",
        "video_path" => "game/video/intro.mp4",
        "video_mode" => "fullscreen",
        "video_x" or "video_y" => "0",
        "video_width" => "640",
        "video_height" => "360",
        _ => ""
    };
    internal static LogicOperand DefaultOperand(string parameter) =>
        new(parameter switch
        {
            "seconds" or "opacity" or "video_x" or "video_y" or "video_width" or "video_height" => "number",
            "visible" or "video_allow_skip" or "allow_test_resume" => "boolean",
            _ => "string"
        }, Default(parameter));

    internal static string? LiteralVideoPath(ScriptNode node) =>
        (node.SystemArguments?.GetValueOrDefault("video_path") ?? DefaultOperand("video_path")) is { Kind: "string" } path
            ? path.Value : null;

    internal static string Compile(ScriptNode node)
    {
        var block = All[node.Kind];
        var parameters = block.Parameters.AsEnumerable();
        if (node.Kind == ScriptNodeKind.ShowVideo)
            parameters = (node.SystemArguments?.GetValueOrDefault("video_mode")?.Value ?? "fullscreen") switch
            {
                "fullscreen" => ["video_path"],
                "area" => ["video_path", "video_x", "video_y", "video_width", "video_height"],
                _ => throw new ArgumentException("Выберите полноэкранный режим или область видео.")
            };
        var arguments = parameters.Select(key =>
        {
            var value = node.SystemArguments?.GetValueOrDefault(key) ?? DefaultOperand(key);
            if (value.Value is { Length: > 16000 }) throw new ArgumentException("Аргумент блока длиннее 16 000 символов.");
            return LogicCompiler.Operand(value);
        });
        if (node.Kind == ScriptNodeKind.ShowVideo)
        {
            bool skip = (node.SystemArguments?.GetValueOrDefault("video_allow_skip")?.Value ?? "true") == "true";
            string key = node.SystemArguments?.GetValueOrDefault("video_skip_key")?.Value ?? "Escape";
            if (!skip) arguments = arguments.Append("false");
            else if (key != "Escape")
            {
                _ = VideoSkipShortcut.Parse(key);
                arguments = arguments.Append(VisualScript.Quote(key));
            }
        }
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
    internal static bool IsPresentation(string name) => name is "show_fps" or "watermark" or "show_input" or "global_timer" or "connect_font";
}
