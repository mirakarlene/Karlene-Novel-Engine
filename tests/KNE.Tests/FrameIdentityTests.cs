using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class FrameIdentityTests
{
    [Fact]
    public async Task Registry_UnchangedSynchronizationPreservesTimestamp_AndConcurrentSynchronizationsProduceOneConsistentRegistry()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var catalogs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => FrameCatalog.SynchronizeAsync(project)));
        foreach (var catalog in catalogs) Assert.Equal(catalogs[0].Entries, catalog.Entries);
        string metadata = Path.Combine(project.Path, "engine/frame-ids.json");
        string bytes = File.ReadAllText(metadata);
        var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(metadata, timestamp);
        await FrameCatalog.SynchronizeAsync(project);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(metadata));
        Assert.Equal(bytes, File.ReadAllText(metadata));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(metadata)!, "frame-ids.json.*.tmp"));
    }

    [Fact]
    public async Task Registry_LineShiftsAndWhitespacePreserveIds_AndReadOnlyLoadNeverCreatesMetadata()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n d(nil, 'first')\n d(nil, 'second')\nend");
        var project = await fixture.LoadAsync();
        string metadata = Path.Combine(project.Path, "engine", "frame-ids.json");
        var initial = FrameCatalog.Load(project);
        Assert.False(File.Exists(metadata));
        var saved = await FrameCatalog.SynchronizeAsync(project);
        fixture.Write("game/scenes/main.krl", "-- an inserted comment\n\nfunction main()\n    d( nil , 'first' ) -- trailing\n\n d(nil, 'second')\nend");
        var shifted = FrameCatalog.Load(project);
        var before = saved.Entries.Where(entry => entry.File == "game/scenes/main.krl").ToArray();
        var after = shifted.Entries.Where(entry => entry.File == "game/scenes/main.krl").ToArray();
        Assert.Equal(before.Select(entry => entry.Identity), after.Select(entry => entry.Identity));
        Assert.Equal(new[] { 4, 6 }, after.Where(entry => entry.HasText).Select(entry => entry.Line));
        Assert.Equal(initial.Entries.Select(entry => entry.Identity), saved.Entries.Select(entry => entry.Identity));
        Assert.DoesNotContain("inserted comment", File.ReadAllText(metadata));
    }

    [Fact]
    public async Task Registry_RemovingLargeBranch_ReusesSmallestFreeSceneAndGameIds_WithoutRenumberingSurvivors()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        for (int index = 0; index < 100; index++)
            graph.Nodes.Insert(1 + index, new ScriptNode { Id = "branch_" + index, Kind = ScriptNodeKind.Dialogue, Text = "removed " + index });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        var before = await FrameCatalog.SynchronizeAsync(project);
        var removed = before.Entries.Where(entry => entry.NodeId?.StartsWith("branch_", StringComparison.Ordinal) == true).ToArray();
        var kept = before.Entries.Where(entry => entry.NodeId is "start" or "dialogue" or "end").ToArray();
        graph.Nodes.RemoveAll(node => node.Id.StartsWith("branch_", StringComparison.Ordinal));
        graph.Nodes.Add(new() { Id = "new", Kind = ScriptNodeKind.Dialogue, Text = "new" });
        graph.Nodes.Reverse(); // Layout and JSON storage order do not rename existing nodes.
        fixture.Write("game/scenes/visual.knegraph", graph.Serialize());
        var after = await FrameCatalog.SynchronizeAsync(project);
        var added = Assert.Single(after.Entries, entry => entry.NodeId == "new");
        Assert.Equal(removed.Min(entry => entry.Identity.SceneFrameId), added.Identity.SceneFrameId);
        Assert.Equal(removed.Min(entry => entry.Identity.GlobalFrameId), added.Identity.GlobalFrameId);
        foreach (var entry in kept) Assert.Equal(entry.Identity, after.Entries.Single(next => next.Key == entry.Key).Identity);
        Assert.DoesNotContain(after.Entries, entry => entry.NodeId?.StartsWith("branch_", StringComparison.Ordinal) == true);
        Assert.Equal(after.Entries.Count, after.Entries.Select(entry => entry.Identity.GlobalFrameId).Distinct().Count());
        Assert.Equal(after.Entries.Count, FrameCatalog.Load(project).Entries.Count);
    }

    [Fact]
    public async Task Registry_IdenticalNodeIdsInDifferentScenes_HaveIndependentLocalIdsAndDistinctGlobalIds()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        graph.FunctionName = "chapter";
        fixture.Write("game/scenes/second.knegraph", graph.Serialize());
        var catalog = await FrameCatalog.SynchronizeAsync(project);
        var entries = catalog.Entries.Where(entry => entry.NodeId == "dialogue").ToArray();
        Assert.Equal(2, entries.Length);
        Assert.NotEqual(entries[0].Key, entries[1].Key);
        Assert.NotEqual(entries[0].Identity.Scene, entries[1].Identity.Scene);
        Assert.Equal(entries[0].Identity.SceneFrameId, entries[1].Identity.SceneFrameId);
        Assert.NotEqual(entries[0].Identity.GlobalFrameId, entries[1].Identity.GlobalFrameId);
        Assert.Equal(entries.Select(entry => entry.Identity), FrameCatalog.Load(project).Entries.Where(entry => entry.NodeId == "dialogue").Select(entry => entry.Identity));
    }

    [Fact]
    public async Task Registry_DraftWithoutMainStillReceivesIds_AndCancelledSyncLeavesExistingRegistryUntouched()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function chapter() d(nil, 'draft') end");
        var project = await fixture.LoadAsync();
        var result = await FrameCatalog.SynchronizeAsync(project);
        Assert.Contains(result.Entries, entry => entry.Function == "chapter" && entry.HasText && entry.Identity.GlobalFrameId > 0);
        string metadata = Path.Combine(project.Path, "engine/frame-ids.json");
        string original = File.ReadAllText(metadata);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FrameCatalog.SynchronizeAsync(project, cancellation.Token));
        Assert.Equal(original, File.ReadAllText(metadata));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate-global")]
    [InlineData("duplicate-local")]
    [InlineData("zero")]
    [InlineData("null")]
    [InlineData("json")]
    public async Task Registry_RejectsCorruptOrCollidingIds_InsteadOfSilentlyReassigning(string corruption)
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        var entries = FrameCatalog.Load(project).Entries.ToArray();
        if (corruption == "duplicate-global") entries[1] = entries[1] with { Identity = entries[1].Identity with { GlobalFrameId = entries[0].Identity.GlobalFrameId } };
        if (corruption == "duplicate-local")
        {
            int next = Array.FindIndex(entries, 1, entry => entry.Identity.Scene == entries[0].Identity.Scene);
            entries[next] = entries[next] with { Identity = entries[next].Identity with { SceneFrameId = entries[0].Identity.SceneFrameId } };
        }
        if (corruption == "zero") entries[0] = entries[0] with { Identity = entries[0].Identity with { GlobalFrameId = 0 } };
        string json = corruption == "json" ? "{" : JsonSerializer.Serialize(new { Version = corruption == "version" ? 2 : 1,
            Entries = corruption == "null" ? null : entries });
        fixture.Write("engine/frame-ids.json", json);
        var error = Assert.Throws<InvalidDataException>(() => FrameCatalog.Load(project));
        Assert.Contains("реестр", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, File.ReadAllText(Path.Combine(project.Path, "engine/frame-ids.json")));
    }

    [Fact]
    public async Task Runtime_TwoDialogueCallsOnOneLine_HaveDistinctStableIds_AndLegacyCheckpointSignaturesStillRestore()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'first'); d(nil, 'second') end");
        var project = await fixture.LoadAsync();
        var registry = await FrameCatalog.SynchronizeAsync(project);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        var first = runtime.Story.Identity;
        Assert.NotNull(first);
        await runtime.AdvanceAsync();
        var second = runtime.Story.Identity;
        Assert.NotNull(second);
        Assert.NotEqual(first.GlobalFrameId, second.GlobalFrameId);
        Assert.NotEqual(first.SceneFrameId, second.SceneFrameId);
        Assert.Equal(2, registry.Entries.Count(entry => entry.File == "game/scenes/main.krl" && entry.HasText));
        var checkpoint = await runtime.GetCheckpointAsync();
        const string legacySignature = "{\"Kind\":\"dialogue\",\"Speaker\":\"\",\"Text\":\"first\",\"Options\":[],\"Completed\":false}";
        Assert.Equal(legacySignature, Assert.Single(checkpoint.History).ExpectedFrame);
        await using var restored = new LuaRuntime(project, _ => { }, interactive: true);
        await restored.RestoreAsync(checkpoint with { Frame = checkpoint.Frame with { Identity = null } });
        Assert.Equal("second", restored.Story.Text);
        Assert.Equal(second, restored.Story.Identity);
    }

    [Fact]
    public async Task Runtime_IdenticalDialogueCallsAndChoice_OnOneLineEachReceiveTheirOwnId()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil,'same'); d(nil,'same'); choice('pick',{'only'}) end");
        var project = await fixture.LoadAsync();
        var entries = FrameCatalog.Load(project).Entries.Where(entry => entry.File == "game/scenes/main.krl" && entry.HasText).ToArray();
        Assert.Equal(3, entries.Length);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(entries[0].Identity, runtime.Story.Identity);
        await runtime.AdvanceAsync();
        Assert.Equal(entries[1].Identity, runtime.Story.Identity);
        await runtime.AdvanceAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal(entries[2].Identity, runtime.Story.Identity);
        Assert.Equal(3, entries.Select(entry => entry.Identity.GlobalFrameId).Distinct().Count());
    }

    [Fact]
    public async Task Registry_UnsavedGraphOverrideIncludesNewBlocksWithoutWritingSourcesOrRegistry_AndDoesNotTrustForgedNodeContext()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        var saved = await FrameCatalog.SynchronizeAsync(project);
        string metadata = File.ReadAllText(Path.Combine(project.Path, "engine/frame-ids.json"));
        string original = File.ReadAllText(Path.Combine(project.Path, "game/scenes/visual.knegraph"));
        graph.Nodes[1].Next = "draft-only";
        graph.Nodes.Add(new() { Id = "draft-only", Kind = ScriptNodeKind.Dialogue, Text = "draft", Next = "end" });
        var source = new EditorPreviewSource("game/scenes/visual.knegraph", graph.Compile(true));
        var catalog = FrameCatalog.Load(project, editorSource: source);
        var added = Assert.Single(catalog.Entries, entry => entry.NodeId == "draft-only");
        Assert.DoesNotContain(saved.Entries, entry => entry.NodeId == "draft-only");
        Assert.Equal(added.Identity, catalog.Resolve(added.File, added.Line, "draft-only", 2));
        Assert.Null(catalog.Resolve(added.File, added.Line, "dialogue", 2));
        Assert.Null(catalog.Resolve("outside.krl", added.Line));
        Assert.Equal(added.Identity, catalog.Resolve(Path.Combine(project.Path, added.File), added.Line, "draft-only", 2));
        Assert.Null(catalog.Resolve(Path.Combine(fixture.Root, "outside.krl"), added.Line));
        Assert.Null(catalog.Resolve(added.File, 0));
        Assert.Equal(metadata, File.ReadAllText(Path.Combine(project.Path, "engine/frame-ids.json")));
        Assert.Equal(original, File.ReadAllText(Path.Combine(project.Path, "game/scenes/visual.knegraph")));
    }

    [Fact]
    public async Task Runtime_NestedChoiceAndDialogueCalls_PreserveArgumentsAndCallerIdentity()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, tostring(choice('pick', {'a','b'}))); d() end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("choice", runtime.Story.Kind);
        var choice = runtime.Story.Identity;
        await runtime.ChooseAsync(2);
        Assert.Equal("2", runtime.Story.Text);
        Assert.NotEqual(choice, runtime.Story.Identity);
        await runtime.AdvanceAsync();
        Assert.Equal("nil", runtime.Story.Text);
        Assert.NotNull(runtime.Story.Identity);
    }

    [Fact]
    public async Task Runtime_ShadowedDialogueFunction_IsCalledWithOriginalArguments()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "local function d(a,b) state.result = a .. b; return 'ok' end\nfunction main() state.returned = d('one','two') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.True(runtime.Story.Completed);
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal("onetwo", state.GetProperty("result").GetString());
        Assert.Equal("ok", state.GetProperty("returned").GetString());
    }

    [Fact]
    public async Task Worker_GraphResourceFailure_ReportsFailingNonTextBlockId_AndMetadataSurvivesTransport()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        graph.Nodes[1].Next = "missing-background";
        graph.Nodes.Add(new() { Id = "missing-background", Kind = ScriptNodeKind.Background, Path = "game/backgrounds/missing.png", Next = "end" });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        var catalog = await FrameCatalog.SynchronizeAsync(project);
        var expected = catalog.Entries.Single(entry => entry.NodeId == "missing-background").Identity;
        await using var session = new ProcessGameSession(project, _ => { });
        await session.StartAsync();
        Assert.Equal("dialogue", session.Story.Identity!.BlockId);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => session.AdvanceAsync());
        var report = GameErrorReport.TryGet(error);
        Assert.NotNull(report);
        Assert.Equal(expected, report.Frame);
        Assert.Contains("ID блока: missing-background", report.ToDisplayText());
        Assert.Contains("ID фрейма в игре: " + expected.GlobalFrameId, report.ToDisplayText());
        Assert.DoesNotContain("frame-ids", report.Message);
    }

    [Fact]
    public async Task Runtime_CodeFailureAfterDialogue_ReportsCurrentCommand_AndChildFailureUsesChildScene()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n d(nil, 'safe')\n call(child)\nend");
        fixture.Write("game/scenes/child.krl", "function child()\n background('game/backgrounds/missing.png')\n d(nil, 'never')\nend");
        var project = await fixture.LoadAsync();
        var catalog = await FrameCatalog.SynchronizeAsync(project);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        var prior = runtime.Story.Identity;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.AdvanceAsync());
        var report = GameErrorReport.TryGet(error)!;
        Assert.Equal("game/scenes/child.krl", report.File);
        Assert.Equal(2, report.Line);
        Assert.Equal(catalog.Resolve(report.File, 2), report.Frame);
        Assert.NotEqual(prior, report.Frame);
        Assert.EndsWith("::child", report.Frame!.Scene);
    }

    [Fact]
    public async Task SceneTest_SelectedFunctionRunsWithoutCommandPauses_AndStopsRootGotoButAllowsChildCallAndGoto()
    {
        using var fixture = new TempProject();
        const string code = """
            function main() d(nil, 'game main') end
            function selected()
                set_bg_color('#123456')
                call(child)
                d(nil, 'selected end')
                goto(escape)
            end
            function child() goto(child_tail) end
            function child_tail() d(nil, 'nested') end
            function escape() error('escaped selected scene') end
            """;
        fixture.Write("game/scenes/main.krl", code);
        var project = await fixture.LoadAsync();
        var source = new EditorPreviewSource("game/scenes/main.krl", code, "selected", StepCommands: false, SceneTest: true);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true, preview: true, editorSource: source);
        await runtime.StartAsync();
        Assert.False(runtime.PreviewPaused);
        Assert.Equal("#123456", runtime.WindowSettings.Background);
        Assert.Equal("nested", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("selected end", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
        await using var ordinary = new LuaRuntime(project, _ => { }, interactive: true);
        await ordinary.StartAsync();
        Assert.Equal("game main", ordinary.Story.Text);
        await ordinary.AdvanceAsync();
        Assert.True(ordinary.Story.Completed);
    }

    [Fact]
    public async Task SceneTest_SeedIsDeterministicAcrossIndependentSessions_AndOrdinaryGameGotoStillContinues()
    {
        using var fixture = new TempProject();
        const string code = "function main() d(nil,tostring(math.random())); goto(tail) end\nfunction tail() d(nil,'tail') end";
        fixture.Write("game/scenes/main.krl", code);
        var project = await fixture.LoadAsync();
        var source = new EditorPreviewSource("game/scenes/main.krl", code, "main", StepCommands: false, SceneTest: true, Seed: 1234);
        await using var first = new LuaRuntime(project, _ => { }, interactive: true, preview: true, editorSource: source);
        await using var second = new LuaRuntime(project, _ => { }, interactive: true, preview: true, editorSource: source);
        await first.StartAsync(); await second.StartAsync();
        Assert.Equal(first.Story.Text, second.Story.Text);
        Assert.Equal(first.Story.Identity, second.Story.Identity);
        await using var ordinary = new LuaRuntime(project, _ => { }, interactive: true);
        await ordinary.StartAsync(); await ordinary.AdvanceAsync();
        Assert.Equal("tail", ordinary.Story.Text);
    }

    [Fact]
    public async Task SceneTest_ProjectCannotForgeCompletionOrDisableBoundaryByChangingGlobalFlag()
    {
        using var fixture = new TempProject();
        const string forged = "function main() coroutine.yield({kind='completed'}) end";
        fixture.Write("game/scenes/main.krl", forged);
        var project = await fixture.LoadAsync();
        await using (var runtime = new LuaRuntime(project, _ => { }, interactive: true, preview: true,
            editorSource: new("game/scenes/main.krl", forged, StepCommands: false, SceneTest: true)))
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
            Assert.Contains("Неизвестная команда", error.Message);
            Assert.False(runtime.Story.Completed);
        }
        const string changedFlag = "kne_scene_test=false\nfunction main() goto(tail) end\nfunction tail() error('escaped') end";
        await using var scene = new LuaRuntime(project, _ => { }, interactive: true, preview: true,
            editorSource: new("game/scenes/main.krl", changedFlag, StepCommands: false, SceneTest: true));
        await scene.StartAsync();
        Assert.True(scene.Story.Completed);
    }

    [Theory]
    [InlineData("missing", "Не найдена функция")]
    [InlineData("nil", "Цель call/goto")]
    [InlineData("42", "Цель call/goto")]
    [InlineData("'game/scenes/file.krl'", "Укажите имя функции")]
    public async Task SceneTest_InvalidRootGotoIsReportedAsError_InsteadOfSuccessfulSceneEnd(string target, string expected)
    {
        using var fixture = new TempProject();
        string code = "function main() goto(" + (target == "missing" ? "'missing'" : target) + ") end";
        fixture.Write("game/scenes/main.krl", code);
        var project = await fixture.LoadAsync();
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true, preview: true,
            editorSource: new("game/scenes/main.krl", code, StepCommands: false, SceneTest: true));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(expected, error.Message);
        Assert.False(runtime.Story.Completed);
        Assert.NotNull(GameErrorReport.TryGet(error)!.Frame);
    }

    [Theory]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    [InlineData(-50000, 50000)]
    public void Graph_SignedCoordinatesBeyondOldCanvasBounds_ArePersistedWithoutChangingExecution(double x, double y)
    {
        var graph = VisualScript.Create(); graph.Nodes[1].X = x; graph.Nodes[1].Y = y;
        var parsed = VisualScript.Parse(graph.Serialize());
        Assert.Equal(x, parsed.Nodes[1].X); Assert.Equal(y, parsed.Nodes[1].Y);
        Assert.Contains("kne_runtime_node(\"dialogue\")", parsed.Compile());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1_000_000_001)]
    [InlineData(1_000_000_001)]
    public void Graph_RejectsNonfiniteOrExcessiveCoordinates(double coordinate)
    {
        var graph = VisualScript.Create(); graph.Nodes[1].Y = coordinate;
        Assert.Throws<InvalidDataException>(() => graph.Serialize());
    }
}
