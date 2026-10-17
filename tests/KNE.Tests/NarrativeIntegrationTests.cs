using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class NarrativeIntegrationTests
{
    [Fact]
    public async Task NewReplies_KeepDistinctFrameIdsOnOneLine_AndWorkerRestoresEffectsAndPartialAutoTimer()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() kne_say(nil,'first'); kne_say(nil,'effect',nil,'{\"kind\":\"wave\",\"speed\":2}'); kne_say(nil,'auto',nil,nil,.2,false); d(nil,'last') end");
        var project = await fixture.LoadAsync(); var registry = await FrameCatalog.SynchronizeAsync(project);
        Assert.Equal(4, registry.Entries.Count(entry => entry.File == "game/scenes/main.krl" && entry.HasText));
        await using var worker = new ProcessGameSession(project, _ => { }); await worker.StartAsync();
        var firstId = worker.Story.Identity; Assert.NotNull(firstId);
        await worker.AdvanceAsync(); Assert.Equal("effect", worker.Story.Text); Assert.Equal("wave", worker.Story.TextEffect!.Kind);
        var effectId = worker.Story.Identity; Assert.NotNull(effectId); Assert.NotEqual(firstId.GlobalFrameId, effectId.GlobalFrameId);
        var effectSave = await worker.GetCheckpointAsync();
        await worker.AdvanceAsync(); Assert.Equal("auto", worker.Story.Text); Assert.True(worker.Story.NeedsTick); Assert.False(worker.Story.CanAdvance);
        Assert.Null(worker.Story.TextEffect); Assert.NotEqual(effectId.GlobalFrameId, worker.Story.Identity!.GlobalFrameId);
        await worker.TickAsync(.1); Assert.Equal(.1, worker.Story.RemainingSeconds!.Value, precision: 6);
        var timerSave = await worker.GetCheckpointAsync();
        await using var restored = new ProcessGameSession(project, _ => { }); await restored.RestoreAsync(effectSave);
        Assert.Equal("effect", restored.Story.Text); Assert.Equal(worker.Story.Identity!.Scene, restored.Story.Identity!.Scene);
        Assert.Equal(effectId, restored.Story.Identity); Assert.Equal("wave", restored.Story.TextEffect!.Kind);
        await restored.RestoreAsync(timerSave); Assert.Equal(.1, restored.Story.RemainingSeconds!.Value, precision: 6);
        await restored.TickAsync(.1); Assert.Equal("last", restored.Story.Text); Assert.Null(restored.Story.TextEffect); Assert.False(restored.Story.NeedsTick);
    }

    [Fact]
    public async Task Preview_SeeksPastUnskippableAutoReplyAndLongTextPause_WithoutChangingSourcesOrSaves()
    {
        using var fixture = new TempProject();
        const string source = "function main()\n kne_say(nil,'auto',nil,nil,2,false)\n text_pause(2100,false)\n kne_say(nil,'selected',nil,'{\"kind\":\"rainbow\",\"loop\":false}')\nend";
        string path = fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        string saves = Path.Combine(project.Path, "saves"); var savedFiles = Directory.Exists(saves) ? Directory.GetFiles(saves, "*", SearchOption.AllDirectories) : [];
        await using var preview = new EditorPreview(project, _ => { });
        await preview.SeekAsync(new(path, source, 4, null, false, "main"));
        Assert.True(preview.Reached); Assert.Equal("selected", preview.Story.Text);
        Assert.Equal("rainbow", preview.Story.TextEffect!.Kind); Assert.False(preview.Story.TextEffect.Loop);
        Assert.Equal(source, File.ReadAllText(path)); Assert.Equal(savedFiles, Directory.Exists(saves) ? Directory.GetFiles(saves, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Registry_ClassifiesAutoAndEffectBlocksAsText_AndKeepsInitializationAndPauseNontext()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create();
        graph.Nodes.Add(new() { Id = "auto", Kind = ScriptNodeKind.AutoDialogue, Text = "auto", Next = "end" });
        graph.Nodes.Add(new() { Id = "effect", Kind = ScriptNodeKind.EffectDialogue, Text = "effect", Next = "end" });
        graph.Nodes.Add(new() { Id = "pause", Kind = ScriptNodeKind.TextPause, Next = "end" });
        graph.Nodes.Add(new() { Id = "profile", Kind = ScriptNodeKind.RegisterCharacter, Character = new(), Next = "end" });
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph); var registry = FrameCatalog.Load(project);
        var entries = registry.Entries.Where(entry => entry.File == "game/scenes/visual.knegraph").ToDictionary(entry => entry.NodeId!);
        Assert.True(entries["auto"].HasText); Assert.True(entries["effect"].HasText);
        Assert.False(entries["pause"].HasText); Assert.False(entries["profile"].HasText);
        Assert.Equal(entries.Count, entries.Values.Select(entry => entry.Identity.GlobalFrameId).Distinct().Count());
    }

    [Fact]
    public async Task MissingSelectedProfile_ReportsGraphFileAndOriginalDialogueBlock()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.Nodes.Single(node => node.Id == "dialogue").SpeakerId = "missing";
        await using var worker = new ProcessGameSession(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => worker.StartAsync());
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/visual.knegraph", report.File); Assert.Equal("dialogue", report.Frame!.BlockId);
        Assert.True(report.Line > 0); Assert.Contains("missing", report.Message);
    }

    [Fact]
    public async Task Analyzer_RecognizesNewReplyYieldAndRequiredArguments_WithoutExecutingDeclarations()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function chapter() while true do kne_say(nil,'read me') end end
            function main()
                register_character(nil)
                text_pause(nil,false)
                kne_say(nil,nil)
                call(chapter)
            end
            """);
        var report = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        var invalid = report.Findings.Where(finding => finding.Code == "KNE303").ToArray();
        Assert.Equal(new int?[] { 3, 4, 5 }, invalid.Select(finding => finding.Line));
        Assert.DoesNotContain(report.Findings, finding => finding.File == "game/scenes/main.krl" && finding.Line == 1 && finding.Code == "KNE401");
        Assert.DoesNotContain(report.Findings, finding => finding.Code == "KNE100");
    }
}
