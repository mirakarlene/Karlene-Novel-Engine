using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    /// <summary>Commits pending property edits and returns a detached snapshot for plugin generators.</summary>
    public VisualScript? GetPluginGraph()
    {
        ApplyProperties();
        return Document?.Graph is { } graph ? VisualScript.Parse(graph.Serialize()) : null;
    }
    /// <summary>Commits a validated plugin edit as one undo operation.</summary>
    public void ReplacePluginGraph(VisualScript graph)
    {
        if (Document?.Graph == null) throw new InvalidOperationException("Откройте блок-схему.");
        var copy = VisualScript.Parse(graph.Serialize());
        if (copy.IsShader != Document.Graph.IsShader) throw new InvalidOperationException("Нельзя менять тип документа генератором.");
        ApplyProperties(); Remember(); Document.Graph = copy; selectedIds.Clear(); selectedId = null; Repaint(); UpdateTitle();
    }
    internal void ApplyPluginTexture()
    {
        if (PluginCatalog.Current?.WorkspaceImage is { } image)
            GraphCanvas.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, Opacity = .4 };
        else GraphCanvas.SetResourceReference(Panel.BackgroundProperty, "Editor.Canvas");
    }
    private void AddPluginFields(ScriptNode node)
    {
        var definition = PluginCatalog.Current?.Blocks.FirstOrDefault(b => b.Id == node.Plugin?.BlockId);
        if (definition == null)
        {
            ExtendedProperties.Children.Add(new TextBlock { Text = "Расширение недоступно: " + node.Plugin?.BlockId + ". Параметры и связи сохранены.", TextWrapping = TextWrapping.Wrap }); return;
        }
        foreach (var parameter in definition.Parameters ?? [])
        {
            string current = node.Plugin!.Parameters[parameter.Id]?.ToString() ?? parameter.Default;
            ExtendedProperties.Children.Add(new TextBlock { Text = parameter.Label, TextWrapping = TextWrapping.Wrap });
            if (parameter.Kind == "slider")
            {
                var slider = new Slider { Minimum = parameter.Minimum, Maximum = parameter.Maximum,
                    Value = double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : parameter.Minimum, Margin = new Thickness(0, 6, 0, 10), ToolTip = current };
                slider.ValueChanged += (_, _) => { slider.ToolTip = slider.Value.ToString("0.##", CultureInfo.InvariantCulture); TrackProperties(); };
                ExtendedProperties.Children.Add(slider); extendedEdits.Add(n => n.Plugin!.Parameters[parameter.Id] = JsonValue.Create(slider.Value));
            }
            else if (parameter.Kind == "choice")
            {
                var combo = new ComboBox { ItemsSource = parameter.Choices, SelectedItem = current, Margin = new Thickness(0, 3, 0, 8) };
                combo.SelectionChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(combo);
                extendedEdits.Add(n => n.Plugin!.Parameters[parameter.Id] = JsonValue.Create((string?)combo.SelectedItem ?? parameter.Default));
            }
            else if (parameter.Kind == "boolean")
            {
                var check = new CheckBox { IsChecked = bool.TryParse(current, out bool enabled) && enabled, Margin = new Thickness(0, 3, 0, 8) };
                check.Checked += (_, _) => TrackProperties(); check.Unchecked += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(check);
                extendedEdits.Add(n => n.Plugin!.Parameters[parameter.Id] = JsonValue.Create(check.IsChecked == true));
            }
            else
            {
                var text = new TextBox { Text = current, Margin = new Thickness(0, 3, 0, 8) }; text.TextChanged += (_, _) => TrackProperties(); ExtendedProperties.Children.Add(text);
                extendedEdits.Add(n => { if (text.Text.Length > 16000) throw new InvalidDataException("Параметр плагина длиннее 16000 символов."); n.Plugin!.Parameters[parameter.Id] = JsonValue.Create(text.Text); });
            }
        }
    }
}
