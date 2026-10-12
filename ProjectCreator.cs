using System.Globalization;
using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Creates complete projects in a staging folder before publishing them.</summary>
public static class ProjectCreator
{
    public static Task<string> CreateProjectAsync(string projectName, string basePath, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateProject(projectName, basePath, cancellationToken), cancellationToken);

    public static string CreateProject(string projectName, string basePath, CancellationToken cancellationToken = default)
    {
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
            File.WriteAllText(Path.Combine(staging, "game", "scenes", "main.krl"), """
                -- Главная сцена. KRL использует синтаксис Lua.
                function main()
                    d(nil, "Добро пожаловать! Это базовая сцена.")
                    d(player_name, "Нажмите «Далее» или пробел, чтобы продолжить.")
                end
                """);
            File.WriteAllText(Path.Combine(staging, "game", "scripts", "gamelogic.lua"), """
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
                game/scenes/main.krl — главная сцена с функцией main().
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
                Metadata = new ProjectMetadata { Name = projectName, CreatedDate = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                GameInfo = new GameInfo { Title = projectName, Description = "Новая визуальная новелла" },
                EntryPoints = new EntryPoints { MainScene = "game/scenes/main.krl", InitScript = "game/scripts/gamelogic.lua" }
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
