using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.Wave;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using Xunit;

namespace KNE.Tests;

public sealed class PluginSdkIntegrationTests
{
    [Theory]
    [InlineData(".json")]
    [InlineData(".docx")]
    public void DialogueInterchange_RoundTripsUnicodeMultilineAndEscapedText_WithStableIdentity(string extension)
    {
        PluginDialogue[] rows = [new("first-1", "Анна & <друг>", "  Привет, «мир»!\nВторая \"строка\" & <tag>\n"),
            new("thought", "", "Мысль\n\nПосле пустой строки"), new("empty", "", "")];
        byte[] exported = PluginDialogues.Export(rows, extension.ToUpperInvariant());
        var imported = PluginDialogues.Import(exported, extension);
        Assert.Equal(rows, imported);
        Assert.Equal(new[] { "first-1", "thought", "empty" }, imported.Select(row => row.Id));
        if (extension == ".docx")
        {
            using var archive = new ZipArchive(new MemoryStream(exported), ZipArchiveMode.Read);
            Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            string xml = reader.ReadToEnd();
            Assert.Contains("&amp;", xml); Assert.Contains("&lt;tag&gt;", xml);
            Assert.DoesNotContain("<tag>", xml);
        }
    }

    [Fact]
    public void DialogueReadAndApply_UpdateOnlyDialogueTextAndSpeaker_AndKeepSourceAndLayoutUnchanged()
    {
        var graph = VisualScript.Create();
        var auto = BlockLibrary.Create(ScriptNodeKind.AutoDialogue); auto.Id = "auto"; auto.Text = "Авто";
        var effect = BlockLibrary.Create(ScriptNodeKind.EffectDialogue); effect.Id = "effect"; effect.Text = "Волна";
        graph.Nodes.AddRange([auto, effect, new() { Id = "title", Kind = ScriptNodeKind.Title, Text = "Не реплика" }]);
        graph.Nodes[1].X = 456; graph.Nodes[1].Width = 340;
        string before = graph.Serialize();
        Assert.Equal(new[] { "dialogue", "auto", "effect" }, PluginDialogues.Read(graph).Select(row => row.Id));
        var translated = PluginDialogues.Apply(graph, [new("auto", "Рассказчик", "Translated\nLine two"), new("dialogue", "Анна", "Новый текст")]);
        Assert.Equal(before, graph.Serialize());
        Assert.Equal("Новый текст", translated.Nodes.Single(node => node.Id == "dialogue").Text);
        Assert.Equal("Анна", translated.Nodes.Single(node => node.Id == "dialogue").Speaker);
        Assert.Equal(456, translated.Nodes.Single(node => node.Id == "dialogue").X);
        Assert.Equal(340, translated.Nodes.Single(node => node.Id == "dialogue").Width);
        Assert.Equal("end", translated.Nodes.Single(node => node.Id == "dialogue").Next);
        Assert.Equal("Translated\nLine two", translated.Nodes.Single(node => node.Id == "auto").Text);
        Assert.Equal("Волна", translated.Nodes.Single(node => node.Id == "effect").Text);
        Assert.Equal("Не реплика", translated.Nodes.Single(node => node.Id == "title").Text);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("notDialogue")]
    [InlineData("nullText")]
    [InlineData("tooLong")]
    public void TranslationErrors_RejectWholeBatchWithoutChangingOriginalGraph(string variation)
    {
        var graph = VisualScript.Create(); string before = graph.Serialize();
        PluginDialogue bad = variation switch
        {
            "duplicate" => new("dialogue", "Другой", "Дублирование"),
            "unknown" => new("missing", "", "Нет такого блока"),
            "notDialogue" => new("start", "", "Точка входа"),
            "nullText" => new("dialogue", "", null!),
            _ => new("dialogue", "", new string('я', 16001))
        };
        Assert.Throws<InvalidDataException>(() => PluginDialogues.Apply(graph, [new("dialogue", "Переводчик", "Первая замена"), bad]));
        Assert.Equal(before, graph.Serialize());
    }

    [Theory]
    [InlineData("missingDocument")]
    [InlineData("wrongCells")]
    [InlineData("dtd")]
    public void DocxImport_RejectsMalformedAndExternalEntityDocuments(string variation)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            string entry = variation == "missingDocument" ? "unrelated.xml" : "word/document.xml";
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
            writer.Write(variation == "dtd" ? "<!DOCTYPE doc [<!ENTITY external SYSTEM 'file:///should-never-read'>]><doc>&external;</doc>" :
                "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:tbl><w:tr/><w:tr><w:tc><w:p><w:r><w:t>one cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>");
        }
        var error = Record.Exception(() => PluginDialogues.Import(memory.ToArray(), ".docx"));
        if (variation == "dtd") Assert.IsType<System.Xml.XmlException>(error);
        else Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public void PdfExport_ProducesReadableMultiPageUnicodeDocument_WithActualDialogueContent()
    {
        var rows = Enumerable.Range(0, 80).Select(index => new PluginDialogue("node-" + index, "Анна", "Диалог " + index + ": привет, мир! Second line\nНовая строка.")).ToArray();
        byte[] data = PluginDialogues.Export(rows, ".pdf");
        using var stream = new MemoryStream(data);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        Assert.True(document.PageCount > 1, "Long dialogue export must create multiple actual PDF pages.");
        Assert.Equal("KNE · Диалоги для перевода", document.Info.Title);
        string text = string.Join("\n", document.Pages.Cast<PdfPage>().Select(ReadPdfText));
        string compact = Regex.Replace(text, "\\s+", "");
        Assert.Contains("[node-0]", compact); Assert.Contains("[node-79]", compact);
        Assert.Contains("Диалог", compact); Assert.Contains("привет,мир!", compact); Assert.Contains("Second", compact);
        Assert.Throws<ArgumentException>(() => PluginDialogues.Import(data, ".pdf"));
    }

    [Fact]
    public void CustomDialogueFormat_DispatchesCaseInsensitivelyAndAppliesTranslationsAtomically()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var graph = VisualScript.Create(); string original = graph.Serialize(); int exports = 0, imports = 0;
        api.RegisterDialogueFormat(new(".quest", rows => { exports++; return Encoding.UTF8.GetBytes("QUEST:" + rows.Single().Id + ":" + rows.Single().Text); },
            bytes => { imports++; Assert.Equal("translated", Encoding.UTF8.GetString(bytes)); return [new("dialogue", "Переводчик", "Новый текст")]; }));
        Assert.Equal("QUEST:dialogue:Добро пожаловать!", Encoding.UTF8.GetString(api.ExportDialogues(graph, ".QUEST")));
        var translated = api.ImportDialogues(graph, Encoding.UTF8.GetBytes("translated"), ".QUEST");
        Assert.Equal(1, exports); Assert.Equal(1, imports);
        Assert.Equal("Новый текст", translated.Nodes[1].Text); Assert.Equal("Переводчик", translated.Nodes[1].Speaker);
        Assert.Equal(original, graph.Serialize());
        Assert.Equal("Добро пожаловать!", Assert.Single(PluginDialogues.Import(api.ExportDialogues(graph, ".json"), ".json")).Text);
    }

    [Fact]
    public async Task EditorManifest_InjectsToolbarContextAndTab_AndGeneratorsParticipateInUndoAndUnload()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "chapter";
        string path = fixture.Write("game/scenes/chapter.knegraph", graph.Serialize());
        Install(fixture, """
            using KNE;
            using System.Windows.Controls;
            public sealed class EditorExtension : IKnePlugin
            {
                public void Execute(IPluginContext context)
                {
                    if (!context.Api.IsEditor) return;
                    context.Api.RegisterTab(new PluginEditorTab("inventory", "Инвентарь", () => new TextBlock { Text = "Инвентарь проекта" }));
                    context.Api.RegisterCommand(new PluginEditorCommand("generate", "Создать NPC", editor => {
                        var graph = editor.GetGraph()!; graph.Nodes.Add(new ScriptNode { Id = "generated-npc", Kind = ScriptNodeKind.Comment, Text = "NPC Анна" }); editor.ReplaceGraph(graph);
                    }));
                    context.Api.RegisterCommand(new PluginEditorCommand("annotate", "Переименовать реплику", editor => {
                        var graph = editor.GetGraph()!; graph.Nodes.Find(node => node.Id == "dialogue")!.Text = "Из контекстного меню"; editor.ReplaceGraph(graph);
                    }, "context"));
                }
            }
            """);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow(); var previous = PluginCatalog.Current;
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", path);
                var toolbar = Find<Panel>(window, "PluginToolbar"); var tabs = Find<TabControl>(window, "DocumentTabs");
                var editor = (ScriptEditor)((TabItem)tabs.Items[0]).Tag;
                Assert.Single(toolbar.Children.OfType<Button>(), button => Equals(button.Content, "Диалоги / перевод…"));
                var commands = toolbar.Children.OfType<Button>().Where(button => !Equals(button.Content, "Диалоги / перевод…")).ToArray();
                Assert.Equal(new[] { "Создать NPC", "Инвентарь" }, commands.Select(button => button.Content));
                Click(commands[0]); await Drain();
                Assert.Equal("NPC Анна", editor.Document!.Graph!.Nodes.Single(node => node.Id == "generated-npc").Text);
                var menu = Find<Grid>(window, "EditorWorkspace").ContextMenu;
                var item = Assert.Single(menu.Items.OfType<MenuItem>());
                Assert.Equal("Переименовать реплику", item.Header);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); await Drain();
                Assert.Equal("Из контекстного меню", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Text);
                Click(Find<Button>(editor, "UndoButton"));
                Assert.Equal("Добро пожаловать!", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").Text);
                Assert.Contains(editor.Document.Graph.Nodes, node => node.Id == "generated-npc");
                Click(Find<Button>(editor, "UndoButton")); Assert.DoesNotContain(editor.Document.Graph.Nodes, node => node.Id == "generated-npc");
                Click(commands[1]); await Drain(); Click(commands[1]); await Drain();
                Assert.Equal(2, tabs.Items.Count);
                var customTab = Assert.Single(tabs.Items.OfType<TabItem>(), tab => tab.Tag is PluginEditorTab);
                var workspace = Find<Grid>(window, "EditorWorkspace");
                var view = Assert.Single(workspace.Children.OfType<TextBlock>(), text => text.Tag is PluginEditorTab);
                Assert.Equal("Инвентарь проекта", view.Text); Assert.Equal(Visibility.Visible, view.Visibility);
                Assert.Equal(Visibility.Collapsed, editor.Visibility);
                Click(((StackPanel)customTab.Header).Children.OfType<Button>().Single());
                Assert.Single(tabs.Items); Assert.DoesNotContain(view, workspace.Children.OfType<UIElement>());
                Assert.Equal(Visibility.Visible, editor.Visibility);
                Click(commands[1]); await Drain();
                var activeApi = PluginCatalog.Current!; Invoke(window, "UnloadEditorPlugins");
                Assert.Empty(toolbar.Children); Assert.Null(workspace.ContextMenu);
                Assert.Single(tabs.Items); Assert.Null(PluginCatalog.Current);
                Assert.Throws<ObjectDisposedException>(() => activeApi.RegisterGroup(new("late", "Поздно", "#123456")));
                editor.ConfirmSave = _ => MessageBoxResult.No; await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", editor);
            }
            finally { Invoke(window, "UnloadEditorPlugins"); window.Close(); PluginCatalog.Current = previous; }
        });
    }

    [Fact]
    public void LuaManifest_RegistersCustomEditorGroupBlockAndPalette_ThroughRealProjectDiscovery()
    {
        using var fixture = new TempProject();
        fixture.Write("game/plugins/editor.lua", """
            if not kne.is_editor then error('expected editor registration host') end
            function run_block(parameters) return 1 end
            kne.register_group([[{"Id":"lua-tools","Title":"Lua инструменты","Color":"#9844BB"}]])
            kne.register_block([[{"Id":"lua.custom","Title":"Lua инструмент","Group":"lua-tools","Icon":"◆","Color":"#9844BB","Parameters":[{"Id":"name","Label":"Имя","Default":"Анна"}]}]], 'run_block')
            kne.register_palette([[{"Id":"lua-neon","Title":"Lua неон","Background":"#102030","Surface":"#203040","Text":"#FFFFFF","Accent":"#DD33AA","Muted":"#888899"}]])
            """);
        fixture.Write("plugins/editor.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("lua-editor", "game/plugins/editor.lua")));
        using var host = new PluginHost(fixture.ProjectPath, true, new object());
        host.Load(() => new EditorPluginContext(host.Api));
        var previous = PluginCatalog.Current;
        try
        {
            PluginCatalog.Current = host.Api;
            Assert.Contains(BlockLibrary.Groups, group => group.Id == "lua-tools" && group.Title == "Lua инструменты");
            Assert.Equal("plugins", BlockLibrary.Groups[^1].Id);
            var definition = Assert.Single(BlockLibrary.Definitions, entry => entry.Plugin?.Id == "lua.custom");
            var node = BlockLibrary.Create(definition);
            Assert.Equal("◆", node.Plugin!.Icon); Assert.Equal("Анна", node.Plugin.Parameters["name"]!.GetValue<string>());
            Assert.Equal("Lua неон", EditorTheme.Themes.Single(theme => theme.Id == "lua-neon").Label);
        }
        finally { PluginCatalog.Current = previous; }
    }

    [Fact]
    public async Task PluginParameterEditors_CommitTypedValuesTogether_AndGeneratorReplacementIsOneUndoStep()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("chapter.knegraph", VisualScript.Create().Serialize());
        using var api = new PluginApi(fixture.ProjectPath, true);
        var definition = new PluginBlock("demo.properties", "Параметры", "logic", Parameters: [
            new("text", "Текст", Default: "исходный"), new("amount", "Количество", "slider", "2", 0, 10),
            new("mode", "Режим", "choice", "first", Choices: ["first", "second"]), new("active", "Активен", "boolean", "false")]);
        api.RegisterBlock(definition, (_, _) => 1);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var previous = PluginCatalog.Current; PluginCatalog.Current = api; var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path);
                editor.AddBlock(new(ScriptNodeKind.Plugin, "logic", definition.Title, "", definition.Color, definition));
                string id = editor.Document!.Graph!.Nodes.Last().Id;
                var properties = Find<StackPanel>(editor, "ExtendedProperties");
                Assert.Equal(new[] { "Текст", "Количество", "Режим", "Активен" }, properties.Children.OfType<TextBlock>().Select(label => label.Text));
                properties.Children.OfType<TextBox>().Single().Text = "изменённый";
                properties.Children.OfType<Slider>().Single().Value = 7.5;
                properties.Children.OfType<ComboBox>().Single().SelectedItem = "second";
                properties.Children.OfType<CheckBox>().Single().IsChecked = true;
                var pendingSnapshot = editor.GetPluginGraph()!;
                Assert.Equal("изменённый", pendingSnapshot.Nodes.Single(node => node.Id == id).Plugin!.Parameters["text"]!.GetValue<string>());
                var values = editor.Document.Graph.Nodes.Single(node => node.Id == id).Plugin!.Parameters;
                Assert.Equal("изменённый", values["text"]!.GetValue<string>()); Assert.Equal(7.5, values["amount"]!.GetValue<double>());
                Assert.Equal("second", values["mode"]!.GetValue<string>()); Assert.True(values["active"]!.GetValue<bool>());
                Click(Find<Button>(editor, "UndoButton"));
                Assert.Equal("исходный", editor.Document.Graph.Nodes.Single(node => node.Id == id).Plugin!.Parameters["text"]!.GetValue<string>());
                Click(Find<Button>(editor, "RedoButton"));
                string beforeGeneration = editor.Document.Graph.Serialize();
                var generated = editor.GetPluginGraph()!; generated.Nodes[1].Text = "Сгенерировано";
                generated.Nodes.Add(new() { Id = "note", Kind = ScriptNodeKind.Comment, Text = "новый блок" });
                Assert.Equal("Добро пожаловать!", editor.Document.Graph.Nodes[1].Text);
                editor.ReplacePluginGraph(generated); generated.Nodes[1].Text = "Изменение после commit";
                Assert.Equal("Сгенерировано", editor.Document.Graph.Nodes[1].Text);
                Assert.Equal("новый блок", editor.Document.Graph.Nodes.Single(node => node.Id == "note").Text);
                Click(Find<Button>(editor, "UndoButton")); Assert.Equal(beforeGeneration, editor.Document.Graph.Serialize());
                Assert.Throws<InvalidOperationException>(() => editor.ReplacePluginGraph(ShaderGraphCompiler.Create()));
                Assert.Equal(beforeGeneration, editor.Document.Graph.Serialize());
            }
            finally { editor.Clear(); PluginCatalog.Current = previous; }
        });
    }

    [Fact]
    public async Task MultiInputPlugin_RendersDistinctHandles_ConnectsEitherDirection_AndPersistsSelectedInputWithUndo()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath, true);
        var definition = new PluginBlock("multi.input", "Несколько входов", "logic", Inputs: ["Первый", "Второй", "Третий"], Outputs: ["Успех", "Ошибка"]);
        var noInputs = new PluginBlock("no.input", "Без входа", "logic", Inputs: []);
        api.RegisterBlock(definition, (_, _) => 1); api.RegisterBlock(noInputs, (_, _) => 1);
        var graph = VisualScript.Create(); var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", definition.Title, "", definition.Color, definition));
        node.Id = "multi"; node.X = 400; node.Y = 300; node.SetOutput(0, "dialogue"); node.SetOutput(1, "dialogue"); graph.Nodes.Add(node);
        string path = fixture.Write("ports.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var previous = PluginCatalog.Current; PluginCatalog.Current = api; var editor = new ScriptEditor();
            try
            {
                await editor.OpenAsync(fixture.ProjectPath, path); Layout(editor);
                var ports = GraphPorts(editor);
                Assert.Equal(5, ports.Keys.Count(key => key.Item1 == "multi"));
                Assert.False(ports.ContainsKey(("multi", null)));
                Assert.Equal(new[] { "Первый", "Второй", "Третий" }, new[] { -1, -2, -3 }.Select(index => ports[("multi", index)].Content));
                Assert.All(new[] { -1, -2, -3 }, index => Assert.False(ports[("multi", index)].IsOutput));
                Assert.True(ports[("multi", 0)].IsOutput); Assert.True(ports[("multi", 1)].IsOutput);
                var card = Find<Canvas>(editor, "GraphCanvas").Children.OfType<Border>().Single(item => Equals(item.Tag, "multi"));
                Assert.True(card.ActualHeight >= 172);
                var inputYs = new[] { -1, -2, -3 }.Select(index => ports[("multi", index)].TranslatePoint(new Point(11, 11), card).Y).ToArray();
                Assert.True(inputYs[0] < inputYs[1] && inputYs[1] < inputYs[2]); Assert.InRange(inputYs[2], 0, card.ActualHeight);
                Assert.True(editor.ConnectGraphPorts("start", 0, "multi", -2));
                Assert.Equal("multi", editor.Document!.Graph!.Nodes[0].Next); Assert.Equal(1, editor.Document.Graph.Nodes[0].TargetInput(0));
                Layout(editor); Invoke(editor, "RenderLinks"); AssertLinkEndsAt(editor, "start", 0, "multi", -2);
                Click(Find<Button>(editor, "UndoButton")); Assert.Equal("dialogue", editor.Document.Graph.Nodes[0].Next); Assert.Equal(0, editor.Document.Graph.Nodes[0].TargetInput(0));
                Click(Find<Button>(editor, "RedoButton")); Assert.Equal(1, editor.Document.Graph.Nodes[0].TargetInput(0));
                Assert.True(editor.ConnectGraphPorts("multi", -3, "start", 0));
                Assert.Equal(2, editor.Document.Graph.Nodes[0].TargetInput(0));
                // Repainting happens before layout, so the third input must also have a valid fallback anchor.
                Invoke(editor, "RenderLinks");
                Click(Find<Button>(editor, "UndoButton")); Assert.Equal(1, editor.Document.Graph.Nodes[0].TargetInput(0));
                Click(Find<Button>(editor, "RedoButton")); Assert.Equal(2, editor.Document.Graph.Nodes[0].TargetInput(0));
                await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
                Assert.Equal(2, editor.Document!.Graph!.Nodes[0].TargetInput(0));
                Layout(editor); Invoke(editor, "RenderLinks"); AssertLinkEndsAt(editor, "start", 0, "multi", -3);
                Assert.True(editor.ConnectGraphPorts("multi", -1, "start", 0));
                Assert.Equal(0, editor.Document.Graph.Nodes[0].TargetInput(0));
                Assert.False(editor.Document.Graph.Nodes[0].TargetInputs?.ContainsKey(0) == true);
                editor.AddBlock(new(ScriptNodeKind.Plugin, "logic", noInputs.Title, "", noInputs.Color, noInputs));
                string noInputId = editor.Document.Graph.Nodes.Last().Id;
                string beforeRejected = editor.Document.Graph.Serialize();
                Assert.False(editor.ConnectGraphPorts("start", 0, "multi", -4));
                Assert.False(editor.ConnectGraphPorts("multi", -1, "multi", -2));
                Assert.False(editor.ConnectGraphPorts("start", 0, noInputId, -1));
                Assert.Equal(beforeRejected, editor.Document.Graph.Serialize());
            }
            finally { editor.Clear(); PluginCatalog.Current = previous; }
        });
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 20)]
    public void TargetInputMetadata_RejectsInvalidOutputKeysAndInputIndices_WithSourceIdentity(int output, int input)
    {
        var graph = VisualScript.Create(); graph.Nodes[0].TargetInputs = new() { [output] = input };
        var error = Assert.Throws<InvalidDataException>(() => graph.Serialize());
        Assert.Contains("start", error.Message); Assert.Contains("целевого входа", error.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 19)]
    public void Compiler_RejectsMissingDestinationInputIncludingZeroInputBlocks(int inputCount, int selectedInput)
    {
        var graph = VisualScript.Create();
        var custom = new PluginBlock("target", "Цель", "logic", Inputs: Enumerable.Range(0, inputCount).Select(index => "Вход" + index).ToArray());
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", custom.Title, "", custom.Color, custom));
        node.Id = "target"; node.SetOutput(0, "dialogue"); graph.Nodes.Add(node); graph.Nodes[0].SetOutput(0, node.Id, selectedInput);
        var parsed = VisualScript.Parse(graph.Serialize());
        var error = Assert.Throws<InvalidDataException>(() => parsed.Compile());
        Assert.Contains("start", error.Message); Assert.Contains("целевой входной порт отсутствует", error.Message);
    }

    [Fact]
    public async Task Picker_CustomBlockNamedLikeBuiltIn_UsesDistinctPreferencesAndCanBeSelected()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath, true);
        api.RegisterBlock(new("Dialogue", "Плагин-реплика", "narrative"), (_, _) => 1);
        await WorkspaceTabsTests.Sta(async () =>
        {
            var previous = PluginCatalog.Current; PluginCatalog.Current = api;
            try
            {
                var builtIn = BlockLibrary.Definitions.Single(entry => entry.Kind == ScriptNodeKind.Dialogue);
                var custom = BlockLibrary.Definitions.Single(entry => entry.Plugin?.Id == "Dialogue");
                Assert.Equal("narrative/Dialogue", BlockLibraryPreferences.Key(builtIn));
                Assert.Equal("$plugin/Dialogue", BlockLibraryPreferences.Key(custom));
                var store = new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "picker.json"));
                await store.SaveAsync(new() { Favorites = [BlockLibraryPreferences.Key(builtIn), BlockLibraryPreferences.Key(custom)] });
                var picker = new BlockLibraryView(store); BlockDefinition? selected = null; picker.BlockSelected += (_, entry) => selected = entry;
                picker.Open(_ => true); await picker.LoadPreferencesAsync();
                var groups = Find<ListBox>(picker, "BlockGroups"); groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "favorites");
                var cards = Find<WrapPanel>(picker, "BlockCards").Children.OfType<Button>().ToArray(); Assert.Equal(2, cards.Length);
                Click(cards.Single(card => Equals(card.Tag, ScriptNodeKind.Plugin)));
                Assert.Equal("Dialogue", selected!.Plugin!.Id);
                await picker.RecordUseAsync(custom); await picker.SavePreferencesAsync();
                var saved = await store.LoadAsync(); Assert.Equal(new[] { "narrative/Dialogue", "$plugin/Dialogue" }, saved.Favorites);
                Assert.Equal("$plugin/Dialogue", Assert.Single(saved.Recent));
            }
            finally { PluginCatalog.Current = previous; }
        });
    }

    [Fact]
    public void Preferences_KeepUnavailablePluginEntriesAcrossProjects_AndBoundFavoritesAndRecent()
    {
        var previous = PluginCatalog.Current; PluginCatalog.Current = null;
        try
        {
            var preferences = new BlockLibraryPreferences { Favorites = ["$plugin/unavailable", "$plugin/unavailable", "narrative/Dialogue", "invalid/obsolete", "$plugin/" + new string('a', 161)],
                Recent = ["$plugin/unavailable", "narrative/Dialogue", "$plugin/unavailable"] };
            preferences.Normalize();
            Assert.Equal(new[] { "$plugin/unavailable", "narrative/Dialogue" }, preferences.Favorites);
            Assert.Equal(new[] { "$plugin/unavailable", "narrative/Dialogue" }, preferences.Recent);
            var all = Enumerable.Range(0, 501).Select(index => "$plugin/p" + index).ToList();
            preferences.Favorites = all; preferences.Recent = all; preferences.Normalize();
            Assert.Equal(500, preferences.Favorites.Count); Assert.Equal("$plugin/p499", preferences.Favorites[^1]);
            Assert.Equal(12, preferences.Recent.Count); Assert.Equal("$plugin/p11", preferences.Recent[^1]);
            Assert.DoesNotContain("$plugin/p500", preferences.Favorites);
        }
        finally { PluginCatalog.Current = previous; }
    }

    [Theory]
    [InlineData("favorites")]
    [InlineData("recent")]
    public void PluginGroups_RejectReservedPickerCategoryNames(string id)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var error = Assert.Throws<ArgumentException>(() => api.RegisterGroup(new(id, "Зарезервировано", "#123456")));
        Assert.Contains(id, error.Message); Assert.Empty(api.Groups);
    }

    [Fact]
    public async Task PluginPalette_UpdatesDynamicResourcesAndCategoryColors_ThenRestoresBuiltInTheme()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath, true);
        api.RegisterPalette(new("neon", "Неон", "#081020", "#172B40", "#EAF7FF", "#DD00FF", "#8CA0B8", new() { ["logic"] = "#10EEDD" }));
        await WorkspaceTabsTests.Sta(() =>
        {
            var previous = PluginCatalog.Current; PluginCatalog.Current = api; var window = new Window();
            var button = new Button(); button.SetResourceReference(Control.BackgroundProperty, "KNE.AccentBrush"); window.Content = button;
            try
            {
                Assert.Equal("Неон", EditorTheme.Themes.Single(theme => theme.Id == "neon").Label);
                EditorTheme.Apply(window, "neon");
                Assert.Equal(Color.FromRgb(0x08, 0x10, 0x20), Assert.IsType<SolidColorBrush>(window.Background).Color);
                Assert.Equal(Color.FromRgb(0xEA, 0xF7, 0xFF), Assert.IsType<SolidColorBrush>(window.Foreground).Color);
                Assert.Equal(Color.FromRgb(0xDD, 0x00, 0xFF), Assert.IsType<SolidColorBrush>(button.Background).Color);
                Assert.Equal(Color.FromRgb(0x10, 0xEE, 0xDD), BlockAccessibility.Presentation("logic", "normal").Color);
                EditorTheme.Apply(window, "dark"); Assert.Null(api.ActivePalette);
                Assert.Equal(Color.FromRgb(0x19, 0x1C, 0x24), Assert.IsType<SolidColorBrush>(window.Background).Color);
                Assert.Equal(Color.FromRgb(0x7C, 0x5C, 0xFF), Assert.IsType<SolidColorBrush>(button.Background).Color);
                Assert.Equal(Color.FromRgb(0x7C, 0x5C, 0xFF), Assert.IsType<Color>(window.FindResource("KNE.AccentColor")));
                Assert.Equal(Color.FromRgb(0x25, 0x63, 0xA6), BlockAccessibility.Presentation("logic", "normal").Color);
            }
            finally { window.Close(); PluginCatalog.Current = previous; }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LocalImage_LoadsExternalPngAtOriginalSizeFrozen_AndReleasesSourceFile()
    {
        using var fixture = new TempProject(); using var assets = new PluginAssets(fixture.ProjectPath);
        string path = Path.Combine(fixture.Root, "external.PNG");
        await WorkspaceTabsTests.Sta(() =>
        {
            byte[] pixels = [0, 0, 255, 255, 255, 0, 0, 255];
            var source = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using (var output = File.Create(path)) encoder.Save(output);
            var loaded = assets.LoadImage(path); Assert.True(loaded.IsFrozen);
            Assert.Equal(2, loaded.PixelWidth); Assert.Equal(1, loaded.PixelHeight);
            var copied = new byte[8]; new FormatConvertedBitmap(loaded, PixelFormats.Bgra32, null, 0).CopyPixels(copied, 8, 0);
            Assert.Equal(pixels, copied);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(path); Assert.False(File.Exists(path));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(5000, 4096)]
    [InlineData(16384, 4096)]
    [InlineData(16385, 0)]
    public async Task ImageResources_DownsampleToBoundedDimensions_AndRejectOversizeHeaders(int width, int decodedWidth)
    {
        using var fixture = new TempProject(); using var assets = new PluginAssets(fixture.ProjectPath);
        string path = Path.Combine(fixture.Root, "wide.png");
        await WorkspaceTabsTests.Sta(() =>
        {
            var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, new byte[width * 4], width * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            if (decodedWidth == 0) Assert.Throws<InvalidDataException>(() => assets.LoadImage(path));
            else
            {
                var image = assets.LoadImage(path);
                Assert.Equal(decodedWidth, image.PixelWidth); Assert.Equal(1, image.PixelHeight); Assert.True(image.IsFrozen);
            }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(32 * 1024 * 1024, true)]
    [InlineData(32 * 1024 * 1024 + 1, false)]
    public void LocalAssets_EnforceExactResourceSizeBoundaryWithoutLoadingContents(int length, bool accepted)
    {
        using var fixture = new TempProject(); using var assets = new PluginAssets(fixture.ProjectPath);
        string path = fixture.Write("game/resource.bin", ""); using (var stream = File.OpenWrite(path)) stream.SetLength(length);
        if (accepted) Assert.Equal(path, assets.Local("game/resource.bin", ".bin"));
        else Assert.Throws<InvalidDataException>(() => assets.Local("game/resource.bin", ".bin"));
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Throws<ArgumentException>(() => assets.Local(path, ".png"));
        Assert.Throws<FileNotFoundException>(() => assets.Local("game/missing.png"));
    }

    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("ftp://example.invalid/resource")]
    [InlineData("relative")]
    public async Task HttpAssets_RejectNonHttpSchemesBeforeAnyRequest(string url)
    {
        using var fixture = new TempProject(); using var assets = new PluginAssets(fixture.ProjectPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.RelativeOrAbsolute));
        await Assert.ThrowsAsync<ArgumentException>(() => assets.SendAsync(request));
        await Assert.ThrowsAsync<ArgumentException>(() => assets.DownloadAsync(new Uri("https://example.invalid/data"), ".exe"));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne", "plugin-assets")));
    }

    [Fact]
    public async Task HttpAssets_SendPreservesRequestMethodHeadersAndBody_AndOwnsOnlyResponseAndHandler()
    {
        using var fixture = new TempProject(); var responseContent = new TrackedContent(Encoding.UTF8.GetBytes("ответ"));
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("https://example.invalid/cloud/save", request.RequestUri!.AbsoluteUri);
            Assert.Equal("project-id", Assert.Single(request.Headers.GetValues("X-KNE-Project")));
            Assert.Equal("{\"slot\":2}", request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK) { Content = responseContent };
        });
        var assets = new PluginAssets(fixture.ProjectPath, handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/cloud/save") { Content = new StringContent("{\"slot\":2}") };
        request.Headers.Add("X-KNE-Project", "project-id");
        Assert.Equal("ответ", Encoding.UTF8.GetString(await assets.SendAsync(request)));
        Assert.True(responseContent.Disposed); Assert.False(handler.Disposed);
        Assert.Equal("{\"slot\":2}", await request.Content.ReadAsStringAsync());
        Assert.Equal(1, handler.Calls);
        assets.Dispose(); assets.Dispose(); Assert.True(handler.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => assets.SendAsync(request));
    }

    [Fact]
    public async Task HttpAssets_DownloadsIntoContentAddressedProjectCache_WithoutDuplicateFiles()
    {
        using var fixture = new TempProject(); byte[] payload = Encoding.UTF8.GetBytes("portable resource");
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        using var assets = new PluginAssets(fixture.ProjectPath, handler);
        string first = await assets.DownloadAsync(new("https://example.invalid/image"), ".png");
        string second = await assets.DownloadAsync(new("https://example.invalid/image"), ".png");
        Assert.Equal(first, second); Assert.Equal(payload, await File.ReadAllBytesAsync(first));
        Assert.Equal(first, PathSafety.EnsureInside(fixture.ProjectPath, first));
        Assert.EndsWith(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)) + ".png", first);
        Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(first)!)); Assert.Equal(2, handler.Calls);
        string outside = Path.Combine(fixture.Root, "external.png"); File.WriteAllBytes(outside, payload);
        string imported = assets.ImportLocal(outside);
        Assert.False(Path.IsPathRooted(imported));
        Assert.Equal(first, PathSafety.ResolveRelative(fixture.ProjectPath, imported));
        Assert.Equal(payload, File.ReadAllBytes(outside));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpAssets_RejectOversizeHeaderAndChunkedResponses_AndDisposeContent(bool lengthHeader)
    {
        using var fixture = new TempProject();
        var content = new StreamContent(new GeneratedStream(32 * 1024 * 1024 + 1));
        if (lengthHeader) content.Headers.ContentLength = 32 * 1024 * 1024 + 1;
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = content }); using var assets = new PluginAssets(fixture.ProjectPath, handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/large");
        await Assert.ThrowsAsync<InvalidDataException>(() => assets.SendAsync(request));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.ReadAsStreamAsync());
        Assert.Equal(1, handler.Calls);
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, ".kne", "plugin-assets")));
    }

    [Fact]
    public async Task HttpAssets_AcceptExactBodyLimit_AndPropagateHttpFailureAndCancellation()
    {
        using var fixture = new TempProject();
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/exact"
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new GeneratedStream(32 * 1024 * 1024)) }
            : new(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([]) });
        using var assets = new PluginAssets(fixture.ProjectPath, handler);
        using var exact = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/exact");
        Assert.Equal(32 * 1024 * 1024, (await assets.SendAsync(exact)).Length);
        using var denied = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/denied");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => assets.SendAsync(denied));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        using var canceled = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/exact");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => assets.SendAsync(canceled, cancellation.Token));
    }

    [Fact]
    public void AudioEffects_DryReadPreservesSamplesAndCallerBufferOutsideRequestedRange()
    {
        var effects = new PluginAudioEffects(new Samples([.25f, -.5f, .875f]));
        float[] buffer = [9, 9, 9, 9, 9];
        Assert.Equal(3, effects.Read(buffer, 1, 3));
        Assert.Equal(new[] { 9f, .25f, -.5f, .875f, 9f }, buffer);
        Assert.Equal(0, effects.Read(buffer.AsSpan(1, 3)));
        Assert.Equal(new[] { 9f, .25f, -.5f, .875f, 9f }, buffer);
        Assert.Equal(57, effects.WaveFormat.SampleRate); Assert.Equal(1, effects.WaveFormat.Channels);
    }

    [Fact]
    public void AudioEffects_EchoAndReverbProduceDelayedSignalAndFeedback_WithEquivalentArrayAndSpanReads()
    {
        float[] impulse = new float[40]; impulse[0] = .5f;
        var echo = new PluginAudioEffects(new Samples(impulse)) { Echo = .8f }; var echoData = new float[40];
        Assert.Equal(40, echo.Read(echoData)); Assert.Equal(.5f, echoData[0]); Assert.Equal(.4f, echoData[19], 6); Assert.Equal(0, echoData[38]);
        var reverb = new PluginAudioEffects(new Samples(impulse)) { Reverb = 1 }; var reverbData = new float[40];
        Assert.Equal(40, reverb.Read(reverbData)); Assert.Equal(.3f, reverbData[3], 6); Assert.Equal(.195f, reverbData[6], 6); Assert.Equal(.12675f, reverbData[9], 6);
        var combinedArray = new PluginAudioEffects(new Samples(impulse)) { Echo = .5f, Reverb = .7f };
        var combinedSpan = new PluginAudioEffects(new Samples(impulse)) { Echo = .5f, Reverb = .7f };
        float[] whole = new float[40], chunked = new float[40];
        Assert.Equal(40, combinedArray.Read(whole, 0, 40));
        Assert.Equal(13, combinedSpan.Read(chunked.AsSpan(0, 13))); Assert.Equal(27, combinedSpan.Read(chunked.AsSpan(13)));
        Assert.Equal(whole, chunked);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void AudioEffects_ClampMixedOutputToLegalAmplitude(float sample)
    {
        float[] signal = new float[20]; signal[0] = sample; signal[19] = sample;
        var effects = new PluginAudioEffects(new Samples(signal)) { Echo = 1 }; float[] output = new float[20];
        Assert.Equal(20, effects.Read(output)); Assert.Equal(sample, output[19]);
        Assert.All(output, value => Assert.InRange(value, -1, 1));
    }

    [Theory]
    [InlineData(-.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AudioEffects_RejectInvalidIntensityWithoutChangingLastAcceptedValues(float invalid)
    {
        var effects = new PluginAudioEffects(new Samples([])) { Echo = .3f, Reverb = .7f };
        Assert.Throws<ArgumentOutOfRangeException>(() => effects.Echo = invalid);
        Assert.Throws<ArgumentOutOfRangeException>(() => effects.Reverb = invalid);
        Assert.Equal(.3f, effects.Echo); Assert.Equal(.7f, effects.Reverb);
    }

    private static string ReadPdfText(PdfPage page)
    {
        var result = new StringBuilder(); var fonts = page.Resources.Elements.GetDictionary("/Font")!;
        Dictionary<int, string>? mapping = null;
        foreach (var operation in ContentReader.ReadContent(page).OfType<COperator>())
        {
            if (operation.OpCode.Name == "Tf")
            {
                var font = fonts.Elements.GetDictionary(((CName)operation.Operands[0]).Name)!;
                var unicode = font.Elements.GetDictionary("/ToUnicode");
                mapping = unicode == null ? null : ReadUnicodeMap(Encoding.ASCII.GetString(unicode.Stream!.UnfilteredValue));
            }
            else if (operation.OpCode.Name is "Tj" or "TJ")
            {
                foreach (string value in PdfStrings(operation.Operands))
                {
                    if (mapping == null) { result.Append(value); continue; }
                    for (int index = 0; index + 1 < value.Length; index += 2)
                    {
                        int glyph = (value[index] << 8) | value[index + 1];
                        result.Append(mapping.GetValueOrDefault(glyph, "?"));
                    }
                }
                result.Append(' ');
            }
        }
        return result.ToString();
    }
    private static Dictionary<int, string> ReadUnicodeMap(string cmap)
    {
        var mapping = new Dictionary<int, string>();
        foreach (string line in cmap.Split('\n'))
        {
            var hex = Regex.Matches(line, "<([0-9A-Fa-f]+)>").Select(match => match.Groups[1].Value).ToArray();
            if (hex.Length is not (2 or 3) || hex[0].Length > 4) continue;
            int first = int.Parse(hex[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (hex.Length == 2) mapping[first] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex[1]));
            else
            {
                int last = int.Parse(hex[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int character = int.Parse(hex[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                for (int glyph = first; glyph <= last; glyph++) mapping[glyph] = char.ConvertFromUtf32(character + glyph - first);
            }
        }
        return mapping;
    }
    private static IEnumerable<string> PdfStrings(CObject item)
    {
        if (item is CString text) yield return text.Value;
        else if (item is CSequence sequence) foreach (var child in sequence) foreach (string value in PdfStrings(child)) yield return value;
    }
    private sealed class Samples(float[] values) : ISampleProvider
    {
        private int index;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(57, 1);
        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<float> buffer) { int count = Math.Min(buffer.Length, values.Length - index); values.AsSpan(index, count).CopyTo(buffer); index += count; return count; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(request)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class TrackedContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class GeneratedStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { int read = Math.Min(count, remaining); buffer.AsSpan(offset, read).Clear(); remaining -= read; return read; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); int read = Math.Min(buffer.Length, remaining); buffer.Span[..read].Clear(); remaining -= read; return ValueTask.FromResult(read); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static T Find<T>(FrameworkElement element, string name) where T : class => WorkspaceTabsTests.Find<T>(element, name);
    private static Dictionary<(string, int?), GraphPort> GraphPorts(ScriptEditor editor) =>
        (Dictionary<(string, int?), GraphPort>)typeof(ScriptEditor).GetField("graphPorts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
    private static void Layout(ScriptEditor editor)
    { editor.Measure(new Size(1200, 760)); editor.Arrange(new Rect(0, 0, 1200, 760)); editor.UpdateLayout(); }
    private static void AssertLinkEndsAt(ScriptEditor editor, string source, int output, string target, int input)
    {
        var canvas = Find<Canvas>(editor, "GraphCanvas"); var ports = GraphPorts(editor);
        Point start = ports[(source, output)].TranslatePoint(new Point(11, 11), canvas);
        Point end = ports[(target, input)].TranslatePoint(new Point(11, 11), canvas);
        var figure = canvas.Children.OfType<System.Windows.Shapes.Path>().Select(path => (PathGeometry)path.Data)
            .Select(geometry => geometry.Figures.Single()).Single(item => item.StartPoint == start);
        Assert.Equal(end, Assert.IsType<BezierSegment>(Assert.Single(figure.Segments)).Point3);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static object? Invoke(object target, string method, params object[] arguments) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static async Task Drain() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void Install(TempProject fixture, string source)
    {
        fixture.Write("game/plugins/editor.cs", source);
        fixture.Write("plugins/editor.kneplugin.json", JsonSerializer.Serialize(new PluginManifest("editor-test", "game/plugins/editor.cs")));
    }
}
