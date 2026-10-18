using System.Windows;
using System.Windows.Controls;

namespace KNE;

internal enum ProjectCompatibilityAction { Cancel, Migrate, NewProject }

/// <summary>Offers explicit migration with a backup or an independent new project.</summary>
internal sealed class ProjectCompatibilityWindow : Window
{
    internal ProjectCompatibilityAction Action { get; private set; }
    internal ProjectCompatibilityWindow(IReadOnlyList<ProjectCompatibilityIssue> issues)
    {
        Title = "Ой-ой, похоже, ваш проект работает на старой версии KNE";
        Width = 720; Height = 460; MinWidth = 580; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 21, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        DockPanel.SetDock(panel.Children[^1], Dock.Top);
        var help = new TextBlock { Text = "Обнаружено изменение формата проекта. Миграция поддерживаемых данных сохранит резервную копию в .kne-backups. Неизвестные поля могут принадлежать более новой версии KNE — для них нужна совместимая версия движка. Создание нового проекта оставляет исходный проект на месте.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(help, Dock.Top); panel.Children.Add(help);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        void Add(string text, ProjectCompatibilityAction action, bool enabled = true)
        {
            var button = new Button { Content = text, IsEnabled = enabled, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(5) };
            button.Click += (_, _) => { Action = action; DialogResult = action != ProjectCompatibilityAction.Cancel; };
            buttons.Children.Add(button);
        }
        Add("Миграция данных", ProjectCompatibilityAction.Migrate, issues.All(issue => issue.CanMigrate));
        Add("Создать новый проект", ProjectCompatibilityAction.NewProject);
        Add("Отмена", ProjectCompatibilityAction.Cancel);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = string.Join("\n\n", issues.Select(issue => issue.File + "\n" + issue.Reason)), TextWrapping = TextWrapping.Wrap } });
        Content = panel;
    }
}
