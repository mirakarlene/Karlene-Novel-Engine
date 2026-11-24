using System.Windows;
using System.Windows.Data;

namespace KNE;

public partial class MainWindow
{
    private void AddTranslationsButton()
    {
        var button = AddPluginButton("Диалоги / переводы", ShowProjectTranslations, 10);
        button.Margin = new Thickness(8, 0, 8, 0);
        button.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = AppearanceButton });
    }

    private void ShowProjectTranslations()
    {
        if (currentProject is not { } project) return;
        var drafts = OpenEditors.Where(editor => editor.Document != null).ToDictionary(editor => editor.Document!.FilePath,
            editor => editor.CaptureNavigationSource(), StringComparer.OrdinalIgnoreCase);
        new ProjectTranslationsWindow(this, settings.Theme, project, drafts, lifetime.Token).ShowDialog();
    }
}
