using System.IO;
using System.Net;
using System.Text;

namespace KNE;

/// <summary>Offline HTML reference built from the current block catalog and explicit parameter descriptions.</summary>
public static class BlockHelpDocument
{
    /// <summary>Returns the complete self-contained Russian block manual.</summary>
    public static string BuildHtml()
    {
        static string H(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Справка по блокам · KNE</title>
            <style>:root{color-scheme:light dark}body{font:16px/1.65 system-ui;margin:0;background:light-dark(#f7f6fa,#191c24);color:light-dark(#202733,#e5eaf3)}main{max-width:1000px;margin:auto;padding:32px}nav{display:flex;flex-wrap:wrap;gap:8px}a{color:light-dark(#3155a0,#99c5ff)}nav a{padding:6px 12px;border:1px solid #8886;border-radius:8px}article{padding:20px 24px;margin:20px 0;border:1px solid #8886;border-radius:12px;background:light-dark(white,#242935);scroll-margin-top:16px}h1,h2,h3{line-height:1.3}code{background:#8882;padding:2px 5px;border-radius:4px}li{margin:7px 0}.group{border-left:6px solid;padding-left:14px;margin-top:44px}small{opacity:.75}</style></head><body><main>
            <h1>Блоки Karlene Novel Engine</h1><p>Справочник поставляется вместе с движком и работает без интернета. Здесь перечислены все доступные блоки и их параметры.</p>
            <p><b>Связи:</b> игра начинается с единственной функции <code>main()</code>, каждая схема содержит одно «Начало». Соедините все выходы. Порты перетаскиваются мышью; ПКМ перемещает обзор, Tab ищет существующий блок. Порядок хранения и положение карточек не задают порядок исполнения.</p>
            <p><b>Переменные:</b> значения проекта хранятся в <code>state</code> и входят в сохранения. Пути к ресурсам задаются от корня проекта. «Перейти» (goto) не возвращается, «Вызвать» (call) сохраняет место возврата. У конца вложенной ветки выбирайте «Завершить ветку / вернуться».</p>
            <p><b>Инлайн-текст:</b> <code>{player_name}</code> подставляет значение <code>state.player_name</code> в репликах, выборе, вводе, титрах и текстах показанного экрана. Неизвестная переменная — серый <code>{missing}</code>, пустая — серый <code>{null}</code>, некорректное значение — красный <code>{error}</code>. <code>{color=red}текст{/color}</code> также принимает <code>#ff0000</code> и <code>rgb(255,0,0)</code>. Вложенные цвета восстанавливают внешний цвет. Для буквальных скобок используйте <code>{{</code> и <code>}}</code>. Только в репликах: <code>{w}</code> ждёт клика, <code>{w=0.5}</code> ждёт полсекунды игрового времени. Паузы входят в сохранения.</p>
            <p><b>Тестирование:</b> «Предпросмотр фрейма» показывает выбранный момент; «Тестировать сцену» запускает отдельную сцену, не всю игру. Ошибки содержат файл, строку и ID блока.</p><nav>
            """);
        foreach (var group in BlockLibrary.Groups) html.Append("<a href=\"#group-").Append(group.Id).Append("\">").Append(H(group.Title)).Append("</a>");
        html.Append("</nav><p><b>Шейдерные схемы:</b> .kneshader содержит граф значений без «Начала» и Lua-функций. Выход значения подключается к входам других блоков, итог — единственный Fragment Output. Корневые файлы применяются к цели Texture; контейнеры переиспользуются внутри родителя.</p>");
        foreach (var group in BlockLibrary.Groups)
        {
            html.Append("<h2 class=\"group\" id=\"group-").Append(group.Id).Append("\" style=\"border-color:").Append(group.Color).Append("\">").Append(H(group.Title)).Append("</h2>");
            foreach (var entry in BlockLibrary.Definitions.Where(entry => entry.Group == group.Id))
            {
                html.Append("<article id=\"").Append(entry.Kind).Append("\"><h3>").Append(H(entry.Title)).Append("</h3><p>").Append(H(entry.Description)).Append("</p><ul>");
                foreach (var parameter in Parameters(entry.Kind)) html.Append("<li>").Append(H(parameter)).Append("</li>");
                html.Append("</ul><p><small>").Append(entry.Kind == ScriptNodeKind.ShaderContainer ? "В игровой схеме подключите вход и выход выполнения; внутри .kneshader используйте порты значений." : ShaderLibrary.IsShader(entry.Kind) ? "Соединяйте выходы значений с нужными входами. Шейдер запускается контейнером из сценария." : entry.Kind is ScriptNodeKind.Comment ? "Портов нет. Заметка не выполняется." : entry.Kind is ScriptNodeKind.End or ScriptNodeKind.GotoScene or ScriptNodeKind.SendFlow ? "Выхода «Далее» нет: этот блок завершает текущую ветку." : "Подключите выходы к следующему блоку. Изменения свойств применяются кнопкой «Применить свойства» и сохраняются вместе со схемой.").Append("</small></p><a href=\"#\">К началу ↑</a></article>");
            }
        }
        return html.Append("</main></body></html>").ToString();
    }

    /// <summary>Exports the latest manual atomically to a local HTML file.</summary>
    public static async Task<string> WriteAsync(string directory, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "blocks.html");
        string temp = Path.Combine(directory, ".blocks-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllTextAsync(temp, BuildHtml(), Encoding.UTF8, token); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return path;
    }

    /// <summary>Parameters for one currently supported block.</summary>
    public static IReadOnlyList<string> Parameters(ScriptNodeKind kind)
    {
        if (kind is ScriptNodeKind.RemoveLayerShaders or ScriptNodeKind.RemoveAllShaders)
            return ["Команда игровой схемы .knegraph с входом и выходом выполнения. Удаляет активные эффекты; исходные файлы не удаляются.",
                kind == ScriptNodeKind.RemoveLayerShaders ? "Слой: вся сцена, фон, спрайт, игровой UI или экран по названию. Удаляются все шейдеры выбранной цели; название экрана сравнивается без учёта регистра." : "Удаляет все активные шейдеры.",
                "Галочка скрывает также остальные и новые эффекты до следующего контейнера в этом потоке. Call/goto сохраняют поток; параллельная ветка не снимает чужую блокировку. При нескольких блокировках каждая снимается своим потоком. Если поток завершится без контейнера, блокировка сохраняется до перезапуска."];
        if (ShaderLibrary.IsShader(kind))
        {
            var definition = ShaderLibrary.Get(kind);
            var parameters = new List<string> { "Только .kneshader. Соединения передают значения, один выход можно подключить к нескольким входам. Неподключённый вход использует значение по умолчанию.", "Входы: " + string.Join(", ", definition.Inputs) + ". Выходы: " + string.Join(", ", definition.Outputs) + "." };
            parameters.AddRange(ShaderLibrary.Parameters(kind).Select(parameter => parameter.Label + ": по умолчанию " + parameter.Default.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; диапазон " + parameter.Min + "…" + parameter.Max + "."));
            if (kind == ScriptNodeKind.ShaderTexture) parameters.Add("Цель: вся сцена, фон, спрайт, игровой UI или экран с указанным названием. Один файл — одна цель. Файлы, подключённые контейнером, отдельно не применяются.");
            if (kind == ScriptNodeKind.ShaderContainer) parameters.Add("Путь .kneshader от проекта; по умолчанию game/shaders. Двойной клик открывает / создаёт файл. В игровом графе эффект включается при достижении блока; снятая галочка отключает его. Внутри .kneshader контейнер — чистая функция от UV и текстуры родителя; цель вложенного файла не меняет цель родителя. Циклы запрещены. Блоки «Плагины» доступны в обоих форматах; в шейдере выполняются при вызове как подготовка, include — один раз, Скрипт — каждый вызов.");
            if (kind is ScriptNodeKind.ShaderFloat or ScriptNodeKind.ShaderVector or ScriptNodeKind.ShaderColor) parameters.Add("Имя внешнего параметра; слайдеры и палитра доступны в отдельном предпросмотре шейдера. Значения предпросмотра не меняют файл.");
            if (kind == ScriptNodeKind.ShaderOutput) parameters.Add("Ровно один итоговый выход. Компиляция GLSL / HLSL через SPIR-V и выполнение OpenGL / Vulkan. «Не отображать шейдеры» в параметрах сцены временно убирает эффекты.");
            return parameters;
        }
        if (CalculationCompiler.IsCalculation(kind))
        {
            var result = new List<string> { "Результат: имя переменной state (латиница, цифры, _, не с цифры). Числовые поля принимают выражения: score, state.score, 2.5, (a+b)*2. Пустая или нечисловая переменная — ошибка. Числа пишутся с точкой." };
            result.Add(kind switch {
                ScriptNodeKind.Arithmetic => "A, B и операция +, −, *, /. Деление на ноль запрещено.",
                ScriptNodeKind.Modulo => "A — делимое, B — делитель. Остаток следует Lua: −5 % 3 = 1. Делитель не равен нулю.",
                ScriptNodeKind.Pow => "A — основание, B — степень. Нечисловой или бесконечный результат вызывает ошибку.",
                ScriptNodeKind.Trigonometry => "A — угол в радианах, функция sin / cos / tan. Для градусов: angle * pi / 180.",
                ScriptNodeKind.Clamp => "A — число, B — минимум, C — максимум (B ≤ C). Например clamp(150,0,100) = 100.",
                ScriptNodeKind.Lerp => "A, B — границы; t — коэффициент от 0 до 1: A×(1−t)+B×t. Для движения во времени меняйте t в сценарии/цикле с паузой: сам блок вычисляет один шаг и не ставит таймер.",
                ScriptNodeKind.Remap => "A — число; B/C — исходные границы; D/t — новые границы. Равные исходные границы запрещены. За пределами исходного диапазона выполняется экстраполяция; используйте Ограничитель при необходимости.",
                ScriptNodeKind.WeightedRandom => "Таблица: значение и вес на строку. Веса относительные, дробные, неотрицательные; нулевой вес никогда не выбирается. Например 0.6 и 0.4 дают 60%/40%. От 1 до 100 строк, хотя бы один положительный вес. Результат может быть дробным. Случайный выбор воспроизводится при загрузке сохранений.",
                ScriptNodeKind.Constant => "Значение: pi (π), tau (2π), e, phi ((1+√5)/2), half_pi (π/2), sqrt2 (√2).",
                ScriptNodeKind.Formula => "Название и 1–64 шага «имя = выражение». Каждый шаг видит переменные state и результаты предыдущих шагов. Имена шагов уникальны; последний шаг записывается в результат. Можно скопировать/вставить все шаги и выбрать формулу из схем проекта. Изменения вставленной копии не меняют оригинал.",
                ScriptNodeKind.Sqrt => "A — неотрицательное число. Отрицательное значение вызывает ошибку.",
                ScriptNodeKind.Increment => "A — исходное число/переменная. Записывает A+1 в результат; по умолчанию изменяет score.",
                ScriptNodeKind.Decrement => "A — исходное число/переменная. Записывает A−1 в результат; по умолчанию изменяет score.",
                ScriptNodeKind.Round => "A — число. Половины округляются от нуля: 2.5 → 3, −2.5 → −3.",
                ScriptNodeKind.Floor => "A — число. Округляет к −∞: −1.2 → −2.", ScriptNodeKind.Ceil => "A — число. Округляет к +∞: −1.2 → −1.",
                _ => "A — число. Результат — его абсолютное значение: −5 → 5." });
            result.Add("Формулы поддерживают + − * / % ^, скобки и функции abs, sqrt, sin, cos, tan, floor, ceil, round, pow, mod, min, max, clamp, lerp, remap; константы pi, tau, e, phi, half_pi, sqrt2. Произвольный Lua-код не выполняется.");
            return result;
        }
        if (kind is ScriptNodeKind.TriggerKey or ScriptNodeKind.TriggerCombination or ScriptNodeKind.TriggerButton or ScriptNodeKind.TriggerHotspot or ScriptNodeKind.TriggerTextClosed)
            return [kind switch {
                ScriptNodeKind.TriggerKey => "Клавиша: ввод имени или кнопка захвата. Автоповтор удерживаемой клавиши игнорируется.",
                ScriptNodeKind.TriggerCombination => "Сочетание: нажмите и отпустите все клавиши; например Ctrl+Shift+E. Левая/правая сторона модификатора и порядок записи не важны.",
                ScriptNodeKind.TriggerButton => "ID кнопки: next, save, load, auto_save, pause, choice:N, input_submit, input_cancel или screen:Название/ID. ID можно выбрать из списка. Событие перехватывает стандартное действие.",
                ScriptNodeKind.TriggerHotspot => "X/Y/ширина/высота в логической области 1280×720. Правая и нижняя границы исключены; область масштабируется вместе с игрой.",
                _ => "Срабатывает при скрытии текстового окна и после команды очистки (после окончания анимации)." },
                "Вход подключён: ветка останавливается на триггере до события, затем выполняет выход. Без входящей связи: независимый обработчик регистрируется при входе в функцию, снимается при выходе. Выход ведёт на блоки обработчика. Занятый обработчик повторно не запускается.",
                "Сработать только один раз: отключает повторения независимого обработчика. Подключённое ожидание потребляет одно событие за посещение. Клавиатурные триггеры не перехватывают набор текста в поле ввода или консоли." ];
        return kind switch {
            ScriptNodeKind.Start => ["Имя функции над схемой: main для единственного входа игры, уникальное имя для остальных функций. Выход — первый выполняемый блок."],
            ScriptNodeKind.End => ["После конца сцены: вернуться/завершить ветку, главное меню, специальный экран или закрыть игру. Для специального экрана — его название из «Оформление». Подсистема возвращает родителю независимо от выбранного режима конца."],
            ScriptNodeKind.Dialogue or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue => DialogueParameters(kind),
            ScriptNodeKind.RegisterCharacter or ScriptNodeKind.UpdateCharacter => ["ID — латиница без пробелов; отображаемое имя, цвет имени, позиция текста, семейство шрифта и размер. «Персонаж» регистрирует профиль без спрайта. «Изменить персонажа» требует уже зарегистрированный ID и меняет его параметры."],
            ScriptNodeKind.SetSpeaker => ["Профиль персонажа: ID зарегистрированного персонажа. Следующие реплики без явно выбранного профиля наследуют его в текущей ветке. Рассказчик/мысль может обойти наследование."],
            ScriptNodeKind.ResetSpeakers => ["Параметров нет. Снимает профиль говорящего по умолчанию с текущей ветки."],
            ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice => ["Текст вопроса и 1–20 вариантов. Каждый вариант имеет свой выход. Условия блокировки сравнивают переменную с литералом/флагом; можно задать эффект блокировки (затемнение, зачёркивание, глитч, скрытие, scramble) и подсказку при наведении с положением рядом/поверх.", "Для выбора с таймером: N секунд, режим таймера (полоса/число), вариант по умолчанию или случайный доступный вариант. Заблокированные ответы нельзя выбрать, в том числе автоматически."],
            ScriptNodeKind.TextInput => ["Переменная ответа: допустимое Lua-имя. Подсказка перед вводом, placeholder, максимальная длина 1–256 (по умолчанию 32), автофокус, маска ввода. Проверка: без проверки, точное совпадение с игнорированием регистра по флагу или регулярное выражение. Текст ошибки задаёт реакцию на неправильный ответ.", "Можно включить выходы Успех / Ошибка / Отмена. Без выхода отмены кнопка отмены скрыта. Проверка имени переменной не ограничивает сам ответ игрока латиницей."],
            ScriptNodeKind.Branching => ["Переменная state, оператор сравнения, тип и значение литерала. Выходы Да/Нет выбираются по результату, реплика не показывается."],
            ScriptNodeKind.CompareVariables => ["Две переменные state и оператор сравнения. Выход Да при истинном результате, Нет иначе. Не выводит реплик."],
            ScriptNodeKind.SetVariable => ["Переменная state, тип (число/строка/логический), значение. Для числа точка — десятичный разделитель. Запись входит в сохранения."],
            ScriptNodeKind.SetFlag or ScriptNodeKind.CheckFlag => ["Переменная-флаг и true/false. Установка записывает значение; проверка сравнивает равенство/неравенство и выбирает Да/Нет."],
            ScriptNodeKind.Switch => ["Переменная, тип и список значений Case. Значения уникальны. Выходы Case, По умолчанию и Далее. Внутреннюю ветку завершите возвратом либо соедините с общим Далее."],
            ScriptNodeKind.While => ["Переменная, сравнение, тип и значение. Выход Тело повторяется, пока сравнение истинно; Далее — выход из цикла. Меняйте проверяемую переменную в теле. Предел 10000 итераций защищает от зависания."],
            ScriptNodeKind.ForEach => ["Массив state, переменная текущего элемента и переменная индекса: три различных имени. Массив имеет последовательные индексы с 1, не более 10000 элементов. Выходы Тело и Далее."],
            ScriptNodeKind.TryCatch => ["Переменная для текста ошибки. Выходы Тело, Ошибка, Далее. После выполнения тела или обработчика управление возвращается на Далее."],
            ScriptNodeKind.Random => ["Переменная результата, минимум A и максимум B включительно (A ≤ B). Целые границы дают целое число; дробные — дробное."],
            ScriptNodeKind.RandomBranch => ["2–20 ветвей с названиями и относительными неотрицательными весами; хотя бы один положительный. Каждая ветвь имеет собственный выход. Общего Далее нет."],
            ScriptNodeKind.Comment => ["Текст заметки автора. Можно расположить отдельно, не влияет на запуск."],
            ScriptNodeKind.ConsoleWrite => ["Текст для игровой консоли. Работает как print(), не ставит сцену на паузу."],
            ScriptNodeKind.Title => ["Текст поверх игрового поля вне диалогового окна. Пустой текст скрывает титр; добавьте паузу/реплику, чтобы его успели прочитать."],
            ScriptNodeKind.TextPause => ["Длительность в секундах и возможность пропуска кликом. Временно приостанавливает переход между репликами."],
            ScriptNodeKind.ClearText => ["Мгновенно или анимация стирания, длительность в секундах. Очищает весь текст и уведомляет триггеры закрытия текстового окна."],
            ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene => ["Название функции, например chapter_two, без пути и скобок. goto заменяет текущую функцию; call возвращается на следующий блок после завершения вызова. Глубина вызовов не более 64."],
            ScriptNodeKind.Anchor => ["Название точки возврата в текущей сцене. Выберите этот якорь в блоке «Отправить поток». Якорь принадлежит своему потоку: основная последовательность, отдельная параллельная ветка или обработчик триггера."],
            ScriptNodeKind.SendFlow => ["Выберите якорь текущей сцены. Выполнение продолжается с якоря в том же потоке, включая переход из тела While, For Each или Try/Catch. Выхода «Далее» нет. Переход в другую сцену или параллельную ветку запрещён."],
            ScriptNodeKind.ConditionalGoto => ["Переменная state, оператор сравнения, тип и значение литерала; название функции назначения без пути и скобок. При истинном условии заменяет текущую сцену указанной функцией; при ложном продолжает через «Далее»."],
            ScriptNodeKind.Subsystem => ["Путь .knegraph от корня проекта. На карточке — имя файла. Двойной клик или кнопка открывает файл во вкладке; отсутствующий создаётся. «Ждать полного завершения» приостанавливает родителя. В фоне визуал/звук работают, реплики идут в консоль; выбор и ввод требуют ожидания."],
            ScriptNodeKind.Background or ScriptNodeKind.Character => ["Путь к изображению в проекте. Для спрайта — позиция left/center/right. Пустой путь фона возвращает оформление проекта."],
            ScriptNodeKind.HideBackground or ScriptNodeKind.HideCharacter or ScriptNodeKind.ClearBackgrounds or ScriptNodeKind.ClearSprites => ["Параметров нет. Скрыть фон возвращает стандартное оформление; удалить все фоны убирает также стандартный фон. Удалить все спрайты сбрасывает их перемещения и анимации."],
            ScriptNodeKind.MoveBackground or ScriptNodeKind.MoveCharacter => ["Смещения X/Y в логических пикселях и длительность 0–3600 секунд. Перемещает фон/спрайт, не блокируя интерфейс."],
            ScriptNodeKind.Animation => ["Объект: фон/sprite. Эффект pulse/rotate/fade_in/fade_out/none, длительность и сила. none отключает эффект. Просмотр анимации выбранного блока включается отдельно."],
            ScriptNodeKind.Shake => ["Сила 0–500 и длительность 0–60 секунд. Трясёт игровую сцену."],
            ScriptNodeKind.Flash => ["Цвет #RRGGBB / #AARRGGBB и длительность 0–60 секунд."],
            ScriptNodeKind.Camera => ["Масштаб 0.05–20 (1 = 100%), смещения X/Y и длительность перехода."],
            ScriptNodeKind.ShowScreen => ["Уникальное название экрана из «Оформление», прозрачность 0–1, слой main/overlay. Overlay всегда выше фона и персонажей. Пустое название убирает дополнительный экран слоя."],
            ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.Voice => ["Путь к аудиофайлу проекта. Музыка: канал и повтор. Звук: канал sfx по умолчанию, один раз. Озвучка использует voice."],
            ScriptNodeKind.AudioChannel => ["Канал music/sfx/voice/ambient, громкость 0–1 и повтор. Определяет параметры проигрывания канала."],
            ScriptNodeKind.StopMusic or ScriptNodeKind.StopSound => ["Остановить музыку использует music. Остановить звук — выбранный канал music/sfx/voice/ambient."],
            ScriptNodeKind.MasterVolume => ["Общий множитель громкости от 0 (тишина) до 1 (полная)."],
            ScriptNodeKind.Parallel or ScriptNodeKind.Async => ["Список ветвей и выход Далее. Параллель ждёт завершения всех ветвей. Асинхронность продолжает сразу и сохраняет ID группы в выбранную переменную state. В фоне реплики идут в консоль; выбор/ввод недоступны. Одновременно не более 128 ветвей."],
            ScriptNodeKind.Join => ["Переменная state с ID фоновой группы. Ожидает её завершения; ошибка ветви передаётся ожидающему."],
            ScriptNodeKind.Timer or ScriptNodeKind.Pause => ["Длительность 0–3600 секунд. Приостанавливает текущую ветку, не блокируя окно и параллельные задачи."],
            ScriptNodeKind.WaitCondition => ["Переменная, оператор, тип и значение для сравнения; таймаут в секундах. Нулевой таймаут — без ограничения. По истечении ненулевого таймаута возникает ошибка."],
            ScriptNodeKind.ConfigureDialogue or ScriptNodeKind.ConfigureButton => ["Для кнопки выберите цель: next/save/load/auto_save/pause/choice. Параметры: X/Y/ширина/высота, позиция на экране, шрифт/семейство/размер/вес, цвет текста и фона, прозрачность, скругление, текстура, отступы и выравнивание. Сброс возвращает вид из engine. Меняет игровой UI, не тему редактора."],
            ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script => ["Путь внешнего файла в проекте. Подключить .lua/.cs загружает плагин; Скрипт создаёт/открывает .lua/.krl/.cs в редакторе из настроек и исполняет при входе в блок. Плагины выполняют внешний код; безопасный режим движка отключает сторонние плагины."],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Блок не доступен в текущем каталоге.")
        };
    }
    private static string[] DialogueParameters(ScriptNodeKind kind) => ["Говорящий: вручную или профиль персонажа по кнопке. Пустое поле наследует установленного спикера; «Рассказчик / мысль» явно снимает его. Текст — содержимое диалога.",
        kind == ScriptNodeKind.AutoDialogue ? "Длительность до автоматического перехода и разрешение пропуска кликом." : kind == ScriptNodeKind.EffectDialogue ? "Эффект всего текста: Shake, Wave, Sway, Bounce, Float, Explode, Pulse, Spin, Squash/Stretch, Glitch, Blur, Fade/Dissolve, Scramble, Rainbow, Color Cycle, Растекание. Скорость, цикличность, сила и цвета настраиваются в свойствах." : "Переход к следующей реплике — кликом/пробелом. У каждого фрейма есть ID внутри сцены и игры."];
}
