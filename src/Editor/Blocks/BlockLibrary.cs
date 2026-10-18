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
        new("audio", "Звук", "#228052"), new("flows", "Потоки", "#C44A88"), new("calculations", "Вычисления", "#83CFF0"), new("async", "Асинхронность", "#BA3846"), new("triggers", "Триггеры", "#8B5A35"),
        new("configurations", "Конфигурации", "#137C83"), new("plugins", "Плагины", "#6B7280")];

    /// <summary>All selectable entries with distinct narrative branching and logic comparisons.</summary>
    public static IReadOnlyList<BlockDefinition> Definitions { get; } = Build();

    private static IReadOnlyList<BlockDefinition> Build()
    {
        var entries = new List<BlockDefinition>();
        void Add(string group, ScriptNodeKind kind, string title, string description) => entries.Add(new(kind, group, title, description,
            kind == ScriptNodeKind.Start ? "#FFFFFF" : kind == ScriptNodeKind.End ? "#000000" : Groups.Single(g => g.Id == group).Color));
        Add("terminals", ScriptNodeKind.Start, "Начало", "Точка входа схемы. В одной схеме может быть только одно начало.");
        Add("terminals", ScriptNodeKind.End, "Конец", "Главное меню, специальный экран, закрытие игры или возврат из вызова.");
        Add("narrative", ScriptNodeKind.Dialogue, "Реплика", "Текст в диалоговом окне с именем говорящего или от рассказчика.");
        Add("narrative", ScriptNodeKind.TextInput, "Ввод текста", "Запросить ответ игрока, сохранить в переменную и выбрать ветку по результату.");
        Add("narrative", ScriptNodeKind.Choice, "Выбор", "Варианты ответа с отдельными выходами и условной блокировкой.");
        Add("narrative", ScriptNodeKind.TimedChoice, "Выбор с таймером", "Автоматический выбор по истечении времени; полоса или числовой таймер.");
        Add("narrative", ScriptNodeKind.Branching, "Ветвление", "Выбрать ветку по переменной и заданному значению; не добавляет реплику.");
        Add("narrative", ScriptNodeKind.Comment, "Комментарий", "Заметка автора. Не выполняется и может оставаться без связей.");
        Add("narrative", ScriptNodeKind.Title, "Титр", "Текст поверх сцены вне диалогового окна. Пустой текст скрывает титр.");
        Add("flows", ScriptNodeKind.GotoScene, "Перейти на сцену", "goto: перейти в другую функцию без возврата в эту ветку.");
        Add("flows", ScriptNodeKind.CallScene, "Вызвать сцену", "call: выполнить другую функцию и продолжить после вызова.");
        Add("flows", ScriptNodeKind.Subsystem, "Подсистема", "Вложенная блок-схема из файла: ждать завершения или продолжать параллельно. Двойной клик открывает / создаёт файл.");
        foreach (var kind in Enum.GetValues<ScriptNodeKind>().Where(CalculationCompiler.IsCalculation))
        {
            string description = kind switch {
                ScriptNodeKind.Arithmetic => "Сложить, вычесть, умножить или разделить два выражения.",
                ScriptNodeKind.Modulo => "Остаток A % B; делитель не может быть нулём.",
                ScriptNodeKind.Increment => "Увеличить число или переменную на 1.", ScriptNodeKind.Decrement => "Уменьшить число или переменную на 1.",
                ScriptNodeKind.Pow => "Возвести A в степень B.", ScriptNodeKind.Sqrt => "Квадратный корень неотрицательного числа.",
                ScriptNodeKind.Trigonometry => "Sin, Cos или Tan; угол задаётся в радианах.", ScriptNodeKind.Absolute => "Абсолютное значение: abs(−5) = 5.",
                ScriptNodeKind.Clamp => "Ограничить число минимумом и максимумом.", ScriptNodeKind.Round => "Ближайшее целое; половины округляются от нуля.",
                ScriptNodeKind.Floor => "Принудительно округлить вниз.", ScriptNodeKind.Ceil => "Принудительно округлить вверх.",
                ScriptNodeKind.Lerp => "Смешать A и B с коэффициентом от 0 до 1.", ScriptNodeKind.Remap => "Перевести число из исходного диапазона в новый.",
                ScriptNodeKind.WeightedRandom => "Выбрать числовое значение по таблице дробных относительных весов.",
                ScriptNodeKind.Constant => "π, τ, e, φ, π/2 или √2.", _ => "Именованный набор шагов вычислений: сохранить и вставить в другой блок формулы." };
            Add("calculations", kind, new ScriptNode { Kind = kind }.Title, description);
        }
        Add("triggers", ScriptNodeKind.TriggerKey, "При клике на клавишу", "Независимая ветка по нажатию выбранной клавиши.");
        Add("triggers", ScriptNodeKind.TriggerButton, "При клике на кнопку", "Ветка по ID игровой кнопки или элемента дополнительного экрана.");
        Add("triggers", ScriptNodeKind.TriggerHotspot, "При клике на зону на экране", "Область в логических координатах игровой сцены.");
        Add("triggers", ScriptNodeKind.TriggerTextClosed, "При закрытии текстового окна", "Реакция на скрытие текстового окна или очистку текста.");
        Add("triggers", ScriptNodeKind.TriggerCombination, "При комбинации нажатий", "Ветка по сочетанию клавиш, например Ctrl+Shift+E.");
        Add("narrative", ScriptNodeKind.RegisterCharacter, "Персонаж", "Зарегистрировать имя, цвет и типографику персонажа без отображения спрайта.");
        Add("narrative", ScriptNodeKind.UpdateCharacter, "Изменить персонажа", "Изменить имя и типографику уже зарегистрированного персонажа по его ID.");
        Add("narrative", ScriptNodeKind.SetSpeaker, "Установить спикера", "Назначить профиль следующим репликам с пустым говорящим в текущей ветке.");
        Add("narrative", ScriptNodeKind.ResetSpeakers, "Сбросить спикеров", "Сбросить говорящего по умолчанию в текущей ветке; пустые реплики снова становятся рассказчиком.");
        Add("narrative", ScriptNodeKind.TextPause, "Пауза в тексте", "Ждать между репликами; разрешить или запретить пропуск кликом.");
        Add("narrative", ScriptNodeKind.ClearText, "Очистить текст", "Очистить текстовое окно мгновенно или с анимацией стирания.");
        Add("narrative", ScriptNodeKind.AutoDialogue, "Авто-пропуск", "Реплика с автоматическим переходом через заданное время.");
        Add("narrative", ScriptNodeKind.ConsoleWrite, "Запись в консоль", "Вывести текст в консоль как команда print().");
        Add("narrative", ScriptNodeKind.RandomBranch, "Случайная ветка", "Выбрать один выход с заданными относительными вероятностями.");
        Add("narrative", ScriptNodeKind.EffectDialogue, "Реплика с эффектом", "Анимация каждой буквы: волна, дрожание, радуга и другие эффекты.");
        Add("logic", ScriptNodeKind.CompareVariables, "Условие", "Сравнить две переменные state без реплики или остановки игры.");
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
        if (!Enum.IsDefined(kind) || kind == ScriptNodeKind.Condition) throw new ArgumentOutOfRangeException(nameof(kind), "Этот блок недоступен для создания.");
        if (category != null && !Definitions.Any(entry => entry.Kind == kind && entry.Group == category))
            throw new ArgumentException("Блок недоступен в выбранной группе.", nameof(category));
        var node = new ScriptNode { Kind = kind };
        if (CalculationCompiler.IsCalculation(kind))
        {
            node.Calculation = new CalculationSettings();
            if (kind is ScriptNodeKind.Increment or ScriptNodeKind.Decrement) node.Calculation = node.Calculation with { A = node.Variable };
            if (kind == ScriptNodeKind.Clamp) node.Calculation = node.Calculation with { B = "0", C = "100" };
            if (kind == ScriptNodeKind.Remap) node.Calculation = node.Calculation with { B = "0", C = "1", D = "0", T = "100" };
        }
        if (node.IsTrigger) node.Trigger = new TriggerSettings { Kind = kind switch { ScriptNodeKind.TriggerButton => "button", ScriptNodeKind.TriggerHotspot => "hotspot", ScriptNodeKind.TriggerTextClosed => "text_closed", _ => "key" }, Keys = kind == ScriptNodeKind.TriggerCombination ? "Ctrl+E" : "E" };
        node.Category = category ?? Definitions.First(entry => entry.Kind == kind).Group;
        if (kind is ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue) node.Text = "Новая реплика";
        if (kind == ScriptNodeKind.TextInput) node.Input = new TextInputSettings { Prompt = "Введите ответ:" };
        if (kind == ScriptNodeKind.RegisterCharacter) node.Character = new CharacterProfile { Id = "character", Name = "Персонаж" };
        if (kind == ScriptNodeKind.UpdateCharacter) node.Character = new CharacterProfile { Id = "character", Name = "Персонаж" };
        if (kind == ScriptNodeKind.SetSpeaker) node.SpeakerId = "character";
        if (kind == ScriptNodeKind.EffectDialogue) node.TextEffect = new TextEffectSettings();
        if (kind == ScriptNodeKind.ClearText) node.Duration = .5;
        if (kind == ScriptNodeKind.AutoDialogue) node.Duration = 2;
        if (kind == ScriptNodeKind.ConsoleWrite) node.Text = "Сообщение";
        if (kind == ScriptNodeKind.RandomBranch) node.Options = [new() { Text = "Ветка 1", Weight = 60 }, new() { Text = "Ветка 2", Weight = 40 }];
        if (kind == ScriptNodeKind.Comment) node.Text = "Комментарий автора";
        if (kind == ScriptNodeKind.Title) node.Text = "Новый титр";
        if (kind == ScriptNodeKind.TimedChoice) node.ChoiceSettings = new ChoiceSettings();
        if (kind is ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice) { node.Text = "Выберите вариант"; node.Options = [new() { Text = "Да" }, new() { Text = "Нет" }]; }
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
        ? Definitions.First(entry => entry.Kind == (node.Kind == ScriptNodeKind.Condition ? ScriptNodeKind.Branching : node.Kind)).Color : Groups.Single(group => group.Id == node.Category).Color;
}
