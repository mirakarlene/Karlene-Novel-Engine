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
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Руководство Karlene Novel Engine</title>
            <style>
              :root{color-scheme:dark;--page:#11151e;--panel:#191f2c;--line:#31394b;--ink:#e8ecf5;--muted:#aeb9cb;--link:#a9c6ff;}
              *{box-sizing:border-box}html{scroll-padding-top:2rem}body{margin:0;background:var(--page);color:var(--ink);font:16px/1.65 system-ui,sans-serif}
              a{color:var(--link);text-decoration:none}a:hover{text-decoration:underline}a:focus-visible,summary:focus-visible{outline:2px solid var(--link);outline-offset:4px}
              header{border-bottom:1px solid var(--line);padding:15px clamp(1rem,5vw,4rem);font-weight:700;background:var(--panel)}header strong{color:#c8d9ff}
              .layout{max-width:1320px;margin:auto;display:grid;grid-template-columns:228px minmax(0,1fr);gap:48px;padding:32px clamp(1rem,4vw,3rem)}
              aside{align-self:start;position:sticky;top:24px;max-height:calc(100vh - 48px);overflow:auto;padding-right:18px;border-right:1px solid var(--line)}
              aside nav{display:grid;gap:8px}aside details{margin-top:14px}aside details a{display:block;margin:4px 0 4px 12px;color:var(--muted);font-size:.9em}summary{cursor:pointer;font-weight:600}
              main{min-width:0;max-width:960px}h1,h2,h3,h4{line-height:1.2}h1{font-size:clamp(2rem,4vw,3.25rem);margin:0 0 .7rem}
              h2{font-size:1.65rem;margin:2.4rem 0 .8rem}h3{font-size:1.22rem}p,ul{margin:.7rem 0}small,.lead{color:var(--muted)}
              nav.index{display:flex;flex-wrap:wrap;gap:8px;margin:1rem 0}nav.index a{background:var(--panel);padding:4px 10px;border:1px solid var(--line);border-radius:6px}
              .formats{display:flex;gap:12px;flex-wrap:wrap}.formats span{display:inline-flex;align-items:center;gap:8px;border:1px solid var(--line);padding:6px 12px;border-radius:8px}
              .group{border-bottom:1px solid var(--line);border-left:4px solid;padding:0 0 .6rem .75rem;margin-top:3rem}
              article{border-bottom:1px solid var(--line);padding:1.1rem 0}article h3,article h4{margin:0 0 .45rem}article ul{padding-left:1.35rem;color:var(--muted)}article a{font-size:.86rem}
              code,pre{font-family:ui-monospace,Consolas,monospace;background:#222b3a;border-radius:5px}code{padding:2px 5px;color:#d2e4ff}
              pre{overflow:auto;padding:14px 16px;border:1px solid var(--line)}pre code{padding:0}li{margin:.28rem 0}.table{overflow:auto}
              table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:9px 11px;border-bottom:1px solid var(--line)}th{background:var(--panel)}td:first-child{width:32%}
              @media(max-width:800px){.layout{display:block}aside{position:static;max-height:none;border-right:0;border-bottom:1px solid var(--line);margin-bottom:22px;padding:0 0 16px}aside details{display:inline-block;margin-right:14px}}
            </style></head><body id="top">
            <header><strong>KNE</strong> &nbsp; Справочник редактора и сценариев</header>
            <div class="layout"><aside><nav aria-label="Содержание">
              <a href="#quick-start">Первая новелла</a><a href="#workspace">Работа с проектом</a>
              <a href="#project-events">Запуск и события</a><a href="#testing">Тестирование и сборка</a>
              <a href="#saves">Сохранения и резервные копии</a><a href="#plugins">Подключение плагинов</a>
              <a href="#kne-reference">KNE / knegraph</a><a href="#lua-reference">Lua / Visual Lua</a>
            """);
        AddContents("Блоки KNE", BlockLibrary.Groups);
        AddContents("Блоки Lua", VisualLuaCatalog.Groups);
        html.Append("""
            </nav></aside><main>
            <h1>Руководство Karlene Novel Engine</h1>
            <p class="lead">Как создать визуальную новеллу, проверить её в редакторе и подготовить сборку для игрока.</p>
            <section id="quick-start"><h2>Создайте первую новеллу</h2>
              <ol><li>На главном экране создайте проект и выберите, как будете редактировать сценарий. KRL хранит сценарий в виде кода, а блок-схема показывает связи между командами.</li>
              <li>Откройте начальную сцену в папке <code>game/scenes</code>. В новом проекте она уже содержит короткий пример с репликами.</li>
              <li>Замените текст первой реплики. В схеме выберите блок «Реплика», измените текст и нажмите «Применить свойства».</li>
              <li>Нажмите «Сохранить», затем «Запустить проект». Начните игру и проверьте, что появилась ваша реплика.</li></ol>
              <p>В проекте должна быть одна функция <code>main()</code>. Имена остальных сцен можно выбирать свободно, пока они уникальны. Для вспомогательного кода используйте <code>.lua</code> или <code>.vlua</code>.</p>
            </section>
            <section id="workspace"><h2>Работайте с файлами и сценами</h2>
              <p>Слева видна структура проекта. В центральном окне можно открыть папку или файл. В настройках KNE выберите плитки или таблицу. Кнопка «Сохранить» записывает текущий файл, «Сохранить всё» записывает открытые файлы.</p>
              <p>Чтобы создать файл, нажмите «Создать файл», выберите формат, введите имя и укажите папку. Через «Импорт файла» скопируйте изображения, музыку, видео и другие материалы в проект. Для <code>.rpy</code> и <code>.docx</code> редактор предложит проверить преобразованный сценарий перед сохранением.</p>
              <p>«Навигатор сцен» показывает функции в <code>.krl</code> и <code>.knegraph</code>, их файлы и переходы. Дважды щёлкните по сцене, чтобы открыть её. На большой схеме перетащите миникарту или нажмите на нужную область, чтобы переместить обзор.</p>
              <p>Параметры миникарты находятся на странице «Проекты и Рендер» настроек KNE. Для блок-схем можно выбрать размер, угол и цвета категорий. Цветная миникарта использует выбранную палитру доступности.</p>
              <p>В «Оформлении» настраиваются игровые экраны. Для реплик и перевода используйте «Диалоги / переводы». В «Параметрах сцены» можно задать её музыку, переходы, начальные переменные и правила сохранения.</p>
            </section>
            <section id="project-events"><h2>Запуск и события</h2>
              <p>Четвёртая страница «Параметров проекта» содержит метаданные, начальную сцену, скрипт инициализации и события. Все пути отсчитываются от корня проекта. Чтобы выключить необязательное событие, очистите его поле.</p>
              <div class="table"><table><thead><tr><th scope="col">Параметр</th><th scope="col">Когда срабатывает</th></tr></thead><tbody>
              <tr><td>Сплэш-скрин</td><td>Изображение показывается до открытия окна игры. Время задаётся в секундах.</td></tr>
              <tr><td>Сцена до главного меню</td><td>Проигрывается при запуске, перед первым показом главного меню.</td></tr>
              <tr><td>Скрипт для главного меню</td><td>Выполняется при открытии главного меню вне активного прохождения.</td></tr>
              <tr><td>Скрипт при AFK</td><td>Выполняется после заданного времени без ввода. После нового действия игрока отсчёт начинается снова.</td></tr>
              <tr><td>Скрипт при сохранении</td><td>После успешной записи слота. Имя слота доступно в <code>project_event.slot</code>.</td></tr>
              <tr><td>Скрипт при выходе</td><td>При запросе выхода, перед подтверждением.</td></tr>
              <tr><td>Скрипт при ошибке</td><td>Возвращает строку для окна ошибки. Исходное сообщение доступно в <code>project_event.message</code>.</td></tr>
              </tbody></table></div>
              <h3>Запишите имя слота в консоль</h3>
              <p>Сохраните этот код в файле <code>.lua</code> и выберите его в поле «Скрипт при сохранении».</p>
              <pre><code>local event = project_event
            if event.kind == "save" then
                print("Сохранён слот " .. event.slot)
            end</code></pre>
              <p>Скрипт ошибки может вернуть текст для игрока, например <code>return "Возникла ошибка. Откройте игру заново."</code></p>
            </section>
            <section id="testing"><h2>Проверьте игру и подготовьте сборку</h2>
              <p>Используйте «Предпросмотр фрейма», чтобы посмотреть выбранный момент, и «Тестировать сцену», чтобы пройти отдельную сцену. Для проверки всего проекта нажмите «Запустить проект». Консоль обычно открывается клавишей F3. Сочетание меняется в «Параметрах проекта».</p>
              <p>На странице сборки выберите режим тестирования. VM быстро запускает игру через рабочий процесс Lua. Standalone Test создаёт отдельный EXE. Remote Debug запускает отдельный EXE и подключает отладчик редактора.</p>
              <p>Укажите папку и формат релиза, затем нажмите «Собрать релиз». Файлы EXE, ZIP и MSIX получают исходное название проекта. Если оно не записано в старом проекте, KNE берёт имя его папки. В параметрах проекта можно использовать то же имя для папки сборки.</p>
              <p>Framework-Dependent требует .NET 10 Desktop Runtime у игрока. Self-Contained включает среду выполнения. Single-File собирает игру в один EXE. Для MSIX понадобится Windows SDK, а Portable ZIP хранит сохранения рядом с игрой.</p>
              <p>Если запуск завершился ошибкой, начните с имени файла и номера строки в отчёте. Для визуального сценария отчёт может также содержать идентификатор блока. Команда <code>diagnostics</code> повторяет проверку в проектах со штатным скриптом инициализации.</p>
            </section>
            <section id="saves"><h2>Различайте сохранения игры и копии проекта</h2>
              <ul><li>Слот игры хранит прохождение. Путь к слотам, автосохранение и глубина отката задаются в «Параметрах проекта».</li>
              <li><code>persistent</code> хранит данные, которые должны быть доступны между запусками и в разных слотах. Его расположение настраивается отдельно.</li>
              <li>Резервная копия охватывает весь проект. В настройках KNE выберите папку, расписание и число копий.</li>
              <li>Автосохранение редактора создаёт версии открытых файлов. Откройте «Просмотреть историю», чтобы вернуть выбранную версию.</li></ul>
            </section>
            <section id="plugins"><h2>Подключите плагин</h2>
              <p>На странице «Плагины» в настройках KNE можно посмотреть сведения о плагине и включить его. Игровые сценарии могут подключать модули Lua и C# из проекта. Для C# используется интерфейс <code>IKnePlugin</code>.</p>
              <p>Перед выпуском проверьте блок «Безопасность» в «Параметрах проекта». В нём задаются разрешения на плагины, сетевые запросы и доступ к файлам.</p>
            </section>
            <section id="kne-reference"><h2>KNE / knegraph</h2>
            """);
        html.Append("""<div class="formats"><span>""").Append(ProjectFileFormats.HtmlIcon(ProjectFileFormats.Krl)).Append(" KRL · .krl</span><span>").Append(ProjectFileFormats.HtmlIcon(ProjectFileFormats.Graph)).Append(" KNE · .knegraph</span></div>");
        html.Append("""
            <h3>Соедините команды</h3>
            <p>В игровой схеме есть одно «Начало». Протяните связь от выходного порта блока к входу следующего. Игра выполняет команды по связям. Перетаскивание правой кнопкой мыши перемещает обзор, а Tab открывает поиск по блокам схемы.</p>
            <p>Значения <code>state</code> входят в сохранения. Блок «Перейти на сцену» передаёт управление другой функции. Блок «Вызвать сцену» возвращает управление на следующий блок после завершения вызова.</p>
            <h3>Подставьте переменную в реплику</h3>
            <pre><code>state.player_name = "Мира"
            d(nil, "Привет, {b}{player_name}{/b}!{w=0.5} Начнём?")</code></pre>
            <p>Фигурные скобки подставляют значение переменной. Серый <code>{missing}</code> означает, что имя неизвестно, а <code>{null}</code> означает пустое значение. Красный <code>{error}</code> указывает на ошибку вычисления. Чтобы напечатать сами скобки, удвойте их.</p>
            <p>Используйте парные теги <code>{b}</code>, <code>{i}</code>, <code>{size=24}</code> и <code>{color=red}</code>. Цвет также можно записать как <code>#ff0000</code> или <code>rgb(255,0,0)</code>. Тег <code>{font=имя}</code> работает после подключения шрифта. В реплике <code>{w}</code> ждёт действия игрока, а <code>{w=0.5}</code> делает паузу на полсекунды.</p>
            <h3>Создайте эффект в шейдерной схеме</h3>
            <ol><li>Нажмите «Создать файл» и выберите «Шейдерная схема» (<code>.kneshader</code>).</li>
            <li>В шейдерной схеме соедините блоки, передающие значения, и подключите результат к <code>Fragment Output</code>.</li>
            <li>В игровой схеме укажите путь к файлу в блоке «Контейнер GLSL/HLSL».</li></ol>
            <h3>Справочник блоков</h3>
            <p>Выберите категорию, чтобы посмотреть назначение и параметры каждого блока. После изменения свойств нажмите «Применить свойства», затем сохраните схему.</p>
            <nav class="index" aria-label="Категории блоков KNE">
            """);
        foreach (var group in BlockLibrary.Groups) html.Append("<a href=\"#group-").Append(group.Id).Append("\">").Append(H(group.Title)).Append("</a>");
        html.Append("</nav>");
        foreach (var group in BlockLibrary.Groups)
        {
            html.Append("<h3 class=\"group\" id=\"group-").Append(group.Id).Append("\" style=\"border-color:").Append(group.Color).Append("\">").Append(H(group.Title)).Append("</h3>");
            foreach (var entry in BlockLibrary.Definitions.Where(entry => entry.Group == group.Id))
            {
                html.Append("<article id=\"").Append(entry.Kind).Append("\"><h4>").Append(H(entry.Title)).Append("</h4><p>").Append(H(entry.Description)).Append("</p><ul>");
                foreach (var parameter in Parameters(entry.Kind)) html.Append("<li>").Append(H(parameter)).Append("</li>");
                html.Append("</ul>");
                if (entry.Kind == ScriptNodeKind.Comment) html.Append("<p><small>Портов нет. Заметка не выполняется.</small></p>");
                html.Append("</article>");
            }
        }
        html.Append("""</section><section id="lua-reference"><h2>Lua / Visual Lua (VLua)</h2><div class="formats"><span>""")
            .Append(ProjectFileFormats.HtmlIcon(ProjectFileFormats.Lua)).Append(" Lua · .lua</span><span>")
            .Append(ProjectFileFormats.HtmlIcon(ProjectFileFormats.VisualLua)).Append(" Visual Lua · .vlua</span></div>")
            .Append("<p>Модули используют Lua 5.4. Переключатель «Код / Блок-схема» преобразует файл между .lua и .vlua. При обратном преобразовании редактор восстанавливает положения блоков. Фрагменты, которые нельзя разложить на блоки, остаются в блоке «Исходный код».</p>")
            .Append("""
                <p>Нажмите «Дебаг», чтобы запустить выбранный модуль во временной копии проекта. В отчёте видны сообщения Lua и ошибки выполнения.</p>
                <h3>Вызов модуля из KNE</h3>
                <p>В блоке «Вызвать VLua» укажите путь к модулю от корня проекта. Чтобы открыть модуль в редакторе, дважды щёлкните по блоку. В KRL тот же вызов выглядит так.</p>
                <pre><code>call_vlua("game/scripts/inventory.vlua")</code></pre>
                <nav class="index" aria-label="Категории Visual Lua">
                """);
        foreach (var group in VisualLuaCatalog.Groups)
            html.Append("<a href=\"#group-").Append(group.Id).Append("\">").Append(H(group.Title)).Append("</a>");
        html.Append("</nav>");
        foreach (var group in VisualLuaCatalog.Groups)
        {
            html.Append("<h3 class=\"group\" id=\"group-").Append(group.Id).Append("\" style=\"border-color:").Append(group.Color).Append("\">").Append(H(group.Title)).Append("</h3>");
            foreach (var entry in VisualLuaCatalog.Definitions.Where(entry => entry.Group == group.Id))
                html.Append("<article><h4>").Append(H(entry.Title)).Append("</h4><pre><code>").Append(H(entry.Description)).Append("</code></pre></article>");
        }
        return html.Append("</section></main></div></body></html>").ToString();

        void AddContents(string title, IEnumerable<BlockGroup> groups)
        {
            html.Append("<details><summary>").Append(H(title)).Append("</summary>");
            foreach (var group in groups)
                html.Append("<a href=\"#group-").Append(group.Id).Append("\">").Append(H(group.Title)).Append("</a>");
            html.Append("</details>");
        }
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
        if (SystemBlocks.All.TryGetValue(kind, out var system))
        {
            var details = new List<string> { kind == ScriptNodeKind.ShowVideo
                ? "Lua / KRL: show_video(path [, skip]) или show_video(path, x, y, width, height [, skip])."
                : "Lua / KRL: " + system.Function + "(" + string.Join(", ", system.Parameters) + ")." };
            if (system.Result == "state") details.Add("Результат записывается в переменную state.");
            if (system.Result == "list") details.Add("Имена записываются в отдельный именованный список.");
            if (SystemBlocks.UsesFileSystem(system.Function)) details.Add("Требует разрешения игрока. Пути ограничиваются политикой проекта.");
            if (kind == ScriptNodeKind.SaveNamed) details.Add("Загрузка слота продолжает сценарий после этого блока. Запрет сохранений в сцене действует и здесь.");
            if (kind == ScriptNodeKind.PersistentClear) details.Add("Полный сброс persistent выполняется после подтверждения игроком.");
            if (kind is ScriptNodeKind.OpenBrowser or ScriptNodeKind.ClipboardRead or ScriptNodeKind.ClipboardWrite or ScriptNodeKind.Screenshot)
                details.Add("Игроку предлагается отдельно разрешить внешние функции. Ссылки ограничены HTTP и HTTPS, снимки сохраняются в PNG внутри проекта.");
            if (kind == ScriptNodeKind.FreezeScan) details.Add("Разморозка: left (ЛКМ), right (ПКМ), key (клавиша) или timer (от 0,1 до 3600 секунд).");
            if (kind == ScriptNodeKind.Watermark) details.Add("Положение: один из углов, центров краёв или центр окна. Непрозрачность от 0 до 100.");
            if (kind == ScriptNodeKind.GlobalTimer) details.Add("Действия show, hide и reset. Форматы HH:mm:ss и mm:ss:fff. Счётчик идёт в активной игровой сессии.");
            if (kind == ScriptNodeKind.ConnectFont) details.Add("Укажите путь к .ttf/.otf относительно проекта и имя вида dialogue_font. Затем используйте {font=dialogue_font}текст{/font}.");
            if (SystemBlocks.Note(kind) is { } note) details.Add(note);
            if (kind == ScriptNodeKind.ShowVideo) details.Add("Сценарий ждёт окончания видео. Для пропуска по умолчанию нажмите Esc. Передайте false, чтобы выключить пропуск, или строку, например \"Space\" или \"Ctrl+S\", чтобы изменить клавишу.");
            if (kind == ScriptNodeKind.GlobalPause) details.Add("global_pause(секунды, true) разрешает досрочное продолжение по Ctrl+Shift+Esc в активном окне тестового билда. Без второго аргумента пауза длится заданное время, а значение −1 удерживает её до закрытия игры.");
            return details;
        }
        if (LogicCompiler.IsLogic(kind)) return LogicParameters(kind);
        if (kind is ScriptNodeKind.RemoveLayerShaders or ScriptNodeKind.RemoveAllShaders)
            return [kind == ScriptNodeKind.RemoveLayerShaders
                ? "Выберите, откуда снять эффекты. Это может быть вся сцена, фон, спрайт, интерфейс игры или экран с указанным названием. Регистр букв в названии экрана не учитывается."
                : "Блок снимает все активные эффекты в игровой схеме.",
                "Дополнительная галочка скрывает эффекты до следующего контейнера в том же потоке. Если поток завершится раньше, скрытие действует до перезапуска. Каждая параллельная ветка управляет собственной блокировкой."];
        if (ShaderLibrary.IsShader(kind))
        {
            var definition = ShaderLibrary.Get(kind);
            var parameters = new List<string>();
            if (kind == ScriptNodeKind.ShaderContainer)
                parameters.Add("Укажите путь к .kneshader от корня проекта. Дважды щёлкните по карточке, чтобы открыть файл или создать его. В игровой схеме блок включает эффект, когда выполнение доходит до него. В шейдерной схеме контейнер использует координаты UV и текстуру родительского эффекта. Избегайте циклических ссылок между файлами.");
            else
                parameters.Add("Используйте блок в .kneshader. Один выход значения можно подключить к нескольким входам. Свободный вход использует значение по умолчанию.");
            if (definition.Inputs.Length != 0) parameters.Add("Входные порты — " + string.Join(", ", definition.Inputs) + ".");
            if (definition.Outputs.Length != 0) parameters.Add("Выходные порты — " + string.Join(", ", definition.Outputs) + ".");
            parameters.AddRange(ShaderLibrary.Parameters(kind).Select(parameter => $"{parameter.Label} принимает значения от {parameter.Min} до {parameter.Max}. По умолчанию {parameter.Default}."));
            if (kind == ScriptNodeKind.ShaderTexture) parameters.Add("Выберите одну цель для файла. Ею может быть вся сцена, фон, спрайт, интерфейс игры или именованный экран. Вложенный эффект использует цель родителя.");
            if (kind is ScriptNodeKind.ShaderFloat or ScriptNodeKind.ShaderVector or ScriptNodeKind.ShaderColor) parameters.Add("Дайте параметру имя. В окне предпросмотра можно менять его значение, чтобы оценить результат. Эти изменения не записываются в файл.");
            if (kind == ScriptNodeKind.ShaderOutput) parameters.Add("В каждой шейдерной схеме должен быть один итоговый выход. Опция «Не отображать шейдеры» в параметрах сцены временно отключает эффекты.");
            return parameters;
        }
        if (CalculationCompiler.IsCalculation(kind))
        {
            var result = new List<string> { "Укажите переменную state для результата. Её имя начинается с латинской буквы или подчёркивания. Числовое поле принимает значение, имя переменной или выражение, например (a+b)*2. В дробных числах используйте точку." };
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
            if (kind == ScriptNodeKind.Formula)
                result.Add("В выражениях доступны арифметические операции, скобки и функции abs, sqrt, sin, cos, tan, floor, ceil, round, pow, mod, min, max, clamp, lerp и remap. Можно использовать константы pi, tau, e, phi, half_pi и sqrt2.");
            return result;
        }
        if (TriggerBlocks.All.ContainsKey(kind))
            return [kind switch {
                ScriptNodeKind.TriggerKey => "Введите название клавиши или запишите нажатие. Автоповтор удерживаемой клавиши игнорируется.",
                ScriptNodeKind.TriggerCombination => "Для записи сочетания нажмите нужные клавиши, затем отпустите их. Например, Ctrl+Shift+E. Можно использовать левую или правую клавишу-модификатор.",
                ScriptNodeKind.TriggerButton => "Выберите ID игровой кнопки из списка. Например, choice:1 выбирает первый вариант, а screen:Название/ID указывает на кнопку произвольного экрана. Триггер заменяет стандартное действие кнопки.",
                ScriptNodeKind.TriggerHotspot or ScriptNodeKind.TriggerPointerEnterZone => "Задайте координаты верхнего левого угла, ширину и высоту в области 1280×720. Зона масштабируется вместе с игрой. Правая и нижняя границы в неё не входят.",
                ScriptNodeKind.TriggerSequence => "Перечислите клавиши через запятую и отпускайте каждую перед следующей. Например, ↑, ↑, ↓, ↓, ←, →, ←, →, B, A.",
                ScriptNodeKind.TriggerPointerMove => "Срабатывает при изменении положения курсора внутри игрового окна.",
                ScriptNodeKind.TriggerPointerLeave => "Срабатывает в момент выхода курсора из игрового окна.",
                ScriptNodeKind.TriggerDoubleClick => "Выберите кнопку мыши и максимальный интервал между щелчками. По умолчанию интервал составляет 300 мс. Если на той же кнопке настроен одиночный триггер, игра ждёт второй щелчок и выполняет только одну ветку.",
                ScriptNodeKind.TriggerLongPress => "Выберите клавишу или кнопку мыши и время в секундах. По умолчанию срабатывает один раз после пяти секунд удержания.",
                ScriptNodeKind.TriggerWheel => "Выберите нужное направление прокрутки или оба направления.",
                ScriptNodeKind.TriggerFocus => "Выберите получение фокуса, потерю фокуса или оба события.",
                ScriptNodeKind.TriggerResize => "Выберите реакцию во время изменения размера или после того, как окно перестанет изменяться.",
                ScriptNodeKind.TriggerClipboard => "Выберите Ctrl+C для копирования, Ctrl+V для вставки или Ctrl+X для вырезания. Событие срабатывает внутри игрового окна.",
                ScriptNodeKind.TriggerGesture => "Выберите кнопку мыши и траекторию. Удерживайте кнопку, проведите курсором по линии или кругу и отпустите кнопку.",
                _ => "Срабатывает при скрытии текстового окна. Если очистка текста анимирована, событие поступает после окончания анимации." },
                "Подключите вход, чтобы ветка ждала событие и продолжалась через выход. Если вход свободен, обработчик действует всё время, пока открыта сцена. Во время выполнения обработчик не запускается повторно.",
                "Включите «Сработать только один раз», чтобы обработчик выполнился один раз за посещение сцены." ];
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
            ScriptNodeKind.Initialization => ["Целый приоритет от −10000 до 10000. Меньшие числа загружаются раньше, равные — в порядке путей файлов. Эквивалент начального init(приоритет) в KRL. Сцена выполняется по обычному вызову или переходу."],
            ScriptNodeKind.ReverseDialogue => [.. DialogueParameters(kind), "Раскрытие с конца сохраняет порядок исходной реплики. Инверсию слов и букв можно включать одновременно. Разметка {b}…{/b}, {i}…{/i}, {size=24}…{/size} и {font=имя}…{/font} сохраняется при перестановке."],
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
            ScriptNodeKind.ConfigureDialogue or ScriptNodeKind.ConfigureButton => ["Для кнопки выберите цель: save/load/auto_save/pause/rollback/history/choice. Параметры: X/Y/ширина/высота, позиция на экране, шрифт/семейство/размер/вес, цвет текста и фона, прозрачность, скругление, текстура, отступы и выравнивание. Сброс возвращает вид из engine. Меняет игровой UI, не тему редактора."],
            ScriptNodeKind.SetHistorySize => ["Укажите число записей от 0 до 1000. Реплика или выбранный ответ занимает одну запись. При 0 журнал отключён. Отменённые откатом реплики остаются в журнале до достижения лимита."],
            ScriptNodeKind.SetDialogueSpeed => ["Укажите целое число от 0 до 120 символов в секунду. При 0 отрезок реплики появляется сразу. Игрок может снова изменить скорость в настройках игры."],
            ScriptNodeKind.CallVisualLua => ["Путь к модулю .vlua от корня проекта. Блок исполняет код модуля при каждом входе. Двойной щелчок открывает назначенный файл в редакторе."],
            ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script => ["Путь внешнего файла в проекте. Подключить .lua/.cs загружает плагин; Скрипт создаёт/открывает .lua/.krl/.cs в редакторе из настроек и исполняет при входе в блок. Плагины выполняют внешний код; безопасный режим движка отключает сторонние плагины."],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "Блок недоступен в текущем каталоге.")
        };
    }

    private static IReadOnlyList<string> LogicParameters(ScriptNodeKind kind) => [kind switch
    {
        ScriptNodeKind.CreateList => "Введите имя нового массива и начальные элементы в формате JSON. Например, [\"ключ\", \"карта\"]. Для пустого массива укажите []. Именованные массивы входят в сохранения.",
        ScriptNodeKind.DeleteList => "Укажите имя массива, который нужно удалить. После удаления его имя можно использовать для нового массива.",
        ScriptNodeKind.ClearList => "Укажите массив. Блок удаляет его элементы, но сохраняет имя.",
        ScriptNodeKind.ListAdd => "Укажите массив и значение. Новый элемент добавляется в конец.",
        ScriptNodeKind.ListRemove => "Укажите массив и искомое значение. Блок удаляет первое совпадение. Если совпадений нет, массив сохраняется.",
        ScriptNodeKind.ListRemoveAt => "Введите имя массива и индекс элемента. Первый элемент имеет индекс 0.",
        ScriptNodeKind.ListInsert => "Введите индекс и значение. Элементы от этого индекса сдвигаются вправо. Чтобы дописать элемент в конец, укажите длину массива.",
        ScriptNodeKind.ListSet => "Укажите индекс существующего элемента и новое значение.",
        ScriptNodeKind.ListLength => "Введите имя массива и имя переменной результата. В неё запишется число элементов.",
        ScriptNodeKind.ListGet => "Введите имя массива, индекс с отсчётом от 0 и имя переменной, в которую нужно записать элемент.",
        ScriptNodeKind.ListContains => "Укажите массив и искомое значение. Блок записывает true при совпадении и false, если элемент не найден.",
        ScriptNodeKind.ListIndexOf => "Блок записывает индекс первого совпадения. Индексы начинаются с 0. Если элемент не найден, результат равен −1.",
        ScriptNodeKind.ListIsEmpty => "Блок записывает true для пустого массива, в котором нет элементов.",
        ScriptNodeKind.ListShuffle => "Укажите массив, элементы которого нужно расположить в случайном порядке.",
        ScriptNodeKind.ListSort => "Укажите массив из чисел или массив из строк. Элементы сортируются по возрастанию.",
        ScriptNodeKind.ListMerge => "Сначала укажите массив назначения, затем источник. Блок дописывает элементы источника в конец массива назначения.",
        ScriptNodeKind.ListCopy => "Укажите исходный массив и имя копии. Если массив содержит вложенные значения, они тоже копируются.",
        ScriptNodeKind.ListExists => "Введите имя массива и переменную для результата. Блок позволяет проверить наличие массива до обращения к нему.",
        ScriptNodeKind.ListRandom => "Укажите непустой массив и переменную результата. Блок выбирает элемент случайно и записывает его в переменную.",
        ScriptNodeKind.CompoundCondition => "Добавьте сравнения и объедините их в группы AND, OR или NOT. AND требует, чтобы выполнялись все условия, OR достаточно одного, а NOT меняет результат одного вложенного условия. Соедините оба выхода.",
        ScriptNodeKind.StringConcat => "Введите две строки. Блок соединяет их без пробела. Чтобы добавить пробел, включите его в одну из строк.",
        ScriptNodeKind.StringLength => "Введите строку и имя переменной результата. Блок считает символы Unicode.",
        ScriptNodeKind.StringContains => "Введите исходную строку и текст для поиска. Результат равен true, если строка содержит этот текст.",
        ScriptNodeKind.StringReplace => "Укажите исходную строку, фрагмент для поиска и новый фрагмент. Блок заменяет все точные совпадения.",
        ScriptNodeKind.StringUpper => "Укажите строку. Блок переводит её буквы в верхний регистр.",
        ScriptNodeKind.StringLower => "Укажите строку. Блок переводит её буквы в нижний регистр.",
        ScriptNodeKind.ConvertType => "Выберите целевой тип и переменную результата. Для преобразования в логическое значение используются true, false, 1 и 0.",
        ScriptNodeKind.CopyVariable => "Укажите переменную-источник и имя переменной назначения в state.",
        ScriptNodeKind.ResetVariable => "Укажите имя переменной. Блок восстанавливает начальное значение сцены или проекта. Если начального значения нет, ключ удаляется.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    }];
    private static string[] DialogueParameters(ScriptNodeKind kind) => ["Говорящий: вручную или профиль персонажа по кнопке. Пустое поле наследует установленного спикера; «Рассказчик / мысль» явно снимает его. Текст — содержимое диалога.",
        kind == ScriptNodeKind.AutoDialogue ? "Длительность до автоматического перехода и разрешение пропуска кликом." : kind == ScriptNodeKind.EffectDialogue ? "Эффект всего текста: Shake, Wave, Sway, Bounce, Float, Explode, Pulse, Spin, Squash/Stretch, Glitch, Blur, Fade/Dissolve, Scramble, Rainbow, Color Cycle, Растекание. Скорость, цикличность, сила и цвета настраиваются в свойствах." : "Переход к следующей реплике — кликом/пробелом. У каждого фрейма есть ID внутри сцены и игры."];
}
