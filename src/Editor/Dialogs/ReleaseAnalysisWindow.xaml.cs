using System.Windows;
using System.Windows.Media;

namespace KNE;

internal partial class ReleaseAnalysisWindow : Window
{
    private sealed record Row(string Title, string Location, string Message, Brush Color);

    internal ReleaseAnalysisWindow(Window owner, string theme, ProjectAnalysisResult analysis)
    {
        InitializeComponent();
        Owner = owner;
        EditorTheme.Apply(this, theme);
        Summary.Text = $"Ошибок: {analysis.ErrorCount}, предупреждений: {analysis.WarningCount}, сообщений: {analysis.InformationCount}";
        ContinueButton.IsEnabled = analysis.ErrorCount == 0;
        BuildStatus.Text = ContinueButton.IsEnabled
            ? "Исправьте предупреждения или продолжите сборку."
            : "Исправьте ошибки проекта, чтобы собрать релиз.";
        var surface = ((SolidColorBrush)FindResource("Editor.Surface")).Color;
        bool light = .2126 * surface.R + .7152 * surface.G + .0722 * surface.B > 140;
        Diagnostics.ItemsSource = analysis.Findings.OrderBy(finding => finding.Severity switch
        {
            ProjectDiagnosticSeverity.Error => 0,
            ProjectDiagnosticSeverity.Warning => 1,
            _ => 2
        }).Select(finding => ToRow(finding, light)).ToArray();
    }

    private static Row ToRow(ProjectDiagnostic finding, bool light)
    {
        var (label, color) = finding.Severity switch
        {
            ProjectDiagnosticSeverity.Error => ("Ошибка", light ? Color.FromRgb(170, 39, 53) : Color.FromRgb(237, 107, 120)),
            ProjectDiagnosticSeverity.Warning => ("Предупреждение", light ? Color.FromRgb(128, 86, 0) : Color.FromRgb(240, 196, 89)),
            _ => ("Сообщение", light ? Color.FromRgb(26, 86, 166) : Color.FromRgb(107, 174, 245))
        };
        string location = finding.File;
        if (finding.Line is int line) location += $" · строка {line}";
        if (finding.BlockId is { } id) location += $" · блок {id}";
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new(label + " · " + finding.Code, location, finding.Message, brush);
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (ContinueButton.IsEnabled) DialogResult = true;
    }
}
