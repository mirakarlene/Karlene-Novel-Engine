using System.Windows.Controls;
using System.Windows.Input;

namespace KNE;

public partial class MainWindow
{
    private void UpdateWelcomeGreeting() => WelcomeGreeting.Text = string.IsNullOrWhiteSpace(settings.UserName) ? "Добро пожаловать в KNE" : "Добро пожаловать, " + settings.UserName.Trim();
    private void WelcomePointer_Move(object sender, MouseEventArgs e) => WelcomeBubbleField.SetPointer(e.GetPosition(WelcomeBubbleField));
    private void WelcomePointer_Leave(object sender, MouseEventArgs e) => WelcomeBubbleField.SetPointer(null);
    private void ThemeInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeInput?.SelectedValue is string theme) EditorTheme.Apply(this, theme);
    }
}
