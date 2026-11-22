using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KNE;

internal sealed class ProjectTemplateDialog : Window
{
    internal bool BackRequested { get; private set; }

    internal ProjectTemplateDialog(string theme)
    {
        Title = "Шаблоны";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var body = new StackPanel { Margin = new Thickness(16) };
        var tiles = new UniformGrid { Columns = 4, Rows = 2, Margin = new Thickness(0, 0, 0, 6) };
        var empty = new Button
        {
            Content = "Пустой шаблон", Width = 148, Height = 82, Margin = new Thickness(3), IsDefault = true
        };
        empty.Click += (_, _) => DialogResult = true;
        tiles.Children.Add(empty);
        for (int slot = 1; slot < 8; slot++)
        {
            var placeholder = new Button
            {
                Content = new TextBlock { Text = "…", FontSize = 26 },
                Width = 148, Height = 82, Margin = new Thickness(3)
            };
            placeholder.Click += (_, _) => EditorPromptWindow.ShowDevelopmentNotice(this, theme);
            tiles.Children.Add(placeholder);
        }
        body.Children.Add(tiles);

        var actions = new DockPanel();
        var back = new Button { Content = "Назад", Padding = new Thickness(12, 4, 12, 4) };
        back.Click += (_, _) => { BackRequested = true; DialogResult = false; };
        DockPanel.SetDock(back, Dock.Left);
        actions.Children.Add(back);
        var cancel = new Button
        {
            Content = "Отмена", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 4, 12, 4)
        };
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        body.Children.Add(actions);

        Content = body;
        EditorTheme.Apply(this, theme);
    }
}
