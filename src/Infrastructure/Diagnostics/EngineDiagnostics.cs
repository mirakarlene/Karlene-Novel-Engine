using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace KNE;

internal sealed record EngineTraceEntry(DateTimeOffset Time, string Operation, string Detail);
internal sealed record EngineFailureReport(DateTimeOffset Time, string Kind, string Message, string Component,
    string Exception, string OperatingSystem, string Runtime, int ProcessId, bool PluginsDisabled,
    string Project, long ManagedBytes, IReadOnlyList<EngineTraceEntry> Trace)
{
    internal string ToDisplayText() => $"Время: {Time:O}\nТип сбоя: {Kind}\nПричина: {Message}\nКомпонент: {Component}\n" +
        $"Проект: {Project}\nОС: {OperatingSystem}\n.NET: {Runtime}\nПроцесс: {ProcessId}\n" +
        $"Плагины отключены: {PluginsDisabled}\nУправляемая память: {ManagedBytes:N0} байт\n\nПодробности\n{Exception}\n\nПоследние операции\n" +
        string.Join("\n", Trace.Select(entry => $"{entry.Time:O} · {entry.Operation} · {entry.Detail}"));
}

internal sealed record EngineJournal(int ProcessId, long StartedTicks, bool CleanExit, bool PluginsDisabled,
    string Project, EngineTraceEntry[] Trace, EngineFailureReport? Failure = null);

/// <summary>Bounded engine diagnostics. Game dialogue and variable values are never recorded here.</summary>
internal sealed class EngineDiagnostics
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private EngineJournal journal;
    internal static EngineDiagnostics? Current { get; set; }
    internal string SessionPath { get; }
    internal string ReportPath => Path.ChangeExtension(SessionPath, ".txt");
    internal string? PersistenceError { get; private set; }
    internal EngineJournal Snapshot { get { lock (sync) return journal; } }

    internal EngineDiagnostics(string sessionPath, int processId, long startedTicks, bool pluginsDisabled, TimeProvider? clock = null)
    {
        SessionPath = Path.GetFullPath(sessionPath);
        this.clock = clock ?? TimeProvider.System;
        journal = new(processId, startedTicks, false, pluginsDisabled, "Не открыт", []);
        Record("Запуск движка", pluginsDisabled ? "Без сторонних плагинов" : "Обычный режим");
    }

    internal static void Trace(string operation, string detail = "") => Current?.Record(operation, detail);

    internal void SetProject(string title, string path)
    {
        lock (sync) journal = journal with { Project = Limit(title + " · " + path, 2048) };
        Record("Проект открыт", title);
    }

    internal void Record(string operation, string detail = "")
    {
        lock (sync)
        {
            var entries = journal.Trace.TakeLast(199).Append(new EngineTraceEntry(clock.GetUtcNow(), Limit(operation, 256), Limit(detail, 1024))).ToArray();
            journal = journal with { Trace = entries };
            Persist();
        }
    }

    internal EngineFailureReport Report(Exception exception, string kind)
    {
        lock (sync)
        {
            if (journal.Failure is { } first)
            {
                Record("Повторная ошибка", kind + ": " + exception.Message);
                return first;
            }
            var cause = exception.GetBaseException();
            var report = new EngineFailureReport(clock.GetUtcNow(), kind, Limit(cause.Message, 4096),
                cause.TargetSite?.DeclaringType?.FullName + "." + cause.TargetSite?.Name is { Length: > 1 } component ? component : cause.GetType().FullName ?? "Неизвестный компонент",
                Limit(exception.ToString(), 32768), RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                journal.ProcessId, journal.PluginsDisabled, journal.Project, GC.GetTotalMemory(false), journal.Trace);
            journal = journal with { Failure = report };
            Persist();
            TryWrite(ReportPath, report.ToDisplayText());
            return report;
        }
    }

    internal void Complete()
    {
        lock (sync) { journal = journal with { CleanExit = true }; Persist(); }
    }

    private void Persist() => TryWrite(SessionPath, JsonSerializer.Serialize(journal));

    private void TryWrite(string path, string contents)
    {
        string temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, true);
            PersistenceError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            PersistenceError = error.Message;
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static EngineJournal Read(string path)
    {
        if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Журнал движка больше 4 МБ.");
        var data = JsonSerializer.Deserialize<EngineJournal>(File.ReadAllText(path)) ?? throw new InvalidDataException("Пустой журнал движка.");
        if (data.Trace is null || data.Trace.Length > 200 || data.Project is null) throw new InvalidDataException("Некорректный журнал движка.");
        return data;
    }

    internal static EngineFailureReport? RecordUnexpectedExit(string path, int exitCode, TimeProvider? clock = null)
    {
        var previous = Read(path);
        if (previous.CleanExit) return null;
        if (previous.Failure != null) return previous.Failure;
        var diagnostics = new EngineDiagnostics(path, previous.ProcessId, previous.StartedTicks, previous.PluginsDisabled, clock);
        diagnostics.journal = previous;
        string reason = unchecked((uint)exitCode) switch
        {
            0xC0000005 => "Недопустимое обращение к памяти (нативный сбой).",
            0xC00000FD => "Переполнение стека.",
            0xC0000409 => "Принудительное аварийное завершение процесса (FailFast).",
            _ => "Процесс движка завершился без штатного закрытия. Точную причину по одному коду завершения установить нельзя."
        };
        return diagnostics.Report(new InvalidOperationException($"{reason} Код: {exitCode} (0x{unchecked((uint)exitCode):X8})."), "Аварийное завершение процесса");
    }

    private static string Limit(string? value, int maximum) => value is null ? "" : value.Length <= maximum ? value : value[..maximum] + "…";
}
