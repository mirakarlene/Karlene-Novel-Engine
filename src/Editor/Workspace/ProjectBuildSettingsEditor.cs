using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace KNE;

internal sealed class ProjectBuildSettingsEditor : StackPanel
{
    internal ComboBox TestMode { get; } = GraphicsSettingsEditor.Choice(ProjectBuildSettings.TestModes);
    internal ComboBox ReleaseMode { get; } = GraphicsSettingsEditor.Choice(ProjectBuildSettings.ReleaseModes);
    internal TextBox TestPath { get; } = new();
    internal TextBox ReleasePath { get; } = new();
    internal TextBox Author { get; } = new() { MaxLength = 128 };
    internal TextBox Team { get; } = new() { MaxLength = 128 };
    internal TextBox Version { get; } = new() { MaxLength = 24 };
    internal ComboBox Saves { get; } = GraphicsSettingsEditor.Choice([new GraphicsOption<string>("appdata", "AppData (для текущего пользователя)"), new("local", "Папка игры (saves)")]);
    internal Button BuildButton { get; } = new() { Content = "Собрать релиз", ToolTip = "Сохранить сценарии и параметры проекта, затем собрать релиз.", Padding = new(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock testHint = Hint(""), releaseHint = Hint("");
    private string gameId = "";
    internal ProjectBuildSettings Value => new() { GameId = gameId, TestMode = (string?)TestMode.SelectedValue ?? "vm", TestPath = TestPath.Text.Trim(), ReleaseMode = (string?)ReleaseMode.SelectedValue ?? "self_contained", ReleasePath = ReleasePath.Text.Trim(), Author = Author.Text.Trim(), Team = Team.Text.Trim(), Version = Version.Text.Trim(), SaveLocation = (string?)Saves.SelectedValue ?? "appdata" };

    internal ProjectBuildSettingsEditor(ProjectInfo project, string? storagePath)
    {
        var value = ProjectBuildSettingsStore.Load(project.Path); gameId = value.GameId;
        string defaultPath = ProjectBuildSettings.DefaultOutput(project.Path, storagePath);
        TestMode.SelectedValue = value.TestMode; ReleaseMode.SelectedValue = value.ReleaseMode;
        TestPath.Text = value.TestPath == "" ? System.IO.Path.Combine(defaultPath, "test") : value.TestPath;
        ReleasePath.Text = value.ReleasePath == "" ? defaultPath : value.ReleasePath;
        Author.Text = value.Author; Team.Text = value.Team; Version.Text = value.Version; Saves.SelectedValue = value.SaveLocation;
        var test = Section("Тестовый билд"); Field(test, "Режим запуска", TestMode); test.Children.Add(testHint); Field(test, "Папка тестового билда", PathRow(TestPath)); Children.Add(Card(test));
        var release = Section("Релизный билд · Windows x64"); Field(release, "Формат", ReleaseMode); release.Children.Add(releaseHint); Field(release, "Папка выходных файлов", PathRow(ReleasePath)); Field(release, "Сохранения и настройки игрока", Saves); Children.Add(Card(release));
        var metadata = Section("Метаданные EXE");
        var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); row.ColumnDefinitions.Add(new());
        var left = new StackPanel(); Field(left, "Автор / никнейм", Author); Field(left, "Версия игры", Version);
        var right = new StackPanel(); Field(right, "Команда разработчиков (необязательно)", Team); right.Children.Add(Hint("Эти поля записываются в свойства EXE. Цифровая подпись сертификатом не создаётся."));
        row.Children.Add(left); Grid.SetColumn(right, 2); row.Children.Add(right); metadata.Children.Add(row); Children.Add(Card(metadata));
        Children.Add(Hint("Для сборки нужен .NET 10 SDK и доступ к пакетам NuGet. Тестовый EXE использует выбранный релизный профиль; MSIX тестируется как EXE до упаковки. Сценарии и ресурсы входят в билд, сохранения разработчика — нет."));
        Children.Add(BuildButton);
        TestMode.SelectionChanged += (_, _) => Refresh(); ReleaseMode.SelectionChanged += (_, _) => Refresh(); Refresh();
    }
    internal void Saved(ProjectBuildSettings settings) => gameId = settings.GameId;
    private void Refresh()
    {
        TestPath.IsEnabled = (string?)TestMode.SelectedValue != "vm";
        testHint.Text = (string?)TestMode.SelectedValue switch { "standalone" => "Компиляция и запуск отдельного EXE тем же профилем, что и релиз.", "remote_debug" => "Standalone + JSON-соединение с вкладкой отладки: переменные, пауза, шаги и точки останова.", _ => "Быстрый запуск через Lua worker, обновление ресурсов и GPU через Veldrid-прокси. Режим по умолчанию." };
        string? mode = (string?)ReleaseMode.SelectedValue;
        releaseHint.Text = mode switch { "framework_dependent" => "ZIP с EXE и зависимостями; игроку нужен .NET 10 Desktop Runtime x64.", "single_file" => "Один сжатый EXE с .NET. WPF не поддерживает trimming: код рантайма не обрезается. При запуске ресурсы распаковываются в кэш.", "msix" => "Пакет без цифровой подписи для последующего подписания / Microsoft Store. Нужен Windows SDK (MakeAppx). Сохранения — AppData.", "portable" => "ZIP с полным рантаймом. Данные KNE остаются в папке игры, AppData и реестр не используются.", _ => "ZIP с EXE, зависимостями и .NET Desktop Runtime; игроку не нужна установка .NET." };
        Saves.IsEnabled = mode is not ("portable" or "msix");
        if (mode == "portable") Saves.SelectedValue = "local";
        if (mode == "msix") Saves.SelectedValue = "appdata";
    }
    private static TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = .65, FontSize = 11, Margin = new(0, 4, 0, 7) };
    private static StackPanel Section(string heading) { var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = heading, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) }); return panel; }
    private static void Field(Panel panel, string label, FrameworkElement input) { panel.Children.Add(new TextBlock { Text = label, FontSize = 12 }); input.Margin = new(0, 3, 0, 5); panel.Children.Add(input); }
    private static Border Card(UIElement child) { var card = new Border { Child = child, Padding = new(12), CornerRadius = new(8), BorderThickness = new(1), Margin = new(0, 0, 0, 10) }; card.SetResourceReference(BackgroundProperty, "Editor.Surface"); card.SetResourceReference(Border.BorderBrushProperty, "Editor.Border"); return card; }
    private DockPanel PathRow(TextBox input)
    {
        var panel = new DockPanel(); var browse = new Button { Content = "Выбрать…", Padding = new(10, 5, 10, 5), Margin = new(8, 0, 0, 0) }; DockPanel.SetDock(browse, Dock.Right); panel.Children.Add(browse); panel.Children.Add(input);
        browse.Click += (_, _) => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName; }; return panel;
    }
}
