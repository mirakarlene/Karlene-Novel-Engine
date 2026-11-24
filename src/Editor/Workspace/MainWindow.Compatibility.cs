using System.Windows;

namespace KNE;

public partial class MainWindow
{
    internal Func<IReadOnlyList<ProjectCompatibilityIssue>, ProjectCompatibilityAction>? ChooseCompatibilityAction { get; set; }
    internal Action? CreateReplacementProject { get; set; }
    private async Task<bool> CheckCompatibilityAsync(ProjectInfo project)
    {
        var issues = await ProjectCompatibility.InspectAsync(project, lifetime.Token);
        if (issues.Count == 0) return true;
        if (SelectCompatibilityAction(issues) != ProjectCompatibilityAction.Migrate || issues.Any(issue => !issue.CanMigrate)) return false;
        if (IsDocumentOpenInProject(project.Path))
            throw new InvalidOperationException("Закройте вкладки этого проекта перед миграцией: их несохранённые правки должны быть сохранены или отклонены.");
        string backup = await ProjectCompatibility.MigrateAsync(project, lifetime.Token);
        OperationStatus.Text = "Миграция завершена. Резервная копия: " + backup;
        return true;
    }

    private ProjectCompatibilityAction SelectCompatibilityAction(IReadOnlyList<ProjectCompatibilityIssue> issues)
    {
        ProjectCompatibilityAction action;
        if (ChooseCompatibilityAction != null) action = ChooseCompatibilityAction(issues);
        else
        {
            var dialog = new ProjectCompatibilityWindow(issues) { Owner = this };
            EditorTheme.Apply(dialog, settings.Theme);
            dialog.ShowDialog();
            action = dialog.Action;
        }
        if (action == ProjectCompatibilityAction.NewProject)
        {
            // The current operation owns the busy gate. Launch creation after it has released the gate.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    if (CreateReplacementProject != null) CreateReplacementProject();
                    else BtnCreateProject_Click(this, new RoutedEventArgs());
                }));
        }
        return action;
    }
    private bool IsDocumentOpenInProject(string path) => currentProject?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true && OpenEditors.Any();
}
