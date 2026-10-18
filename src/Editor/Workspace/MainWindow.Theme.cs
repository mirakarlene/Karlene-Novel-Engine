using System.Windows.Controls;

namespace KNE;

public partial class MainWindow
{
    private void ThemeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeInput?.SelectedValue is string theme) EditorTheme.Apply(this, theme);
    }
}
