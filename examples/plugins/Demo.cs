using KNE;
using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

public sealed class DemoPlugin : IKnePlugin
{
    public void Execute(IPluginContext context)
    {
        var api = context.Api;
        api.RegisterGroup(new("demo", "Пример SDK", "#9370DB", "⚔"));
        api.RegisterStructure<Quest>("demo.quest");
        var definition = new PluginBlock("demo.quest", "Создать квест", "demo", "Записать типизированный квест в state.quest", "⚔", "#9370DB",
            Inputs: ["Начать"], Outputs: ["Далее"], Parameters: [new("title", "Название", Default: "Новый квест"), new("reward", "Награда", "slider", "10", 0, 100)]);
        api.RegisterBlock(definition, (execution, values) =>
        {
            var quest = new Quest { Title = values["title"]!.GetValue<string>(), Reward = values["reward"]!.GetValue<double>() };
            execution.SetJson("quest", api.CreateStructure("demo.quest", JsonSerializer.Serialize(quest)));
            execution.Log("Создан квест: " + quest.Title); return 1;
        });
        api.RegisterPalette(new("demo.neon", "SDK · Неон", "#101020", "#24243B", "#F4F0FF", "#D74DFF", "#9B93BF", new() { ["logic"] = "#27C9B8" }));
        api.BeforeExecute("Dialogue", dialogue => dialogue.Text = dialogue.Text.Replace("Welcome!", "Добро пожаловать!", StringComparison.Ordinal));
        api.On("on_save", saved => api.SetSaveData(saved.Context, "demo", new { Version = 1, Quest = saved.Context.GetJson("quest") }));
        api.On("on_load", loaded => loaded.Context.Log("Данные demo восстановлены: " + api.GetSaveData(loaded.Context, "demo").GetRawText()));
        api.RegisterScreen(new("demo.quest", variables => new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(210, 36, 36, 59)), Padding = new Thickness(12), Margin = new Thickness(12),
            Child = new TextBlock { Text = "Экран расширения · Квесты", Foreground = Brushes.White }
        }));
        api.On("on_scene_start", scene => api.ShowScreen(scene.Context, "demo.quest"));
        api.RegisterValidator(graph => graph.Nodes.Where(node => node.Plugin?.BlockId == "demo.quest" && string.IsNullOrWhiteSpace(node.Plugin.Parameters["title"]?.ToString()))
            .Select(node => new PluginDiagnostic("Укажите название квеста.", node.Id, IsError: true)));
        if (!api.IsEditor) return;
        api.RegisterTab(new("demo.inventory", "Инвентарь", () => new TextBlock { Text = "Здесь может находиться WPF-редактор вашего расширения.", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap }));
        api.RegisterCommand(new("demo.generate", "Добавить квест", editor =>
        {
            var graph = editor.GetGraph() ?? throw new InvalidOperationException("Откройте блок-схему.");
            var start = graph.Nodes.Single(n => n.Kind == ScriptNodeKind.Start);
            var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, definition.Group, definition.Title, definition.Description, definition.Color, definition));
            node.X = start.X + 260; node.Y = start.Y; node.Options[0].Target = start.Next; start.Next = node.Id;
            graph.Nodes.Add(node); editor.ReplaceGraph(graph);
        }));
        api.RegisterCommand(new("demo.palette", "SDK: палитра Неон", _ => EditorTheme.Apply((Window)api.Engine!, "demo.neon"), "context"));
    }
    public sealed class Quest
    {
        public string Title { get; set; } = "";
        public string Status { get; set; } = "active";
        public double Reward { get; set; }
    }
}
