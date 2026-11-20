using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class VisualLuaTests
{
    [Fact]
    public void ImportSeparatesStructuredFlowAndKeepsUnsupportedSegmentsAndTriviaExactly()
    {
        const string source = "--[=[ keep comment ]=]\r\nlocal seed <const> = 5\r\n" +
            "function main()\r\n  local callback = function(x) return x // 2 end\r\n" +
            "  for i = 1, seed do\r\n    if i == 2 then print(i) else print(callback(i)) end\r\n  end\r\n" +
            "  local n = seed\r\n  repeat n = n - 1 until n == 0\r\n  ::marker::\r\n  return\r\nend\r\n-- tail";
        var graph = VisualScript.Parse(VisualLua.ParseSource(source).Serialize());

        Assert.True(graph.IsVisualLua);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "function" && node.True != null);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "anonymous" && node.True != null);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "for_numeric" && node.True != null);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "if" && node.True != null && node.False != null);
        Assert.Contains(graph.Nodes, node => node.Lua is { Operation: "raw", Code: var code } && code.Contains("repeat", StringComparison.Ordinal));
        Assert.Contains(graph.Nodes, node => node.Lua is { Operation: "raw", Code: var code } && code.Contains("::marker::", StringComparison.Ordinal));
        Assert.Equal(source, graph.Compile());
    }

    [Theory]
    [InlineData("local x = {", "raw")]
    [InlineData("return '🌸'  -- keep", "return")]
    [InlineData("if a then f() elseif b then g() else h() end\nprint(1)", "if")]
    public void DraftsAndFallbackSegmentsRoundTrip(string source, string operation)
    {
        var graph = VisualLua.ParseSource(source);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == operation);
        Assert.Equal(source, VisualScript.Parse(graph.Serialize()).Compile());
    }

    [Fact]
    public void SourceCommentsAreEditableDetachedNodesAndStringsKeepTheirDashes()
    {
        const string source = "-- first\r\nlocal label = '-- a string'\r\nlocal total = 1 --[=[ inline ]=] + 2\r\n-- tail";
        var graph = VisualScript.Parse(VisualLua.ParseSource(source).Serialize());
        var comments = graph.Nodes.Where(node => node.Kind == ScriptNodeKind.Comment).ToArray();
        Assert.Equal(3, comments.Length);
        Assert.All(comments, node => { Assert.Equal("lua_raw", node.Category); Assert.Empty(node.Outputs()); });
        Assert.Equal(source, graph.Compile());
        comments[0].Text = " revised";
        graph.Nodes.Remove(comments[1]);
        Assert.Equal("-- revised\r\nlocal label = '-- a string'\r\nlocal total = 1   + 2\r\n-- tail", graph.Compile());
    }

    [Fact]
    public async Task ConversionPreservesLayoutAndStableNodesAfterCodeEdits()
    {
        using var fixture = new TempProject();
        const string original = "-- note\nif false then print('left') else print('right') end\nreturn 1";
        string lua = fixture.Write("layout.lua", original);
        var document = await ScriptDocument.OpenAsync(fixture.ProjectPath, lua);
        string path = await document.ConvertLuaAsync();
        var visual = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
        var node = Assert.Single(visual.Graph!.Nodes, node => node.Lua?.Operation == "if");
        node.X = -200; node.Y = 1320; node.Width = 310; node.Height = 190;
        await visual.ConvertLuaAsync();
        Assert.Equal(original, await File.ReadAllTextAsync(lua));
        document = await ScriptDocument.OpenAsync(fixture.ProjectPath, lua);
        visual = await ScriptDocument.OpenAsync(fixture.ProjectPath, await document.ConvertLuaAsync());
        Assert.Equal(node.Id, Assert.Single(visual.Graph!.Nodes, candidate => candidate.Lua?.Operation == "if").Id);
        await visual.ConvertLuaAsync();
        document = await ScriptDocument.OpenAsync(fixture.ProjectPath, lua);
        document.Code = original.Replace("return 1", "return 2", StringComparison.Ordinal);
        visual = await ScriptDocument.OpenAsync(fixture.ProjectPath, await document.ConvertLuaAsync());
        var retained = Assert.Single(visual.Graph!.Nodes, candidate => candidate.Lua?.Operation == "if");
        Assert.Equal((node.X, node.Y, node.Width, node.Height), (retained.X, retained.Y, retained.Width, retained.Height));
        Assert.EndsWith("return 2", visual.Graph.Compile());
        Assert.DoesNotContain((await fixture.LoadAsync()).Files, file => file.FilePath.Contains("vlua-layout", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DebugRunsDraftAndDependenciesInCopyWithoutStartingMain()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() error('main must not run') end\nfunction chapter() state.chapter = 4 end");
        fixture.Write("game/scripts/module.vlua", VisualLua.ParseSource("return { value = 42 }").Serialize());
        fixture.Write("game/scripts/test.vlua", VisualLua.ParseSource("error('draft was not used')").Serialize());
        var report = await VisualLuaDebug.RunAsync(fixture.ProjectPath, "game/scripts/test.vlua", """
            local dependency = require('module')
            call_scene('chapter')
            kne.write_file('debug.txt', 'copy only')
            print('dependency = ' .. dependency.value .. ':' .. state.chapter)
            """);
        Assert.True(report.Status == "Скрипт выполнен.", report.Status + Environment.NewLine + string.Join(Environment.NewLine, report.Output));
        Assert.Contains("dependency = 42:4", report.Output);
        Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "debug.txt")));
    }

    [Fact]
    public Task LuaCategoryHoverUsesCatalogAndRespondsToAccessibilityModes() => WorkspaceTabsTests.Sta(() =>
    {
        var label = new ListBoxItem();
        foreach (var category in VisualLuaCatalog.Groups)
        {
            CategoryHover.SetGroup(label, category.Id);
            foreach (var mode in BlockAccessibility.Modes)
            {
                BlockAccessibility.SetMode(label, mode.Id);
                var expected = BlockAccessibility.Presentation(category.Id, mode.Id);
                Assert.Equal(expected.Color, Assert.IsType<SolidColorBrush>(CategoryHover.GetColor(label)).Color);
                Assert.Equal(expected.Foreground, Assert.IsType<SolidColorBrush>(CategoryHover.GetInk(label)).Color);
                Assert.Equal(mode.Id != "normal", expected.Badges);
            }
            Assert.Equal((Color)ColorConverter.ConvertFromString(category.Color), BlockAccessibility.Presentation(category.Id, "normal").Color);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void VmDefaultPlacementFitsOversizedWindowsToTheEditorMonitor()
    {
        var workArea = new Rect(-1920, 30, 1920, 1010);
        Assert.Equal(workArea, GameWindow.CenterVmBounds(workArea, 2560, 1440));
        Assert.Equal(new Rect(-1280, 295, 640, 480), GameWindow.CenterVmBounds(workArea, 640, 480));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionUpdatesInitializerAndLoadsModulesWithExistingLuaReferences(bool worker)
    {
        using var fixture = new TempProject();
        const string initSource = "init(-5)\nstate.initialized = true";
        string initializer = fixture.Write("game/scripts/gamelogic.lua", initSource);
        string helper = fixture.Write("game/scripts/helper.lua", "return { value = 42 }");
        fixture.Write("game/scenes/main.krl", """
            function main()
              local module = require('helper')
              local from_file = assert(loadfile('game/scripts/helper.lua'))()
              d(nil, tostring(state.initialized) .. ':' .. module.value .. ':' .. from_file.value)
            end
            """);
        var init = await ScriptDocument.OpenAsync(fixture.ProjectPath, initializer);
        string converted = await init.ConvertLuaAsync();
        var module = await ScriptDocument.OpenAsync(fixture.ProjectPath, helper);
        string convertedModule = await module.ConvertLuaAsync();
        var project = await fixture.LoadAsync();

        Assert.False(File.Exists(initializer));
        Assert.False(File.Exists(helper));
        Assert.Equal(converted, project.InitScriptPath);
        Assert.True(File.Exists(convertedModule));
        Assert.Equal(-5, Assert.Single(GameScripts.Load(project).Initialization).Priority);
        await using (IGameSession runtime = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true))
        {
            await runtime.StartAsync();
            Assert.Equal("true:42:42", runtime.Story.Text);
        }
        var visual = await ScriptDocument.OpenAsync(fixture.ProjectPath, converted);
        Assert.Equal(initializer, await visual.ConvertLuaAsync());
        Assert.Equal(initSource, await File.ReadAllTextAsync(initializer));
        Assert.False(File.Exists(converted));
        Assert.Equal(initializer, (await fixture.LoadAsync()).InitScriptPath);
    }

    [Fact]
    public async Task ConversionKeepsBothFilesOnConflictAndUsesUnsavedEditorTextOnSuccess()
    {
        using var fixture = new TempProject();
        string source = fixture.Write("utility.lua", "return 1");
        string target = fixture.Write("utility.vlua", "existing");
        var document = await ScriptDocument.OpenAsync(fixture.ProjectPath, source);
        document.Code = "return 2";
        await Assert.ThrowsAsync<IOException>(() => document.ConvertLuaAsync());
        Assert.Equal("return 1", await File.ReadAllTextAsync(source));
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        File.Delete(target);

        Assert.Equal(target, await document.ConvertLuaAsync());
        var reopened = await ScriptDocument.OpenAsync(fixture.ProjectPath, target);
        Assert.Equal("return 2", reopened.Graph!.Compile());
    }

    [Fact]
    public async Task LoopPortsSupportContinueWithoutRunningTheRestOfTheIteration()
    {
        var graph = VisualLua.ParseSource("function main()\n local count = 0\n for i=1,3 do count=count+1 end\n d(nil,count)\nend");
        var loop = Assert.Single(graph.Nodes, node => node.Lua?.Operation == "for_numeric");
        var continuation = VisualLuaCatalog.Create("continue");
        continuation.Next = loop.True;
        loop.SetOutput(0, continuation.Id);
        graph.Nodes.Add(continuation);
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function unused() end");
        fixture.Write("chapter.vlua", graph.Serialize());
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.Equal("0", runtime.Story.Text);
    }

    [Fact]
    public async Task VisualLuaCallsKrlSynchronouslyAndFromAnAsyncBranch()
    {
        const string lua = """
            function main()
                call_scene('first')
                local task = kne_async({function() call_scene('second') end})
                kne_join(task)
                d(nil, state.steps)
            end
            """;
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function first() state.steps = "first" end
            function second() state.steps = state.steps .. ":second" end
            """);
        var graph = VisualLua.ParseSource(lua);
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "krl_sync");
        Assert.Contains(graph.Nodes, node => node.Lua?.Operation == "krl_async");
        fixture.Write("story.vlua", graph.Serialize());
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.Equal("first:second", runtime.Story.Text);
    }

    [Fact]
    public async Task ConversionPreservesBomAndLineEndingsOnDisk()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("unicode.lua", "");
        byte[] original = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("-- тест\r\nreturn '🎮'\r\n")];
        await File.WriteAllBytesAsync(path, original);
        var plain = await ScriptDocument.OpenAsync(fixture.ProjectPath, path);
        var visual = await ScriptDocument.OpenAsync(fixture.ProjectPath, await plain.ConvertLuaAsync());
        await visual.ConvertLuaAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StorageFallbackIsOptInAndUsesOnlyAWritableDestination()
    {
        using var fixture = new TempProject();
        string blocked = fixture.Write("blocked-directory", "preserve");
        string fallback = Path.Combine(fixture.ProjectPath, "profile", "saves");
        var readOnly = GameStartupDiagnostics.SelectStorage(blocked, null);
        Assert.False(readOnly.Effective.Writable);
        Assert.Equal(blocked, readOnly.Effective.Directory);
        Assert.False(Directory.Exists(fallback));

        var alternate = GameStartupDiagnostics.SelectStorage(blocked, fallback);
        Assert.False(alternate.Requested.Writable);
        Assert.True(alternate.Effective.Writable);
        Assert.Equal(fallback, alternate.Effective.Directory);
        Assert.Equal("preserve", await File.ReadAllTextAsync(blocked));
        Assert.Empty(Directory.EnumerateFiles(fallback));

        var project = await fixture.LoadAsync();
        await WorkspaceTabsTests.Sta(async () =>
        {
            var settings = new ProjectParametersWindow(project);
            try
            {
                Assert.Equal("read_only", settings.UnavailableSavesInput.SelectedValue);
                settings.UnavailableSavesInput.SelectedValue = "appdata";
                await settings.SaveAsync();
            }
            finally { settings.Close(); }
        });
        Assert.Equal("appdata", PerformanceSettingsStore.LoadProject(project.Path).UnavailableSaves);
    }

    [Fact]
    public Task ModeSwitchChangesExtensionInTheOpenEditor() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        string file = fixture.Write("helper.lua", "local count = 3\nreturn count");
        var editor = new ScriptEditor();
        await editor.OpenAsync(fixture.ProjectPath, file);
        var conversion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.LuaFileConverted += (_, _) => conversion.TrySetResult();
        ((RadioButton)editor.FindName("GraphMode")).IsChecked = true;
        await conversion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(editor.Document!.Graph!.IsVisualLua);
        Assert.True(editor.CanAddBlock(ScriptNodeKind.LuaBlock));
        Assert.False(editor.CanAddBlock(ScriptNodeKind.Dialogue));
        Assert.Equal(Visibility.Visible, ((Button)editor.FindName("DebugLuaButton")).Visibility);
        foreach (string name in new[] { "RunButton", "TestSceneButton", "MainSceneButton", "ExportButton" })
            Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName(name)).Visibility);
        Assert.Equal(Path.ChangeExtension(file, ".vlua"), editor.Document.FilePath);
        conversion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ((RadioButton)editor.FindName("CodeMode")).IsChecked = true;
        await conversion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(file, editor.Document!.FilePath);
        Assert.Equal(Visibility.Collapsed, ((Button)editor.FindName("DebugLuaButton")).Visibility);
        Assert.Equal("local count = 3\nreturn count", await File.ReadAllTextAsync(file));
    });
}
