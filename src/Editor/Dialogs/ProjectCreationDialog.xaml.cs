using System.Windows;

namespace KNE;

/// <summary>Authoring mode selection for a new project.</summary>
public partial class ProjectCreationDialog : Window
{
    public ProjectAuthoringMode AuthoringMode => VisualModeCard.IsChecked == true ? ProjectAuthoringMode.Visual : ProjectAuthoringMode.Code;

    public ProjectCreationDialog() => InitializeComponent();

    internal ProjectCreationDialog(ProjectAuthoringMode mode) : this()
    {
        if (mode == ProjectAuthoringMode.Visual) VisualModeCard.IsChecked = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
