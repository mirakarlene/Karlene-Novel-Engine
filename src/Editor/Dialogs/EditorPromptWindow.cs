using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace KNE;

/// <summary>A palette-aware prompt for an editor name or a short informational message.</summary>
internal sealed class EditorPromptWindow : Window
{
    private readonly TextBox? input;

    internal string? Value => input?.Text;

    internal EditorPromptWindow(string title, string prompt, string theme, string? initialName = null)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(18) };
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var accept = new Button { Content = "OK", IsDefault = true, MinWidth = 76, Padding = new Thickness(12, 5, 12, 5) };
        actions.Children.Add(accept);
        if (initialName != null)
        {
            input = new TextBox { Text = initialName, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            AutomationProperties.SetName(input, prompt);
            body.Children.Add(input);
            accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            accept.Click += (_, _) => DialogResult = true;
            actions.Children.Add(new Button
            {
                Content = "Отмена", IsCancel = true, MinWidth = 76,
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0)
            });
            Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        }
        else
        {
            accept.IsCancel = true;
            accept.Click += (_, _) => Close();
            Loaded += (_, _) => accept.Focus();
        }

        body.Children.Add(actions);
        Content = body;
        EditorTheme.Apply(this, theme);
    }
}
