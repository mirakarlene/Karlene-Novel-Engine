using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    private void AddShaderRemovalFields(ScriptNode node)
    {
        if (node.Kind == ScriptNodeKind.RemoveLayerShaders)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = "Слой шейдера" });
            var target = GraphicsSettingsEditor.Choice(new GraphicsOption<string>[] { new("scene", "Вся сцена"), new("background", "Фон"), new("sprite", "Спрайт"), new("ui", "Игровой UI"), new("screen", "Экран по названию") });
            target.Name = "ShaderRemovalTargetInput"; target.SelectedValue = node.Target;
            target.SelectionChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(target);
            extendedEdits.Add(changed => changed.Target = (string?)target.SelectedValue ?? "background");
            ExtendedProperties.Children.Add(new TextBlock { Text = "Название экрана (для цели «Экран»)" });
            var screen = new TextBox { Name = "ShaderRemovalScreenInput", Text = node.Path, Margin = new(0, 3, 0, 8) };
            screen.TextChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(screen);
            extendedEdits.Add(changed => changed.Path = screen.Text.Trim());
        }
        var suppress = new CheckBox { Name = "SuppressShadersInput", IsChecked = node.SuppressShadersUntilContainer,
            Content = new TextBlock { Text = "Не отображать шейдеры после этого блока, пока поток не дойдёт до следующего контейнера GLSL/HLSL", TextWrapping = TextWrapping.Wrap }, Margin = new(0, 8, 0, 8) };
        suppress.Click += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(suppress);
        extendedEdits.Add(changed => changed.SuppressShadersUntilContainer = suppress.IsChecked == true);
    }
    internal bool ConnectShaderPorts(string first, int firstPort, string second, int secondPort)
    {
        if (Document?.Graph is not { IsShader: true } graph || first == second || (firstPort >= 0) == (secondPort >= 0)) return false;
        string source = firstPort >= 0 ? first : second, target = firstPort < 0 ? first : second;
        int output = firstPort >= 0 ? firstPort : secondPort, input = -(firstPort < 0 ? firstPort : secondPort) - 1;
        var from = graph.Nodes.FirstOrDefault(n => n.Id == source); var to = graph.Nodes.FirstOrDefault(n => n.Id == target);
        if (from == null || to == null || !ShaderLibrary.IsData(from) || !ShaderLibrary.IsData(to) || output >= ShaderLibrary.Get(from.Kind).Outputs.Length || input >= ShaderLibrary.Get(to.Kind).Inputs.Length) return false;
        var visited = new HashSet<string>();
        bool DependsOn(string id)
        {
            if (id == target) return true;
            return visited.Add(id) && graph.Nodes.Single(n => n.Id == id).Shader!.Inputs.Values.Any(link => DependsOn(link.Node));
        }
        if (DependsOn(source)) { EditorStatus.Text = "Связь образует цикл значений."; return false; }
        ApplyProperties(repaint: false); graph = Document.Graph!;
        Remember(); graph.Nodes.Single(n => n.Id == target).Shader!.Inputs[ShaderLibrary.Get(to.Kind).Inputs[input]] = new(source, output);
        pendingConnection = null; Repaint(); EditorStatus.Text = "Вход значения подключён."; return true;
    }
    private void DisconnectShaderPort(GraphPort port)
    {
        ApplyProperties(repaint: false);
        if (Document?.Graph is not { IsShader: true } graph || !ShaderLibrary.IsData(graph.Nodes.Single(n => n.Id == port.NodeId))) return;
        Remember();
        if (port.IsOutput)
        {
            foreach (var node in graph.Nodes.Where(ShaderLibrary.IsData))
                foreach (string input in node.Shader!.Inputs.Where(pair => pair.Value.Node == port.NodeId && pair.Value.Output == port.OutputIndex).Select(pair => pair.Key).ToArray())
                    node.Shader.Inputs.Remove(input);
        }
        else
        {
            var node = graph.Nodes.Single(n => n.Id == port.NodeId);
            node.Shader!.Inputs.Remove(ShaderLibrary.Get(node.Kind).Inputs[-port.OutputIndex!.Value - 1]);
        }
        pendingConnection = null; Repaint();
    }
    private void AddShaderFields(ScriptNode node)
    {
        var settings = node.Shader!;
        void Text(string name, string label, string value, Action<ScriptNode, string> assign)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Name = name, Text = value, Margin = new(0, 3, 0, 8) }; ExtendedProperties.Children.Add(input);
            input.TextChanged += (_, _) => TrackProperties(); extendedEdits.Add(changed => assign(changed, input.Text.Trim()));
        }
        if (node.Kind == ScriptNodeKind.ShaderContainer)
        {
            Text("ShaderPathInput", "Файл .kneshader от корня проекта", node.Path, (changed, value) => changed.Path = value);
            var open = new Button { Name = "OpenShaderContainerButton", Content = "Открыть / создать контейнер…", Margin = new(0, 0, 0, 8) };
            open.Click += (_, _) => OpenSubsystem(node.Id); ExtendedProperties.Children.Add(open);
            if (node.IsShaderInvocation)
            {
                var enabled = new CheckBox { Content = "Включить шейдер", IsChecked = settings.Enabled, Margin = new(0, 0, 0, 8) };
                enabled.Click += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(enabled);
                extendedEdits.Add(changed => changed.Shader!.Enabled = enabled.IsChecked == true);
                ExtendedProperties.Children.Add(new TextBlock { Text = "Снимите галочку, чтобы отключить эффект в этой точке сценария.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = .65 });
                return;
            }
        }
        if (node.Kind == ScriptNodeKind.ShaderTexture)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = "Цель шейдера" });
            var target = GraphicsSettingsEditor.Choice(new GraphicsOption<string>[] { new("scene", "Вся сцена"), new("background", "Фон"), new("sprite", "Спрайт"), new("ui", "Игровой UI"), new("screen", "Экран по названию") });
            target.SelectedValue = settings.Target; target.SelectionChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(target);
            extendedEdits.Add(changed => changed.Shader!.Target = (string?)target.SelectedValue ?? "scene");
            Text("ShaderScreenInput", "Название экрана (для цели «Экран»)", settings.Screen, (changed, value) => changed.Shader!.Screen = value);
        }
        if (node.Kind is ScriptNodeKind.ShaderFloat or ScriptNodeKind.ShaderVector or ScriptNodeKind.ShaderColor)
            Text("ShaderNameInput", "Имя внешнего параметра", settings.Name, (changed, value) => changed.Shader!.Name = value);
        foreach (var parameter in ShaderLibrary.Parameters(node.Kind))
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = parameter.Label, TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Name = "Shader_" + parameter.Key, Text = ShaderLibrary.Value(node, parameter.Key).ToString("R", CultureInfo.InvariantCulture), Margin = new(0, 3, 0, 8) };
            ExtendedProperties.Children.Add(input); input.TextChanged += (_, _) => TrackProperties();
            extendedEdits.Add(changed =>
            {
                if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) throw new InvalidDataException(parameter.Label + ": укажите число с точкой.");
                changed.Shader!.Values[parameter.Key] = value;
            });
        }
        ExtendedProperties.Children.Add(new TextBlock { Text = "Потяните выход значения к нужному входу. Один выход может питать несколько блоков. Неподключённые входы используют значения по умолчанию.", TextWrapping = TextWrapping.Wrap, Opacity = .65, Margin = new(0, 5, 0, 8) });
    }
}
