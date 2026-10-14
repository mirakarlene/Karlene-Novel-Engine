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

    public static string CreateProject(string projectName, string basePath, CancellationToken cancellationToken = default) =>
        CreateProject(projectName, basePath, ProjectAuthoringMode.Code, cancellationToken);

    public static string CreateProject(string projectName, string basePath, ProjectAuthoringMode authoringMode, CancellationToken cancellationToken = default)
    {
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
            Directory.CreateDirectory(Path.Combine(staging, "engine"));
            Directory.CreateDirectory(Path.Combine(staging, "saves"));
            EngineSettings.WriteTemplate(staging);
            Directory.CreateDirectory(Path.Combine(staging, "metadata"));
            cancellationToken.ThrowIfCancellationRequested();
            string mainScene = authoringMode == ProjectAuthoringMode.Visual ? "game/scenes/main.knegraph" : "game/scenes/main.krl";
            if (authoringMode == ProjectAuthoringMode.Visual)
            {
                var graph = VisualScript.Create();
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
                    d(player_name, "Нажмите «Далее» или пробел, чтобы продолжить.")
                end
                """);
            File.WriteAllText(Path.Combine(staging, "game", "scripts", "gamelogic.lua"), """
                -- Для смены приоритета добавьте init(-10) первым оператором; меньшие числа загружаются раньше.
                player_name = "Игрок"
                state.score = state.score or 0
                print("Игра инициализирована!")
                function process_command(command)
                    d(player_name, command)
                end
                """);
            File.WriteAllText(Path.Combine(staging, "README.txt"), $$"""
                Проект: {{projectName}}
                Точки входа задаются в project.json.
                engine/*.json — окно, оформление, меню и начальное состояние; см. engine/README.txt.
                game/scripts/gamelogic.lua — инициализация и process_command(command).
                {{mainScene}} — главная сцена с функцией main().
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
                Metadata = new ProjectMetadata { Name = projectName, CreatedDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                GameInfo = new GameInfo { Title = projectName, Description = "Новая визуальная новелла" },
                EntryPoints = new EntryPoints { MainScene = mainScene, InitScript = "game/scripts/gamelogic.lua" }
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
