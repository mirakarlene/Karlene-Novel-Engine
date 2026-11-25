using System.Windows;

namespace KNE;

internal partial class TutorialWindow : Window
{
    private readonly string theme;

    internal TutorialWindow(string theme)
    {
        this.theme = theme;
        InitializeComponent();
        EditorTheme.Apply(this, theme);
    }

    private void Placeholder_Click(object sender, RoutedEventArgs e) => EditorPromptWindow.ShowDevelopmentNotice(this, theme);
}
