using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

public partial class MainWindow
{
    private readonly Action<Window> presentTestingWindow;
    private readonly Action<string> presentAnalysisReport;
    private void OpenTestingWindow(bool sceneTest)
    {
        if (currentProject == null || closed) return;
        try
        {
            if (ActiveEditor.Document is { Graph.IsShader: true } shaderDocument)
            {
                var shaderWindow = new ShaderPreviewWindow(currentProject, shaderDocument.Graph, shaderDocument.FilePath); EditorTheme.Apply(shaderWindow, settings.Theme); presentTestingWindow(shaderWindow); return;
            }
            var selection = ActiveEditor.CapturePreview(sceneTest);
            if (selection == null) return;
            presentTestingWindow(new SceneTestingWindow(currentProject, selection with { FromMain = false }, sceneTest));
        }
        catch (Exception error) { OperationStatus.Text = "Тестирование: " + error.Message; }
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => Close();
    private async void BtnSaveAll_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(SaveAllEditorsAsync, "Сохранение проекта…");

    private async void BtnImportFile_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async () =>
    {
        if (currentProject is not { } project || PickProjectFormat(import: true) is not { } format) return;
        if (format.Extension is ".rpy" or ".docx")
        {
            await ImportScriptAsync(project, format.Extension);
            return;
        }
        string gameRoot = PathSafety.ResolveRelative(project.Path, "game");
        if (format.IsDirectory)
        {
            var folder = new OpenFolderDialog { Title = "Выберите папку для импорта" };
            if (folder.ShowDialog(this) != true) return;
            string name = Path.GetFileName(PathSafety.NormalizeDirectory(folder.FolderName));
            string? target = PickNamedDestination(gameRoot, gameRoot, name.Length > 0 ? name : "Папка", "");
            if (target == null) return;
            await ProjectDirectories.CopyAsync(project.Path, folder.FolderName, target, lifetime.Token);
            await OpenProjectAsync(project.Path);
            return;
        }
        var source = new OpenFileDialog { Title = "Выберите файл для импорта", Filter = format.Filter };
        if (source.ShowDialog(this) != true) return;
        string extension = Path.GetExtension(source.FileName);
        if (!format.Accepts(extension))
            throw new InvalidDataException("Выберите файл для импорта через плитку «" + format.Title + "».");
        string? destination = PickNamedDestination(gameRoot, gameRoot, Path.GetFileNameWithoutExtension(source.FileName), extension);
        if (destination == null) return;
        await ProjectImport.CopyFileAsync(project.Path, source.FileName, destination, lifetime.Token);
        await OpenProjectAsync(project.Path);
    }, "Импорт файла…");

    private async Task ImportScriptAsync(ProjectInfo project, string extension)
    {
        var source = new OpenFileDialog { Title = "Экспериментальный импорт " + extension, Filter = $"Сценарий (*{extension})|*{extension}" };
        if (source.ShowDialog(this) != true) return;
        if (!Path.GetExtension(source.FileName).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выберите исходный файл формата " + extension + ".");
        string prefix = "import_" + Guid.NewGuid().ToString("N")[..8];
        var conversion = await ScriptImportConverter.FromPathAsync(source.FileName, prefix, lifetime.Token);
        var review = new ImportReviewWindow(conversion, Path.GetFileName(source.FileName)) { Owner = this };
        EditorTheme.Apply(review, settings.Theme);
        if (review.ShowDialog() != true) return;
        string gameRoot = PathSafety.ResolveRelative(project.Path, "game");
        string? target = PickNamedDestination(gameRoot, Path.Combine(gameRoot, "scenes"), Path.GetFileNameWithoutExtension(source.FileName), ".krl");
        if (target == null) return;
        await ProjectImport.WriteScriptAsync(project.Path, target, review.EditedCode, lifetime.Token);
        await OpenProjectAsync(project.Path); await OpenScriptAsync(target);
    }

    public async Task<string?> AnalyzeCurrentProjectAsync()
    {
        if (currentProject == null) return null;
        await SaveAllEditorsAsync();
        var result = await ProjectAnalyzer.AnalyzeAsync(currentProject.Path, lifetime.Token);
        string report = await ProjectAnalyzer.SaveReportAsync(currentProject.Path, result, lifetime.Token);
        presentAnalysisReport(report);
        OperationStatus.Text = $"Анализ завершён: ошибок {result.ErrorCount}, предупреждений {result.WarningCount}, сообщений {result.InformationCount}. Отчёт: {report}";
        return report;
    }
    private async void BtnAnalyzeProject_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async () => { await AnalyzeCurrentProjectAsync(); }, "Анализ проекта без запуска…");
}
