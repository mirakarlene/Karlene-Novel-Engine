using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Creates complete projects in a staging folder before publishing them.</summary>
public static class ProjectCreator
{
    public static Task<string> CreateProjectAsync(string projectName, string basePath, CancellationToken cancellationToken = default) =>
        CreateProjectAsync(projectName, basePath, ProjectAuthoringMode.Code, cancellationToken);

    public static Task<string> CreateProjectAsync(string projectName, string basePath, ProjectAuthoringMode authoringMode, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateProject(projectName, basePath, authoringMode, cancellationToken), cancellationToken);

    /// <summary>Creates a project asynchronously with explicit window, rendering and palette choices.</summary>
    public static Task<string> CreateProjectAsync(string projectName, string basePath, ProjectAuthoringMode authoringMode, ProjectCreationOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateProject(projectName, basePath, authoringMode, options, cancellationToken), cancellationToken);

    public static string CreateProject(string projectName, string basePath, CancellationToken cancellationToken = default) =>
        CreateProject(projectName, basePath, ProjectAuthoringMode.Code, cancellationToken);

    public static string CreateProject(string projectName, string basePath, ProjectAuthoringMode authoringMode, CancellationToken cancellationToken = default) =>
        CreateProject(projectName, basePath, authoringMode, new ProjectCreationOptions(), cancellationToken);

    /// <summary>Atomically creates a project using validated creation choices.</summary>
    public static string CreateProject(string projectName, string basePath, ProjectAuthoringMode authoringMode, ProjectCreationOptions options, CancellationToken cancellationToken = default)
        => CreateProjectCore(projectName, basePath, authoringMode, options, empty: false, cancellationToken);

    internal static Task<string> CreateEmptyProjectAsync(string projectName, string basePath, ProjectAuthoringMode authoringMode,
        ProjectCreationOptions options, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateProjectCore(projectName, basePath, authoringMode, options, empty: true, cancellationToken), cancellationToken);

    private static string CreateProjectCore(string projectName, string basePath, ProjectAuthoringMode authoringMode,
        ProjectCreationOptions options, bool empty, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate();
        if (!Enum.IsDefined(authoringMode)) throw new ArgumentOutOfRangeException(nameof(authoringMode));
        PathSafety.ValidateFileName(projectName);
        string root = PathSafety.NormalizeDirectory(basePath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Выбранная папка не существует.");
        string destination = PathSafety.EnsureInside(root, Path.Combine(root, projectName));
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Проект с таким именем уже существует.");

        string staging = PathSafety.EnsureInside(root, Path.Combine(root, ".kne-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string directory in new[] { "scenes", "characters", "backgrounds", "resources", "scripts", "audio", "gui" })
                Directory.CreateDirectory(Path.Combine(staging, "game", directory));
            Directory.CreateDirectory(Path.Combine(staging, "game", "shaders"));
            Directory.CreateDirectory(Path.Combine(staging, "engine"));
            Directory.CreateDirectory(Path.Combine(staging, "plugins"));
            Directory.CreateDirectory(Path.Combine(staging, "saves"));
            EngineSettings.WriteTemplate(staging, options);
            Directory.CreateDirectory(Path.Combine(staging, "metadata"));
            cancellationToken.ThrowIfCancellationRequested();
            string mainScene = authoringMode == ProjectAuthoringMode.Visual ? "game/scenes/main.knegraph" : "game/scenes/main.krl";
            if (empty && authoringMode == ProjectAuthoringMode.Visual)
            {
                var graph = new VisualScript
                {
                    Nodes =
                    [
                        new() { Id = "start", Kind = ScriptNodeKind.Start, X = 40, Y = 60, Next = "end" },
                        new() { Id = "end", Kind = ScriptNodeKind.End, X = 330, Y = 60 }
                    ]
                };
                File.WriteAllText(Path.Combine(staging, "game", "scenes", "main.knegraph"), graph.Serialize());
            }
            else if (empty)
                File.WriteAllText(Path.Combine(staging, "game", "scenes", "main.krl"), "function main()\nend\n");
            else if (authoringMode == ProjectAuthoringMode.Visual)
            {
                var graph = VisualScript.Create();
                graph.Nodes.Single(node => node.Kind == ScriptNodeKind.End).EndAction = ScriptEndAction.Menu;
                var introduction = graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Dialogue);
                introduction.Text = "Добро пожаловать! Это базовая сцена.";
                introduction.Next = "continue";
                graph.Nodes.Single(n => n.Kind == ScriptNodeKind.End).X = 910;
                graph.Nodes.Insert(2, new ScriptNode
                {
                    Id = "continue", Kind = ScriptNodeKind.Dialogue, X = 620, Y = 60,
                    Speaker = "Игрок", Text = "Нажмите «Далее» или пробел, чтобы продолжить.", Next = "end"
                });
                File.WriteAllText(Path.Combine(staging, "game", "scenes", "main.knegraph"), graph.Serialize());
            }
            else File.WriteAllText(Path.Combine(staging, "game", "scenes", "main.krl"), """
                -- Главная сцена. KRL использует синтаксис Lua.
                function main()
                    d(nil, "Добро пожаловать! Это базовая сцена.")
                    d("Игрок", "Нажмите «Далее» или пробел, чтобы продолжить.")
                end
                """);
            string initScript = ProjectDiagnosticsTemplate.Create(empty ? ProjectAuthoringMode.Code : authoringMode, staging);
            File.WriteAllText(Path.Combine(staging, "README.txt"), $$"""
                Проект: {{projectName}}
                Точки входа задаются в project.json.
                engine/*.json — окно, оформление, меню и начальное состояние; см. engine/README.txt.
                {{initScript}} — стартовая диагностика целостности проекта. Команда diagnostics повторяет проверку.
                Проверка записи использует отдельный временный файл в папке сохранений. Отчёт учитывает рекомендации Microsoft и Valve и модель качества ISO/IEC 25010.
                {{mainScene}} — главная сцена с функцией main().
                game/shaders — шейдерные файлы .kneshader; подключайте контейнером в нужном месте сценария.
                Основной способ: {{(authoringMode == ProjectAuthoringMode.Visual ? "визуальные блок-схемы; откройте main.knegraph и редактируйте связанные блоки" : "традиционный код; откройте main.krl и редактируйте функцию main()")}}.
                init(-10) в начале Lua/KRL подключает файл к загрузке: меньшие числа раньше.
                main() — единственная глобальная функция старта во всём проекте; дубликаты показывают экран ошибки.
                goto(chapter) — переход в function chapter() без возврата; также goto_scene(chapter) и jump(chapter).
                call(chapter) — вызов function chapter() с возвратом после её завершения; также call_scene(chapter).
                Аргумент перехода — функция или строка с её именем, без пути к файлу.
                Остальные функции имеют свои имена; у блок-схем имя задаётся полем «Функция».
                scene("chapter", function() ... end) объявляет сцену, доступную по имени.
                API: d(имя или nil, текст), print(...), print_to_ui(текст),
                choice(вопрос, {"Ответ 1", "Ответ 2"}) возвращает индекс ответа с 1.
                background("game/backgrounds/фон.png"), show_character(путь, "center"), hide_character(),
                play_music("game/audio/музыка.mp3", true), stop_music(), play_sound(путь).
                Переменные для сохранения храните в state: state.score = 0.
                Кнопки «Сохранить» и «Загрузить» работают с ручным слотом; есть автосохранение.
                set_window_title(текст), set_window_size(ширина, высота), set_bg_color(цвет).
                """);
            var configuration = new ProjectConfiguration
            {
                AuthoringMode = authoringMode,
                Metadata = new ProjectMetadata { Name = projectName, CreationName = projectName, CreatedDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                GameInfo = new GameInfo { Title = projectName, Description = "Новая визуальная новелла" },
                EntryPoints = new EntryPoints { MainScene = mainScene, InitScript = initScript }
            };
            File.WriteAllText(Path.Combine(staging, "project.json"), JsonSerializer.Serialize(configuration, ProjectConfiguration.JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                // Only our unique staging directory is eligible for cleanup.
                PathSafety.EnsureInside(root, staging);
                Directory.Delete(staging, true);
            }
        }
    }
}
