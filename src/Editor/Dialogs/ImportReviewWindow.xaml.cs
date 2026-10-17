using System.Windows;

namespace KNE;

public partial class ImportReviewWindow : Window
{
    public string EditedCode => AdaptedEditor.Text;

    public ImportReviewWindow(ScriptImportResult result, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(result);
        InitializeComponent();
        Title = "Экспериментальный импорт — " + sourceName;
        SourceLabel.Text = "Исходный сценарий — " + sourceName;
        OriginalEditor.Text = result.Original;
        AdaptedEditor.Text = result.Code;
        WarningsText.Text = result.Warnings.Count == 0 ? "Дополнительных замечаний нет. Проверьте адаптацию перед запуском." :
            string.Join(Environment.NewLine, result.Warnings.Select(w => (w.Line > 0 ? "Строка " + w.Line + ": " : "") + w.Message));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EditedCode))
        {
            WarningsText.Text = "Добавьте код адаптации перед сохранением.";
            return;
        }
        DialogResult = true;
    }
}
