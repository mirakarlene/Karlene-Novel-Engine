using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KNE.Tests;

public sealed class PluginSdkTests
{
    [Fact]
    public void Registration_AddsDistinctBlocksToBuiltInAndCustomGroups_WithStableDefaultsAndIdentity()
    {
        using var fixture = new TempProject();
        using var api = new PluginApi(fixture.ProjectPath, isEditor: true);
        api.RegisterGroup(new("quests", "Квесты", "#582AA8"));
        var first = new PluginBlock("quest.accept", "Принять квест", "quests", Icon: "⚑", Color: "#582AA8",
            Inputs: ["Начать", "Повторить"], Outputs: ["Успех", "Ошибка"],
            Parameters: [new("quest", "Квест", Default: "first")]);
        var second = new PluginBlock("quest.check", "Проверить квест", "logic");
        api.RegisterBlock(first, (_, _) => 1);
        api.RegisterBlock(second, (_, _) => 1);
        var previous = PluginCatalog.Current;
        try
        {
            PluginCatalog.Current = api;
            Assert.Equal("plugins", BlockLibrary.Groups[^1].Id);
            Assert.Contains(BlockLibrary.Groups, group => group.Id == "quests" && group.Color == "#582AA8");
            var entries = BlockLibrary.Definitions.Where(entry => entry.Kind == ScriptNodeKind.Plugin).ToArray();
            Assert.Equal(new[] { "quest.accept", "quest.check" }, entries.Select(entry => entry.Plugin!.Id));
            var node = BlockLibrary.Create(entries[0]);
            Assert.Equal("Принять квест", node.Title);
            Assert.Equal("⚑", node.Plugin!.Icon);
            Assert.Equal(new[] { "Начать", "Повторить" }, node.Plugin.Inputs);
            Assert.Equal(new[] { "Успех", "Ошибка" }, node.Outputs().Select(port => port.Label));
            Assert.Equal("first", node.Plugin.Parameters["quest"]!.GetValue<string>());
            node.Plugin.Parameters["quest"] = "edited";
            Assert.Equal("first", BlockLibrary.Create(entries[0]).Plugin!.Parameters["quest"]!.GetValue<string>());
            Assert.Equal("#582AA8", BlockLibrary.ColorFor(node));
        }
        finally { PluginCatalog.Current = previous; }
    }

    [Fact]
    public void GraphRoundTrip_PreservesMultiplePluginKindsParametersAndBranchTargets_WithoutLoadedExtension()
    {
        var first = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "custom", "Первый", "", "#3322AA",
            new("custom.first", "Первый", "custom", Outputs: ["Да", "Нет"])));
        var second = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", "Второй", "", "#4433BB",
            new("custom.second", "Второй", "logic")));
        first.Id = "first"; second.Id = "second";
        first.Plugin!.Parameters["payload"] = JsonNode.Parse("{\"list\":[1,2],\"text\":\"кавычки \\\" и строка\\n\"}");
        first.SetOutput(0, "second"); first.SetOutput(1, "dialogue"); second.SetOutput(0, "dialogue");
        var graph = VisualScript.Create(); graph.Nodes[0].Next = "first"; graph.Nodes.AddRange([first, second]);

        var restored = VisualScript.Parse(graph.Serialize());
        var restoredFirst = restored.Nodes.Single(node => node.Id == "first");
        var restoredSecond = restored.Nodes.Single(node => node.Id == "second");
        Assert.Equal("custom.first", restoredFirst.Plugin!.BlockId);
        Assert.Equal("custom.second", restoredSecond.Plugin!.BlockId);
        Assert.Equal(new[] { "second", "dialogue" }, restoredFirst.Outputs().Select(port => port.Target));
        Assert.Equal(2, restoredFirst.Plugin.Parameters["payload"]!["list"]![1]!.GetValue<int>());
        Assert.Equal("кавычки \" и строка\n", restoredFirst.Plugin.Parameters["payload"]!["text"]!.GetValue<string>());
        string code = restored.Compile();
        Assert.Contains("kne_execute_plugin_block(\"custom.first\"", code);
        Assert.Contains("kne_execute_plugin_block(\"custom.second\"", code);
        Assert.Contains("{\"second\", \"dialogue\"}", code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("emptyId")]
    [InlineData("invalidId")]
    [InlineData("nullParameters")]
    [InlineData("nullInputs")]
    [InlineData("invalidColor")]
    [InlineData("duplicateInputs")]
    [InlineData("emptyInputs")]
    [InlineData("emptyOutput")]
    public void GraphMetadata_RejectsMalformedPluginDataWithBlockIdentity(string variation)
    {
        var graph = VisualScript.Create();
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", "Блок", "", "#112233", new("demo.valid", "Блок", "logic")));
        node.Id = "broken-plugin";
        switch (variation)
        {
            case "missing": node.Plugin = null; break;
            case "emptyId": node.Plugin!.BlockId = ""; break;
            case "invalidId": node.Plugin!.BlockId = "contains space"; break;
            case "nullParameters": node.Plugin!.Parameters = null!; break;
            case "nullInputs": node.Plugin!.Inputs = null!; break;
            case "invalidColor": node.Plugin!.Color = "purple"; break;
            case "duplicateInputs": node.Plugin!.Inputs = ["same", "same"]; break;
            case "emptyInputs": node.Plugin!.Inputs = [" "]; break;
            case "emptyOutput": node.Options[0].Text = " "; break;
        }
        graph.Nodes.Add(node);
        var error = Assert.Throws<InvalidDataException>(() => graph.Serialize());
        Assert.Contains("broken-plugin", error.Message);
    }

    [Theory]
    [InlineData("visual")]
    [InlineData("custom")]
    public void RegisterGroup_RejectsBuiltInAndRepeatedIdentity_WithoutChangingCatalog(string id)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        if (id == "custom") api.RegisterGroup(new(id, "Свой раздел", "#123456"));
        Assert.Throws<ArgumentException>(() => api.RegisterGroup(new(id, "Другое имя", "#654321")));
        Assert.Equal(id == "custom" ? 1 : 0, api.Groups.Count);
    }

    [Theory]
    [InlineData("contains space")]
    [InlineData("1first")]
    [InlineData("")]
    public void RegisterBlock_RejectsInvalidIdentity(string id)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new(id, "Блок", "logic"), (_, _) => 1));
        Assert.Empty(api.Blocks);
    }

    [Fact]
    public void RegisterBlock_RejectsUnknownGroupDuplicateIdAndMalformedColor_WithoutReplacingCallback()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var context = new TestContext(api);
        api.RegisterBlock(new("demo", "Демо", "logic", Parameters: [new("branch", "Ветка", "slider", "1", 1, 2)]), (_, values) => values["branch"]!.GetValue<int>());
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("demo", "Замена", "logic"), (_, _) => 99));
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("missing", "Нет группы", "absent"), (_, _) => 1));
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("badcolor", "Цвет", "logic", Color: "purple"), (_, _) => 1));
        Assert.Single(api.Blocks);
        Assert.Equal(2, api.Execute("demo", context, "{\"branch\":2}"));
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 20)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public void RegisterBlock_AcceptsEmptyAndMaximumPortCounts(bool inputs, int count)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var ports = Enumerable.Range(0, count).Select(index => "port" + index).ToArray();
        var block = new PluginBlock("boundary", "Граница", "logic", Inputs: inputs ? ports : null, Outputs: inputs ? null : ports);
        api.RegisterBlock(block, (_, _) => 1);
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", block.Title, "", block.Color, block));
        Assert.Equal(count, inputs ? node.Plugin!.Inputs.Length : node.Outputs().Count());
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("tooMany")]
    [InlineData("tooLong")]
    public void RegisterBlock_RejectsInvalidPorts(string variation)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        string[] ports = variation switch
        {
            "duplicate" => ["same", "same"], "empty" => [" "],
            "tooMany" => Enumerable.Range(0, 21).Select(index => "p" + index).ToArray(),
            _ => [new string('a', 81)]
        };
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("ports", "Порты", "logic", Inputs: ports), (_, _) => 1));
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("ports", "Порты", "logic", Outputs: ports), (_, _) => 1));
        Assert.Empty(api.Blocks);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("reversed")]
    [InlineData("nan")]
    [InlineData("emptyChoice")]
    [InlineData("missingDefault")]
    [InlineData("duplicate")]
    [InlineData("tooMany")]
    public void RegisterBlock_RejectsInvalidParameterSchemas(string variation)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        PluginParameter[] parameters = variation switch
        {
            "unknown" => [new("value", "Значение", "number")],
            "reversed" => [new("value", "Значение", "slider", Minimum: 10, Maximum: 2)],
            "nan" => [new("value", "Значение", "slider", Maximum: double.NaN)],
            "emptyChoice" => [new("value", "Значение", "choice", Choices: [])],
            "missingDefault" => [new("value", "Значение", "choice", "missing", Choices: ["first", "second"])],
            "duplicate" => [new("same", "Первое"), new("same", "Второе")],
            _ => Enumerable.Range(0, 33).Select(index => new PluginParameter("p" + index, "Параметр")).ToArray()
        };
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new("schema", "Схема", "logic", Parameters: parameters), (_, _) => 1));
        Assert.Empty(api.Blocks);
    }

    [Fact]
    public void RegisterBlock_AcceptsExactIdentityTitlePortAndParameterBoundaries_WithEveryEditorKind()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        PluginParameter[] parameters = [new("text", "Текст", Default: "строка"),
            new("speed", "Скорость", "slider", "2", 2, 2),
            new("mode", "Режим", "choice", "second", Choices: ["first", "second"]),
            new("enabled", "Включено", "boolean", "true"),
            .. Enumerable.Range(4, 28).Select(index => new PluginParameter("p" + index, "Параметр"))];
        string id = new('a', 160); string title = new('б', 200); string port = new('п', 80);
        var block = new PluginBlock(id, title, "logic", Color: "#8088AACC", Inputs: [port], Parameters: parameters);
        api.RegisterBlock(block, (_, _) => 1);
        var node = BlockLibrary.Create(new(ScriptNodeKind.Plugin, "logic", title, "", block.Color, block));
        Assert.Equal(32, node.Plugin!.Parameters.Count);
        Assert.Equal(2, node.Plugin.Parameters["speed"]!.GetValue<double>());
        Assert.Equal("second", node.Plugin.Parameters["mode"]!.GetValue<string>());
        Assert.True(node.Plugin.Parameters["enabled"]!.GetValue<bool>());
        Assert.Equal(id, node.Plugin.BlockId); Assert.Equal(title, node.Title);
        Assert.Equal(port, Assert.Single(node.Plugin.Inputs)); Assert.Equal("#8088AACC", BlockLibrary.ColorFor(node));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("title")]
    [InlineData("emptyTitle")]
    public void RegisterBlock_RejectsImmediatelyPastIdentityAndTitleBoundaries(string variation)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        string id = variation == "id" ? new string('a', 161) : "valid";
        string title = variation == "title" ? new string('a', 201) : variation == "emptyTitle" ? " " : "Блок";
        Assert.Throws<ArgumentException>(() => api.RegisterBlock(new(id, title, "logic"), (_, _) => 1));
        Assert.Empty(api.Blocks);
    }

    [Theory]
    [InlineData("function")]
    [InlineData("plugin.callback")]
    [InlineData("1callback")]
    public void RegisterLuaBlock_RejectsInvalidFunctionNamesBeforeRegistering(string name)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        Assert.Throws<ArgumentException>(() => api.RegisterLuaBlock(new("lua", "Lua", "logic"), name));
        Assert.Empty(api.Blocks);
    }

    [Fact]
    public void Execute_FillsMissingTypedDefaultsBeforeCallingPlugin()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        int calls = 0;
        api.RegisterBlock(new("defaults", "Значения", "logic", Parameters: [new("amount", "Количество", "slider", "2.5", 0, 10),
            new("active", "Активен", "boolean", "true"), new("mode", "Режим", "choice", "second", Choices: ["first", "second"])]),
            (_, values) => { calls++; Assert.Equal(2.5, values["amount"]!.GetValue<double>()); Assert.True(values["active"]!.GetValue<bool>()); Assert.Equal("second", values["mode"]!.GetValue<string>()); return 1; });
        Assert.Equal(1, api.Execute("defaults", new TestContext(api), "{}"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"amount\":11}")]
    [InlineData("{\"amount\":\"NaN\"}")]
    [InlineData("{\"active\":\"not-bool\"}")]
    [InlineData("{\"mode\":\"missing\"}")]
    public void Execute_RejectsUnknownAndInvalidValuesBeforeInvokingPlugin(string json)
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath); int calls = 0;
        api.RegisterBlock(new("checked", "Проверка", "logic", Parameters: [new("amount", "Количество", "slider", "2", 0, 10),
            new("active", "Активен", "boolean", "false"), new("mode", "Режим", "choice", "first", Choices: ["first", "second"])]),
            (_, _) => { calls++; return 1; });
        Assert.Throws<ArgumentException>(() => api.Execute("checked", new TestContext(api), json));
        Assert.Equal(0, calls); Assert.Single(api.Blocks);
    }

    [Fact]
    public void RegisterStructure_ValidatesTypedPayloadAndRejectsNullUnknownAndDuplicate()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        api.RegisterStructure<Quest>("quest");
        var quest = api.CreateStructure("quest", "{\"Name\":\"Свет\",\"Reward\":3.5,\"Ignored\":123}");
        Assert.Equal("Свет", quest.GetProperty("Name").GetString());
        Assert.Equal(3.5, quest.GetProperty("Reward").GetDouble());
        Assert.False(quest.TryGetProperty("Ignored", out _));
        Assert.Throws<ArgumentException>(() => api.RegisterStructure<Quest>("quest"));
        Assert.Throws<ArgumentException>(() => api.CreateStructure("absent", "{}"));
        Assert.Throws<ArgumentException>(() => api.CreateStructure("quest", "null"));
        Assert.Throws<JsonException>(() => api.CreateStructure("quest", "{\"Reward\":\"not a number\"}"));
    }

    [Fact]
    public void HooksEventsAndInput_KeepRegistrationOrderAndStopConsumedInput()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var context = new TestContext(api); var observed = new List<string>();
        api.BeforeExecute("*", value => { value.Text += " universal"; observed.Add("all"); });
        api.BeforeExecute("Dialogue", value => { value.Text += " dialogue"; observed.Add(value.NodeId); });
        api.BeforeExecute("SetFlag", _ => observed.Add("wrong-kind"));
        var execution = new PluginExecution(context, "Dialogue", "node-7", "original"); api.Hook(execution);
        Assert.Equal("original universal dialogue", execution.Text);
        Assert.Equal(new[] { "all", "node-7" }, observed);
        api.On("on_save", value => observed.Add(value.Data.GetProperty("slot").GetString()!));
        api.On("on_load", _ => observed.Add("wrong-event"));
        api.Publish("on_save", JsonSerializer.SerializeToElement(new { slot = "manual" }), context);
        Assert.Equal(new[] { "all", "node-7", "manual" }, observed);
        api.InterceptInput(value => { if (value.Key == "Space") { value.Handled = true; observed.Add(value.Kind); } });
        api.InterceptInput(_ => observed.Add("fallthrough"));
        Assert.True(api.Input(new("key", "Space")));
        Assert.Equal("key", observed[^1]);
        Assert.False(api.Input(new("key", "Escape")));
        Assert.Equal("fallthrough", observed[^1]);
    }

    [Fact]
    public void Validate_UsesDetachedGraphPerValidator_AndReturnsNodeDiagnostics()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var graph = VisualScript.Create();
        api.RegisterValidator(copy => { copy.Nodes[1].Text = "mutated"; return [new("Пустое здоровье", "dialogue", true)]; });
        api.RegisterValidator(copy => [new(copy.Nodes[1].Text, copy.Nodes[1].Id)]);
        var result = api.Validate(graph).ToArray();
        Assert.Equal("Добро пожаловать!", graph.Nodes[1].Text);
        Assert.Equal(new PluginDiagnostic("Пустое здоровье", "dialogue", true), result[0]);
        Assert.Equal(new PluginDiagnostic("Добро пожаловать!", "dialogue"), result[1]);
    }

    [Fact]
    public void Validate_LimitsEachExtensionReportWithoutLosingOtherValidatorResults()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        api.RegisterValidator(_ => Enumerable.Range(0, 1001).Select(index => new PluginDiagnostic("warning " + index)));
        api.RegisterValidator(_ => [new("second validator")]);
        var result = api.Validate(VisualScript.Create()).ToArray();
        Assert.Equal(1001, result.Length);
        Assert.Equal("warning 999", result[999].Message);
        Assert.Equal("second validator", result[1000].Message);
        Assert.DoesNotContain(result, diagnostic => diagnostic.Message == "warning 1000");
    }

    [Fact]
    public void DialogueFormatRegistration_RejectsInvalidExtensionsAndDuplicates_WithoutReplacingCallbacks()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var format = new PluginDialogueFormat(".translation", dialogues => JsonSerializer.SerializeToUtf8Bytes(dialogues),
            bytes => JsonSerializer.Deserialize<PluginDialogue[]>(bytes)!);
        api.RegisterDialogueFormat(format);
        Assert.Throws<ArgumentException>(() => api.RegisterDialogueFormat(format));
        Assert.Throws<ArgumentException>(() => api.RegisterDialogueFormat(format with { Extension = "../wrong" }));
        Assert.Throws<ArgumentException>(() => api.RegisterDialogueFormat(format with { Extension = ".UPPER" }));
        var registered = Assert.Single(api.formats);
        byte[] data = registered.Export([new("node-1", "Анна", "Привет\n\"мир\"")]);
        var imported = Assert.Single(registered.Import(data));
        Assert.Equal(new PluginDialogue("node-1", "Анна", "Привет\n\"мир\""), imported);
    }

    [Fact]
    public void PaletteAndUiRegistrations_RejectCollisionsAndInvalidDescriptors()
    {
        using var fixture = new TempProject(); using var api = new PluginApi(fixture.ProjectPath);
        var palette = new PluginPalette("custom-neon", "Неон", "#102030", "#203040", "#FFFFFF", "#FF00AA", "#999999", new() { ["logic"] = "#00FFEE" });
        api.RegisterPalette(palette);
        Assert.Throws<ArgumentException>(() => api.RegisterPalette(palette));
        Assert.Throws<ArgumentException>(() => api.RegisterPalette(palette with { Id = "kne" }));
        Assert.Throws<ArgumentException>(() => api.RegisterPalette(palette with { Id = "bad", Groups = new() { ["logic"] = "red" } }));
        var command = new PluginEditorCommand("generate", "Создать", _ => { }); api.RegisterCommand(command);
        Assert.Throws<ArgumentException>(() => api.RegisterCommand(command));
        Assert.Throws<ArgumentException>(() => api.RegisterCommand(command with { Id = "wrong", Location = "absent" }));
        var tab = new PluginEditorTab("inventory", "Инвентарь", () => null!); api.RegisterTab(tab);
        Assert.Throws<ArgumentException>(() => api.RegisterTab(tab));
        var screen = new PluginGameScreen("quest-overlay", _ => null!); api.RegisterScreen(screen);
        Assert.Throws<ArgumentException>(() => api.RegisterScreen(screen));
        Assert.Equal("custom-neon", Assert.Single(api.Palettes).Id);
        Assert.Single(api.commands); Assert.Single(api.tabs); Assert.Single(api.screens);
    }

    [Fact]
    public void Dispose_ReleasesOwnedResourcesInReverseOrderOnce_AndRejectsFurtherRegistrations()
    {
        using var fixture = new TempProject(); var api = new PluginApi(fixture.ProjectPath); var order = new List<int>();
        api.Own(new Owned(() => order.Add(1))); api.Own(new Owned(() => order.Add(2)));
        api.RegisterBlock(new("custom", "Блок", "logic"), (_, _) => 1);
        api.Dispose(); api.Dispose();
        Assert.Equal(new[] { 2, 1 }, order); Assert.Empty(api.Blocks);
        Assert.Throws<ObjectDisposedException>(() => api.RegisterGroup(new("late", "Поздно", "#112233")));
        Assert.Throws<ObjectDisposedException>(() => api.Execute("custom", new TestContext(api), "{}"));
    }

    public sealed record Quest(string Name, double Reward);
    private sealed class Owned(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    private sealed class TestContext(PluginApi api) : IPluginContext
    {
        public PluginApi Api => api;
        public string ProjectDirectory => api.ProjectDirectory;
        public CancellationToken CancellationToken => default;
        public object? Get(string name) => null;
        public void Set(string name, object? value) { }
        public void Log(string message) { }
    }
}
