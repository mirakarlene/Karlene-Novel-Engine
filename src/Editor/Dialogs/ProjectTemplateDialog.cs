using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.IO;

namespace KNE;

internal sealed class ProjectTemplateDialog : Window
{
    internal bool BackRequested { get; private set; }

    internal ProjectTemplateDialog(string theme, ProjectAuthoringMode mode)
    {
        Title = "Шаблоны";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var body = new StackPanel { Margin = new Thickness(16) };
        var tiles = new UniformGrid { Columns = 4, Rows = 2, Margin = new Thickness(0, 0, 0, 6) };
        var emptyTile = new StackPanel();
        var icon = ProjectFileFormats.BlankFileIcon();
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        emptyTile.Children.Add(icon);
        emptyTile.Children.Add(new TextBlock { Text = "Пустой шаблон", TextAlignment = TextAlignment.Center });
        var empty = new Button
        {
            Content = emptyTile, Width = 148, Height = 82, Margin = new Thickness(3)
        };
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
        var sceneFormat = mode == ProjectAuthoringMode.Visual ? ProjectFileFormats.Graph : ProjectFileFormats.Krl;
        string scene = "main" + sceneFormat.Extension;
        var description = new TextBlock
        {
            Text = $"Пустой шаблон создаёт системные папки, project.json, README.txt, стартовую сцену {scene} и скрипт диагностики gamelogic.lua.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 608, Margin = new Thickness(3, 2, 3, 10)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "Editor.Muted");
        body.Children.Add(description);

        var contents = new UniformGrid { Columns = 5, Rows = 2, Height = 108 };
        var preview = new Border { Child = contents, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(5), Margin = new Thickness(3, 0, 3, 10) };
        preview.SetResourceReference(Border.BackgroundProperty, "Editor.Background");
        preview.SetResourceReference(Border.BorderBrushProperty, "Editor.Border");
        body.Children.Add(preview);

        var actions = new DockPanel();
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Left);
        actions.Children.Add(cancel);
        var back = new Button { Content = "Назад", Padding = new Thickness(12, 4, 12, 4) };
        back.Click += (_, _) => { BackRequested = true; DialogResult = false; };
        DockPanel.SetDock(back, Dock.Right);
        actions.Children.Add(back);
        var confirm = new Button
        {
            Content = "Подтвердить", IsDefault = true, IsEnabled = false, Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        confirm.Click += (_, _) => { if (confirm.IsEnabled) DialogResult = true; };
        actions.Children.Add(confirm);
        empty.Click += (_, _) =>
        {
            if (confirm.IsEnabled) return;
            var formats = ProjectFileFormats.CreateGroups.SelectMany(group => group).ToArray();
            var folder = formats.Single(format => format.IsDirectory);
            foreach (string name in new[] { "game", "engine", "plugins", "saves", "metadata" })
                AddPreviewIcon(name, folder);
            AddPreviewIcon("project.json", formats.Single(format => format.Extension == ".json"));
            AddPreviewIcon("README.txt", formats.Single(format => format.Extension == ".txt"));
            AddPreviewIcon(sceneFormat.Folder + "/" + scene, sceneFormat);
            AddPreviewIcon(ProjectDiagnosticsTemplate.CodePath, ProjectFileFormats.Lua);
            empty.SetResourceReference(BorderBrushProperty, "KNE.AccentBrush");
            confirm.IsEnabled = true;
            confirm.Focus();
        };
        body.Children.Add(actions);

        Content = body;
        EditorTheme.Apply(this, theme);

        void AddPreviewIcon(string path, ProjectFileFormat format)
        {
            var item = new StackPanel { ToolTip = path, HorizontalAlignment = HorizontalAlignment.Center };
            var fileIcon = ProjectFileFormats.Icon(format);
            fileIcon.HorizontalAlignment = HorizontalAlignment.Center;
            item.Children.Add(fileIcon);
            item.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextAlignment = TextAlignment.Center });
            contents.Children.Add(item);
        }
    }
}
