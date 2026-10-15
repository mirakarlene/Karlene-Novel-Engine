namespace KNE;

/// <summary>A category in the visual command library.</summary>
public sealed record BlockGroup(string Id, string Title, string Color);

/// <summary>A selectable command with a description and category color.</summary>
public sealed record BlockDefinition(ScriptNodeKind Kind, string Group, string Title, string Description, string Color);

/// <summary>The grouped library and defaults used when creating a new visual block.</summary>
public static class BlockLibrary
{
    /// <summary>Groups in the author-facing order.</summary>
    public static IReadOnlyList<BlockGroup> Groups { get; } = [
        new("terminals", "Начало и конец", "#64748B"), new("narrative", "Нарратив", "#C65C16"),
        new("logic", "Логика", "#2563A6"), new("visual", "Визуал", "#8246B3"),
        new("audio", "Звук", "#228052"), new("async", "Асинхронность", "#BA3846"),
        new("configurations", "Конфигурации", "#137C83"), new("plugins", "Плагины", "#6B7280")];

    /// <summary>All selectable entries. Conditions appear in both narrative and logic.</summary>
    public static IReadOnlyList<BlockDefinition> Definitions { get; } = Build();

    private static IReadOnlyList<BlockDefinition> Build()
    {
        var entries = new List<BlockDefinition>();
        void Add(string group, ScriptNodeKind kind, string title, string description) => entries.Add(new(kind, group, title, description,
            kind == ScriptNodeKind.Start ? "#FFFFFF" : kind == ScriptNodeKind.End ? "#000000" : Groups.Single(g => g.Id == group).Color));
        Add("terminals", ScriptNodeKind.Start, "Начало", "Точка входа схемы. В одной схеме может быть только одно начало.");
        Add("terminals", ScriptNodeKind.End, "Конец", "Главное меню, специальный экран, закрытие игры или возврат из вызова.");
        Add("narrative", ScriptNodeKind.Dialogue, "Реплика", "Текст в диалоговом окне с именем говорящего или от рассказчика.");
        Add("narrative", ScriptNodeKind.Choice, "Выбор", "Варианты ответа с отдельными выходами.");
        Add("narrative", ScriptNodeKind.Condition, "Условие", "Два продолжения в зависимости от значения переменной.");
        Add("narrative", ScriptNodeKind.Comment, "Комментарий", "Заметка автора. Не выполняется и может оставаться без связей.");
        Add("narrative", ScriptNodeKind.Title, "Титр", "Текст поверх сцены вне диалогового окна. Пустой текст скрывает титр.");
        Add("narrative", ScriptNodeKind.GotoScene, "Перейти на сцену", "goto: перейти в другую функцию без возврата в эту ветку.");
        Add("narrative", ScriptNodeKind.CallScene, "Вызвать сцену", "call: выполнить другую функцию и продолжить после вызова.");
        Add("logic", ScriptNodeKind.Condition, "Условие", "Сравнить переменную с числом, строкой или логическим значением.");
        Add("logic", ScriptNodeKind.SetVariable, "Переменная", "Записать значение в state.");
        Add("logic", ScriptNodeKind.Switch, "Switch / Case", "Сравнить переменную с вариантами; предусмотрен выход по умолчанию.");
        Add("logic", ScriptNodeKind.While, "Цикл While", "Повторять тело, пока условие истинно. Конец тела возвращает к проверке.");
        Add("logic", ScriptNodeKind.ForEach, "Цикл For Each", "Перебрать значения коллекции state и выполнить тело для каждого.");
        Add("logic", ScriptNodeKind.TryCatch, "Try / Catch", "При ошибке в теле перейти в обработчик; затем продолжить.");
        Add("logic", ScriptNodeKind.SetFlag, "Установить флаг", "Записать true или false в логическую переменную.");
        Add("logic", ScriptNodeKind.CheckFlag, "Проверить флаг", "Выбрать ветку по логическому значению.");
        Add("logic", ScriptNodeKind.Random, "Случайное значение", "Число от A до B; целые границы дают целое значение. Сохранить в state.");
        Add("visual", ScriptNodeKind.Background, "Отобразить фон", "Показать изображение из проекта.");
        Add("visual", ScriptNodeKind.HideBackground, "Скрыть фон", "Вернуть фон из оформления проекта.");
        Add("visual", ScriptNodeKind.Character, "Отобразить спрайт", "Показать персонажа слева, по центру или справа.");
        Add("visual", ScriptNodeKind.HideCharacter, "Скрыть спрайт", "Убрать персонажа со сцены.");
        Add("visual", ScriptNodeKind.MoveBackground, "Передвинуть фон", "Переместить фон на X/Y за указанное время.");
        Add("visual", ScriptNodeKind.MoveCharacter, "Передвинуть спрайт", "Переместить спрайт на X/Y за указанное время.");
        Add("visual", ScriptNodeKind.Animation, "Анимация", "Пульсация, покачивание или появление фона/спрайта.");
        Add("visual", ScriptNodeKind.Shake, "Тряска экрана", "Сила и длительность тряски сцены.");
        Add("visual", ScriptNodeKind.Flash, "Вспышка экрана", "Цвет и длительность вспышки поверх сцены.");
        Add("visual", ScriptNodeKind.Camera, "Камера · Zoom / Pan", "Масштаб и смещение камеры за указанное время.");
        Add("visual", ScriptNodeKind.ShowScreen, "Показать экран", "Показать дополнительный экран оформления поверх игровой сцены.");
        Add("audio", ScriptNodeKind.AudioChannel, "Настроить канал", "Громкость и повтор канала music, sfx, voice или ambient.");
        Add("audio", ScriptNodeKind.Music, "Включить музыку", "Проиграть музыкальный ресурс в выбранном канале.");
        Add("audio", ScriptNodeKind.StopMusic, "Остановить музыку", "Остановить канал music.");
        Add("audio", ScriptNodeKind.Sound, "Включить звук", "Проиграть звуковой эффект в выбранном канале.");
        Add("audio", ScriptNodeKind.StopSound, "Остановить звук", "Остановить выбранный канал.");
        Add("audio", ScriptNodeKind.Voice, "Озвучка", "Проиграть реплику озвучки в канале voice.");
        Add("audio", ScriptNodeKind.MasterVolume, "Общая громкость", "Общий множитель громкости от 0 до 1.");
        Add("async", ScriptNodeKind.Parallel, "Параллель", "Запустить ветки совместно и дождаться завершения всех.");
        Add("async", ScriptNodeKind.Async, "Асинхронность", "Запустить фоновые ветки и продолжить, сохранив их идентификатор.");
        Add("async", ScriptNodeKind.Join, "Join · ожидание веток", "Дождаться фоновых веток по сохранённому идентификатору.");
        Add("async", ScriptNodeKind.Timer, "Таймер", "Продолжить после заданного времени без блокировки окна.");
        Add("async", ScriptNodeKind.Pause, "Пауза", "Приостановить текущую ветку на указанное время.");
        Add("async", ScriptNodeKind.WaitCondition, "Ожидание условия", "Ждать сравнения переменной; время задаёт предел ожидания.");
        Add("visual", ScriptNodeKind.ClearBackgrounds, "Удалить все фоны", "Убрать изображения, стандартный фон и их анимации.");
        Add("visual", ScriptNodeKind.ClearSprites, "Удалить все спрайты", "Убрать все спрайты и сбросить их перемещения и анимации.");
        Add("configurations", ScriptNodeKind.ConfigureDialogue, "Текстовое окно", "Положение, размеры, шрифт, цвета, прозрачность и текстура диалога.");
        Add("configurations", ScriptNodeKind.ConfigureButton, "Кнопка", "Внешний вид и положение игровых кнопок и вариантов выбора.");
        Add("plugins", ScriptNodeKind.IncludeLua, "Подключить .lua", "Подключить внешний Lua-файл один раз за игровую сессию.");
        Add("plugins", ScriptNodeKind.IncludeCs, "Подключить .cs", "Скомпилировать и подключить C#-плагин проекта.");
        Add("plugins", ScriptNodeKind.Script, "Скрипт", "Создать или открыть скрипт в настроенном внешнем редакторе; выполнять при входе в блок.");
        return entries.AsReadOnly();
    }

    /// <summary>Creates a block with runnable argument defaults; its ports still need connections.</summary>
    public static ScriptNode Create(ScriptNodeKind kind, string? category = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (category != null && !Definitions.Any(entry => entry.Kind == kind && entry.Group == category))
            throw new ArgumentException("Блок недоступен в выбранной группе.", nameof(category));
        var node = new ScriptNode { Kind = kind };
        node.Category = category ?? Definitions.First(entry => entry.Kind == kind).Group;
        if (kind == ScriptNodeKind.Dialogue) node.Text = "Новая реплика";
        if (kind == ScriptNodeKind.Comment) node.Text = "Комментарий автора";
        if (kind == ScriptNodeKind.Title) node.Text = "Новый титр";
        if (kind == ScriptNodeKind.Choice) { node.Text = "Выберите вариант"; node.Options = [new() { Text = "Да" }, new() { Text = "Нет" }]; }
        if (kind == ScriptNodeKind.Switch) node.Options = [new() { Text = "0" }, new() { Text = "1" }];
        if (kind is ScriptNodeKind.Parallel or ScriptNodeKind.Async) { node.Options = [new() { Text = "Ветка 1" }, new() { Text = "Ветка 2" }]; node.Variable = "task"; }
        if (kind == ScriptNodeKind.Join) node.Variable = "task";
        if (kind is ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag) { node.ValueKind = ScriptValueKind.Boolean; node.Value = "true"; node.Variable = "flag"; }
        if (kind == ScriptNodeKind.ForEach) { node.Variable = "item"; node.Path = "items"; }
        if (kind == ScriptNodeKind.TryCatch) node.Variable = "last_error";
        if (kind == ScriptNodeKind.WaitCondition) node.Duration = 30;
        if (kind == ScriptNodeKind.Animation) { node.Text = "pulse"; node.Amount = 1.06; }
        if (kind == ScriptNodeKind.Flash) node.Value = "#FFFFFFFF";
        if (kind == ScriptNodeKind.Camera) node.Amount = 1;
        if (kind == ScriptNodeKind.Shake) node.Amount = 12;
        if (kind is ScriptNodeKind.Sound or ScriptNodeKind.StopSound) node.Channel = "sfx";
        if (kind == ScriptNodeKind.Voice) node.Channel = "voice";
        if (kind == ScriptNodeKind.End) node.EndAction = ScriptEndAction.Menu;
        if (kind is ScriptNodeKind.ConfigureDialogue or ScriptNodeKind.ConfigureButton)
        {
            node.UiTarget = kind == ScriptNodeKind.ConfigureDialogue ? "dialogue" : "next";
            node.Ui = GameplayUiStyle.ForTarget(node.UiTarget);
        }
        return node;
    }

    /// <summary>The header color for an existing block.</summary>
    public static string ColorFor(ScriptNode node) => node.Kind is ScriptNodeKind.Start or ScriptNodeKind.End || node.Category.Length == 0
        ? Definitions.First(entry => entry.Kind == node.Kind).Color : Groups.Single(group => group.Id == node.Category).Color;
}
