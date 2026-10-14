using System.Windows;

namespace KNE;

/// <summary>Shows source examples side by side before the project's initial main function is created.</summary>
public partial class ProjectCreationDialog : Window
{
    public ProjectAuthoringMode AuthoringMode => VisualModeCard.IsChecked == true ? ProjectAuthoringMode.Visual : ProjectAuthoringMode.Code;

    public ProjectCreationDialog() => InitializeComponent();

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
