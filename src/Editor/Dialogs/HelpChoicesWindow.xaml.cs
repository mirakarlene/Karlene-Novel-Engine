using System.Windows;

namespace KNE;

internal partial class HelpChoicesWindow : Window
{
    internal bool ShowTutorials { get; private set; }

    internal HelpChoicesWindow(Window owner, string theme)
    {
        InitializeComponent();
        if (new System.Windows.Interop.WindowInteropHelper(owner).Handle != 0) Owner = owner;
        EditorTheme.Apply(this, theme);
    }

    private void Guide_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Tutorials_Click(object sender, RoutedEventArgs e)
    {
        ShowTutorials = true;
        DialogResult = true;
    }
}
