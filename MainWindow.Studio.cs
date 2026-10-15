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
            var selection = SceneEditor.CapturePreview(sceneTest);
            if (selection == null) return;
            presentTestingWindow(new SceneTestingWindow(currentProject, selection with { FromMain = false }, sceneTest));
        }
        catch (Exception error) { OperationStatus.Text = "Тестирование: " + error.Message; }
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e) => Close();
    private async void BtnSaveAllExit_Click(object sender, RoutedEventArgs e)
    {
        bool saved = false;
        await RunOperationAsync(async () => { await SceneEditor.SaveAsync(); await LibraryView.FlushPendingPreferencesAsync(); saved = true; }, "Сохранение проекта…");
        if (saved && !closed) { closeApproved = true; Close(); }
    }

    private async void BtnImportFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project) return;
        var source = new OpenFileDialog { Title = "Выберите файл для импорта", Filter = "Все файлы (*.*)|*.*" };
        if (source.ShowDialog(this) != true) return;
        var destination = CreateFileDialog(Path.Combine(project.Path, "game"), Path.GetFileName(source.FileName));
        destination.Title = "Куда импортировать файл внутри game";
        if (destination.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            await ProjectImport.CopyFileAsync(project.Path, source.FileName, destination.FileName, lifetime.Token);
            await OpenProjectAsync(project.Path);
        }, "Импорт файла…");
    }

    private async void BtnImportScript_Click(object sender, RoutedEventArgs e)
    {
        if (currentProject is not { } project || sender is not Button button) return;
        string extension = (string)button.Tag;
        var source = new OpenFileDialog { Title = "Экспериментальный импорт " + extension, Filter = $"Сценарий (*{extension})|*{extension}" };
        if (source.ShowDialog(this) != true) return;
        await RunOperationAsync(async () =>
        {
            string prefix = "import_" + Guid.NewGuid().ToString("N")[..8];
            var conversion = await ScriptImportConverter.FromPathAsync(source.FileName, prefix, lifetime.Token);
            var review = new ImportReviewWindow(conversion, Path.GetFileName(source.FileName)) { Owner = this };
            if (review.ShowDialog() != true) return;
            var destination = CreateFileDialog(Path.Combine(project.Path, "game", "scenes"), Path.GetFileNameWithoutExtension(source.FileName) + ".krl");
            destination.Filter = "KRL (*.krl)|*.krl"; destination.DefaultExt = ".krl";
            destination.Title = "Сохранить проверенную адаптацию в проект";
            if (destination.ShowDialog(this) != true) return;
            await ProjectImport.WriteScriptAsync(project.Path, destination.FileName, review.EditedCode, lifetime.Token);
            await OpenProjectAsync(project.Path); await OpenScriptAsync(destination.FileName);
        }, "Подготовка адаптации…");
    }

    public async Task<string?> AnalyzeCurrentProjectAsync()
    {
        if (currentProject == null) return null;
        await SceneEditor.SaveAsync();
        var result = await ProjectAnalyzer.AnalyzeAsync(currentProject.Path, lifetime.Token);
        string report = await ProjectAnalyzer.SaveReportAsync(currentProject.Path, result, lifetime.Token);
        presentAnalysisReport(report);
        OperationStatus.Text = $"Анализ завершён: ошибок {result.ErrorCount}, предупреждений {result.WarningCount}. Отчёт: {report}";
        return report;
    }
    private async void BtnAnalyzeProject_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async () => { await AnalyzeCurrentProjectAsync(); }, "Анализ проекта без запуска…");
}
