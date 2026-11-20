using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KNE;

public partial class ScriptEditor
{
    internal event EventHandler? LuaFileConverted;
    internal bool SelectedRawLua => Selected?.Lua?.Operation == "raw";

    private async Task ConvertLuaModeAsync()
    {
        if (saving || Document is not { } document) return;
        await GuardAsync(async () =>
        {
            saving = true; IsEnabled = false;
            try
            {
                ApplyProperties();
                string target = await document.ConvertLuaAsync();
                await OpenAsync(projectRoot, target);
                LuaFileConverted?.Invoke(this, EventArgs.Empty);
                EditorStatus.Text = "Файл преобразован в " + Path.GetExtension(target) + ".";
            }
            finally
            {
                updating = true;
                GraphMode.IsChecked = Document?.IsVisual == true;
                CodeMode.IsChecked = Document?.IsVisual == false;
                updating = false;
                IsEnabled = true; saving = false;
                ShowMode();
            }
        });
    }

    private void AddLuaBlockFields(ScriptNode node)
    {
        var part = node.Lua!;
        var pane = new StackPanel();
        if (LuaParameter(part) is { } parameter)
            Field(parameter.Label, part.Code[parameter.Start..parameter.End], (settings, value) =>
                settings with { Code = part.Code[..parameter.Start] + value + part.Code[parameter.End..] });
        else if (part.Operation != "continue")
            Field(part.Operation == "raw" ? "Исходный код" : "Команда Lua 5.4", part.Code, (settings, value) => settings with { Code = value });
        if (VisualLuaCatalog.Blocks[part.Operation].HasBody)
            Field("Закрытие тела", part.Close, (settings, value) => settings with { Close = value });
        if (part.Operation == "raw")
        {
            var external = new Button { Content = "Открыть исходник во внешнем редакторе", Margin = new(0, 5, 0, 0) };
            external.Click += (_, _) => ExternalEditorRequested?.Invoke(this, EventArgs.Empty);
            pane.Children.Add(external);
        }
        ExtendedProperties.Children.Add(pane);

        void Field(string label, string initial, Func<LuaBlockSettings, string, LuaBlockSettings> change)
        {
            pane.Children.Add(new TextBlock { Text = label });
            var input = new TextBox { Text = initial, AcceptsReturn = true, AcceptsTab = true, MinHeight = 72, MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new("Consolas"), Margin = new(0, 3, 0, 8) };
            bool edited = false;
            input.TextChanged += (_, _) => { edited = true; TrackProperties(); };
            extendedEdits.Add(value => { if (edited) value.Lua = change(value.Lua!, input.Text); });
            pane.Children.Add(input);
        }
    }

    private static (int Start, int End, string Label)? LuaParameter(LuaBlockSettings part)
    {
        var tokens = LuaSyntax.Parse(part.Code).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        if (part.Operation is "if" or "while" && tokens.Length >= 3)
            return (tokens[1].Start, tokens[^2].End, "Условие");
        if (part.Operation is "number" or "string" or "boolean" or "nil" && tokens.Length >= 3 &&
            part.Code[tokens[^2].Start..tokens[^2].End] == "=")
            return (tokens[^1].Start, tokens[^1].End, "Значение");
        if (VisualLuaCatalog.Blocks[part.Operation].Symbol is not { } symbol) return null;
        string[] name = symbol.Split('.');
        for (int i = 0; i < tokens.Length; i++)
        {
            if (part.Code[tokens[i].Start..tokens[i].End] != name[0]) continue;
            int opening = i + 1;
            if (name.Length == 2)
            {
                if (i + 2 >= tokens.Length || part.Code[tokens[i + 1].Start..tokens[i + 1].End] != "." ||
                    part.Code[tokens[i + 2].Start..tokens[i + 2].End] != name[1]) continue;
                opening += 2;
            }
            if (opening < tokens.Length && part.Code[tokens[opening].Start] == '(' && tokens[opening].Partner is int end)
                return (tokens[opening].End, end, "Аргументы");
        }
        return null;
    }

    internal async Task EditRawLuaAsync(string editorPath)
    {
        ApplyProperties();
        if (Document?.Graph is not { } graph || Selected is not { Lua.Operation: "raw" } node) return;
        string staging = Path.Combine(Path.GetTempPath(), "kne-raw-lua-" + Guid.NewGuid().ToString("N") + ".lua");
        try
        {
            await File.WriteAllTextAsync(staging, node.Lua!.Code);
            using (Process.Start(EditorLauncher.CreateStartInfo(editorPath, staging))) { }
            if (MessageBox.Show(Window.GetWindow(this), "Сохраните изменения во внешнем редакторе и нажмите «ОК», чтобы обновить блок.",
                "Исходный код Lua", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (new FileInfo(staging).Length > 8 * 1024 * 1024) throw new InvalidDataException("Исходник больше 8 МБ.");
            string text = await File.ReadAllTextAsync(staging);
            if (text == node.Lua.Code) return;
            var candidate = VisualScript.Parse(graph.Serialize());
            candidate.Nodes.Single(value => value.Id == node.Id).Lua = node.Lua with { Code = text };
            _ = candidate.Serialize();
            Remember(); Document.Graph = candidate; Repaint();
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException error) { EngineDiagnostics.Trace("Временный файл Lua", error.Message); }
        }
    }

    private void ApplyLuaModeAppearance(bool luaMode)
    {
        if (luaMode)
        {
            CodeMode.Style = GraphMode.Style = (Style)Resources["LuaModeSegment"];
            CodeMode.Margin = new Thickness(0);
            ModeTrack.CornerRadius = new CornerRadius(12);
            ModeTrack.Padding = new Thickness(2);
            ModeTrack.BorderThickness = new Thickness(1);
            ModeTrack.SetResourceReference(Border.BorderBrushProperty, "Editor.Border");
            ModeTrack.SetResourceReference(Border.BackgroundProperty, "Editor.Background");
        }
        else
        {
            CodeMode.ClearValue(StyleProperty);
            GraphMode.ClearValue(StyleProperty);
            CodeMode.Margin = new Thickness(8, 0, 8, 0);
            ModeTrack.ClearValue(Border.CornerRadiusProperty);
            ModeTrack.ClearValue(Border.PaddingProperty);
            ModeTrack.ClearValue(Border.BorderThicknessProperty);
            ModeTrack.ClearValue(Border.BorderBrushProperty);
            ModeTrack.ClearValue(Border.BackgroundProperty);
        }
    }
}
