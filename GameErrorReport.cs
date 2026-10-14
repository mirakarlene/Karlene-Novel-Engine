using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KNE;

/// <summary>Diagnostic information captured when a game script fails, suitable for display and worker transport.</summary>
public sealed record GameErrorReport(string ProjectName, string ProjectTitle, string ProjectPath,
    DateTimeOffset OccurredAt, string OperatingSystem, string DeviceName, string Architecture,
    string ErrorType, string Message, string? File, int? Line, string Details, string Operation)
{
    public bool TransitionFailure { get; init; }
    private const string DataKey = "KNE.GameErrorReport";

    /// <summary>Reads a previously captured report without changing its occurrence time or source information.</summary>
    public static GameErrorReport? TryGet(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current.Data[DataKey] is GameErrorReport report) return report;
        return null;
    }

    /// <summary>Attaches a structured report while preserving the exception's original type and message.</summary>
    public static void Attach(Exception error, GameErrorReport report) => error.Data[DataKey] = report;

    /// <summary>Captures project and local device information; an existing script report retains its original timestamp.</summary>
    public static GameErrorReport Create(ProjectInfo project, Exception error, string operation = "Выполнение сценария")
    {
        if (TryGet(error) is { } existing) return existing;
        var location = FindLocation(project, error.Message);
        string? file = location.File ?? (error as FileNotFoundException)?.FileName;
        return new(project.Name, project.Title, project.Path, DateTimeOffset.Now,
            RuntimeInformation.OSDescription, Environment.MachineName, RuntimeInformation.OSArchitecture.ToString(),
            error.GetType().Name, error.Message, file, location.Line, error.ToString(), operation);
    }

    private static (string? File, int? Line) FindLocation(ProjectInfo project, string message)
    {
        string normalized = message.Replace('\\', '/');
        foreach (var file in project.Files.Where(f => ScriptDocument.IsCodePath(f.FilePath) || ScriptDocument.IsVisualPath(f.FilePath)))
        {
            string relative = Path.GetRelativePath(project.Path, file.FilePath).Replace('\\', '/');
            var match = Regex.Match(normalized, Regex.Escape(relative) + @":(\d+)\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int line) && line > 0) return (relative, line);
        }
        return (null, null);
    }

    /// <summary>Formats a copyable report with explicit unknown source locations and timezone-qualified time.</summary>
    public string ToDisplayText()
    {
        var text = new StringBuilder()
            .AppendLine("Проект: " + ProjectTitle).AppendLine("Название проекта: " + ProjectName)
            .AppendLine("Папка проекта: " + ProjectPath)
            .AppendLine("Файл: " + (File ?? "Не определён"))
            .AppendLine("Строка: " + (Line?.ToString(CultureInfo.InvariantCulture) ?? "Не определена"));
        if (File != null && ScriptDocument.IsVisualPath(File) && Line != null)
            text.AppendLine("Строка относится к Lua, сгенерированному из блок-схемы.");
        return text.AppendLine("Ошибка: " + Message).AppendLine("Тип ошибки: " + ErrorType)
            .AppendLine("Когда: " + OccurredAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .AppendLine("Действие: " + Operation).AppendLine("ОС: " + OperatingSystem)
            .AppendLine("Устройство: " + DeviceName).AppendLine("Архитектура ОС: " + Architecture)
            .AppendLine().AppendLine("Подробности:").Append(Details).ToString();
    }
}
