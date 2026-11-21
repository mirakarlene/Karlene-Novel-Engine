using System.IO;
using System.Text;

namespace KNE;

internal static class ProjectDiagnosticsTemplate
{
    internal const string CodePath = "game/scripts/gamelogic.lua";
    internal const string GraphPath = "game/scripts/gamelogic.vlua";

    private const string Previous = """
        -- Отчёт о запуске: вход в сценарий, запись сохранений и среда выполнения.
        local function print_diagnostics()
            local report = project_diagnostics()
            print("Диагностика KNE — " .. report.project)
            print("Точка входа: " .. (report.entry or "не определена") .. " · файлов сценария: " .. report.files)
            print("Среда: " .. report.environment .. " · " .. report.process_architecture .. " / " .. report.os_architecture)
            print("Сохранения: " .. report.saves.message)
            if report.saves.available_bytes then
                print("Доступно байт: " .. report.saves.available_bytes)
            end
            if report.persistent.directory ~= report.saves.directory then
                print("Глобальные данные: " .. report.persistent.message)
            end
            for _, warning in ipairs(report.warnings) do print("Предупреждение: " .. warning) end
        end

        print_diagnostics()

        function process_command(command)
            if command == "diagnostics" then print_diagnostics()
            else print("Для повторной проверки введите diagnostics.") end
        end
        """;

    internal const string Legacy = """
        -- Для смены приоритета добавьте init(-10) первым оператором; меньшие числа загружаются раньше.
        player_name = "Игрок"
        state.score = state.score or 0
        print("Игра инициализирована!")
        function process_command(command)
            d(player_name, command)
        end
        """;

    internal const string Source = """
        -- Проверка целостности сценариев, ресурсов и папок данных.
        -- Команда diagnostics повторяет проверку, не выполняя проверяемые сценарии.
        local function print_diagnostics()
            local report = project_diagnostics()
            print("Диагностика KNE — " .. report.project)
            print("Точка входа: " .. (report.entry or "не определена"))
            print("Сценариев проверено: " .. report.integrity.scripts .. " · ссылок на ресурсы: " .. report.integrity.assets)
            print("Среда: " .. report.environment .. " · " .. report.process_architecture .. " / " .. report.os_architecture)
            if report.architecture_translation_possible then
                print("Возможна эмуляция архитектуры процесса.")
            end
            if report.environment == "wine" or report.environment == "proton" then
                print("Обнаружена среда совместимости: " .. report.environment)
            end
            if not report.requested_saves.writable then
                print("Предупреждение: " .. report.requested_saves.message)
            end
            print("Сохранения (" .. report.saves.directory .. "): " .. report.saves.message)
            if report.saves.available_bytes then print("Свободно байт: " .. report.saves.available_bytes) end
            if report.persistent.directory ~= report.saves.directory then
                print("Глобальные данные: " .. report.persistent.message)
            end
            for _, warning in ipairs(report.warnings) do print("Предупреждение: " .. warning) end
            for _, issue in ipairs(report.integrity.issues) do print("Ошибка целостности: " .. issue) end
            if report.integrity.issue_count == 0 then
                print("В проверенных сценариях и статических ссылках ошибок не обнаружено.")
            else
                print("Обнаружено замечаний: " .. report.integrity.issue_count)
            end
        end

        print_diagnostics()

        function process_command(command)
            if command == "diagnostics" then print_diagnostics()
            else print("Для повторной проверки введите diagnostics.") end
        end
        """;

    internal static string Create(ProjectAuthoringMode mode, string root)
    {
        bool visual = mode == ProjectAuthoringMode.Visual;
        string relative = visual ? GraphPath : CodePath;
        File.WriteAllText(PathSafety.ResolveRelative(root, relative), visual ? VisualLua.ParseSource(Source).Serialize() : Source);
        return relative;
    }

    internal static bool IsStockReport(string source)
    {
        string normalized = source.TrimStart('\uFEFF').ReplaceLineEndings("\n").Trim();
        string current = Source.ReplaceLineEndings("\n");
        return normalized == current || normalized == Previous.ReplaceLineEndings("\n") ||
            normalized == "player_name = \"Игрок\"\nstate.score = state.score or 0\n\n" + current;
    }

    internal static async Task<bool> UpdateStockAsync(ProjectInfo project, CancellationToken token)
    {
        string path = PathSafety.ResolveRelative(project.Path, CodePath);
        if (!File.Exists(path) || new FileInfo(path).Length > 8192) return false;
        if (!path.Equals(project.InitScriptPath, StringComparison.OrdinalIgnoreCase)) return false;
        byte[] original = await File.ReadAllBytesAsync(path, token);
        string previous = Encoding.UTF8.GetString(original).TrimStart('\uFEFF').ReplaceLineEndings("\n").TrimEnd('\n');
        bool legacy = previous == Legacy.ReplaceLineEndings("\n");
        if (!legacy && previous != Previous.ReplaceLineEndings("\n")) return false;
        token.ThrowIfCancellationRequested();
        string source = legacy ? "player_name = \"Игрок\"\nstate.score = state.score or 0\n\n" + Source : Source;
        await ProjectSettingsTransaction.CommitAsync(new Dictionary<string, byte[]> { [path] = Encoding.UTF8.GetBytes(source) },
            new Dictionary<string, byte[]?> { [path] = original });
        return true;
    }
}
