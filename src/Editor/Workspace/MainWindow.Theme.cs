using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Windows.Media;

namespace KNE;

public partial class MainWindow
{
    private void UpdateDesignPreferences()
    {
        BlockAccessibility.Apply(this, settings.AccessibilityMode);
        WelcomeObjectInput.SelectedValue = settings.WelcomeObjects;
        WelcomeBubbleField.ObjectKind = settings.WelcomeObjects;
    }
    private void Accessibility_Checked(object sender, RoutedEventArgs e)
    { if (sender is RadioButton { Tag: string mode }) BlockAccessibility.Apply(this, mode); }
    private void WelcomeObject_Changed(object sender, SelectionChangedEventArgs e)
    { if (WelcomeBubbleField != null && WelcomeObjectInput?.SelectedValue is string value) WelcomeBubbleField.ObjectKind = value; }
    private void UpdateWelcomeGreeting() => WelcomeGreeting.Text = string.IsNullOrWhiteSpace(settings.UserName) ? "Добро пожаловать в KNE" : "Добро пожаловать, " + settings.UserName.Trim();
    private void WelcomePointer_Move(object sender, MouseEventArgs e) => WelcomeBubbleField.SetPointer(e.GetPosition(WelcomeBubbleField));
    private void WelcomePointer_Leave(object sender, MouseEventArgs e) => WelcomeBubbleField.SetPointer(null);
    private void ThemeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeInput?.SelectedValue is string theme) EditorTheme.Apply(this, theme);
    }
}
