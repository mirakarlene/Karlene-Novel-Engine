using System.IO;

namespace KNE;

/// <summary>Read-only description of a file on disk.</summary>
public sealed class ProjectFile
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string FileType => Path.GetExtension(FilePath).ToLowerInvariant() switch
    {
        ".knegraph" => "Блок-схема",
        ".kneshader" => "Шейдерная блок-схема",
        ".cs" or ".bat" or ".lua" or ".json" or ".js" or ".py" or ".html" or ".css" or ".rpy" or ".rpyc" or ".rpa" or ".krl" => "Скрипт",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" => "Рисунок",
        ".mp3" or ".wav" or ".flac" => "Аудио",
        ".mp4" or ".avi" or ".mkv" or ".webm" => "Видео",
        ".txt" or ".docx" or ".pdf" => "Документ",
        ".save" => "Сохранение",
        ".exe" => "Исполняемый файл",
        _ => "Другое"
    };
}
