using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class GameErrorReportTests
{
    [Fact]
    public void Display_IncludesExactProjectSourceErrorTimeAndDeviceFields_AndSurvivesSerialization()
    {
        var report = new GameErrorReport("Название 🌸", "Заголовок игры", "E:/Projects/Novel", new DateTimeOffset(2026, 10, 14, 19, 20, 21, 123, TimeSpan.FromHours(5)),
            "Microsoft Windows test build", "TEST-PC", "X64", "LuaScriptException", "attempt to index nil", "game/scenes/child.krl", 7, "trace\nchild -> main", "Продолжение сценария");
        var restored = JsonSerializer.Deserialize<GameErrorReport>(JsonSerializer.Serialize(report))!;
        Assert.Equal(report, restored);
        string text = restored.ToDisplayText();
        foreach (string expected in new[] { "Проект: Заголовок игры", "Название проекта: Название 🌸", "Папка проекта: E:/Projects/Novel", "Файл: game/scenes/child.krl", "Строка: 7", "Ошибка: attempt to index nil", "Тип ошибки: LuaScriptException", "Когда: 2026-10-14 19:20:21.123 +05:00", "ОС: Microsoft Windows test build", "Устройство: TEST-PC", "Архитектура ОС: X64", "Действие: Продолжение сценария", "trace\nchild -> main" })
            Assert.Contains(expected, text);
    }

    [Fact]
    public void UnknownLocation_IsExplicit_AndGraphLineIsDescribedAsGeneratedLua()
    {
        var report = new GameErrorReport("Project", "Title", "root", DateTimeOffset.UnixEpoch, "OS", "Device", "X64", "IOException", "worker exited", null, null, "details", "Продолжение сценария");
        Assert.Contains("Файл: Не определён", report.ToDisplayText());
        Assert.Contains("Строка: Не определена", report.ToDisplayText());
        Assert.DoesNotContain("Строка: 0", report.ToDisplayText());
        Assert.Contains("Lua, сгенерированному", (report with { File = "helper.knegraph", Line = 12 }).ToDisplayText());
    }

    [Fact]
    public async Task AttachedReport_PreservesOriginalOccurrenceTimeAndMetadata_WhenWrappedForUi()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var error = new InvalidOperationException("original");
        var report = GameErrorReport.Create(project, error, "Выбор ответа") with { OccurredAt = DateTimeOffset.UnixEpoch, File = "game/scenes/child.lua", Line = 9 };
        GameErrorReport.Attach(error, report);
        var wrapped = new IOException("transport wrapper", error);
        Assert.Same(report, GameErrorReport.TryGet(wrapped));
        Assert.Same(report, GameErrorReport.Create(project, wrapped, "New UI action"));
        Assert.Equal(DateTimeOffset.UnixEpoch, GameErrorReport.Create(project, wrapped).OccurredAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreflightParserFailure_ReportsExactFileAndLine_ForInitAndGraphJson(bool graph)
    {
        using var fixture = new TempProject();
        string path = graph ? "broken.knegraph" : "broken.lua";
        fixture.Write(path, graph ? "{\n\"FunctionName\": \"helper\",\n\"Nodes\": ]\n}" : "-- init('fake') in comment\n\ninit('early')");
        var project = await fixture.LoadAsync();
        var error = Assert.Throws<InvalidDataException>(() => GameScripts.Load(project));
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal(path, report.File); Assert.Equal(3, report.Line);
        Assert.Equal("Проверка сценариев", report.Operation);
        Assert.Equal(project.Name, report.ProjectName);
    }
}
