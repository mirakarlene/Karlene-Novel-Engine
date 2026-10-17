using Xunit;

namespace KNE.Tests;

public sealed class TextInputRecognitionTests
{
    [Fact]
    public async Task FrameCatalog_ManualInputCallsOnSameLine_HaveDistinctVisibleFrameIds()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\ntext_input('{}'); text_input('{}')\nend");
        var project = await fixture.LoadAsync();
        var catalog = FrameCatalog.Load(project);
        var entries = catalog.Entries.Where(entry => entry.File == "game/scenes/main.krl" && entry.HasText).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry => { Assert.Equal(2, entry.Line); Assert.Equal("main", entry.Function); Assert.True(entry.Identity.GlobalFrameId > 0); });
        Assert.NotEqual(entries[0].Identity, entries[1].Identity);
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(entries[0].Identity, runtime.Story.Identity);
        await runtime.SubmitInputAsync("first");
        Assert.Equal("input", runtime.Story.Kind);
        Assert.Equal(entries[1].Identity, runtime.Story.Identity);
    }

    [Fact]
    public async Task FrameCatalog_TextInputGraphBlock_IsVisibleFrameWithItsOwnBlockIdentity()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function legacy() end");
        var graph = VisualScript.Create();
        var input = graph.Nodes.Single(node => node.Id == "dialogue");
        input.Kind = ScriptNodeKind.TextInput;
        input.Input = new TextInputSettings();
        fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync();
        project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        var catalog = FrameCatalog.Load(project);
        var entry = Assert.Single(catalog.Entries, item => item.File == "game/scenes/main.knegraph" && item.NodeId == input.Id);
        Assert.True(entry.HasText);
        Assert.Equal(input.Id, entry.Identity.BlockId);
        Assert.True(entry.Identity.SceneFrameId > 0);
        Assert.True(entry.Identity.GlobalFrameId > 0);
        Assert.Equal(entry.Identity, catalog.Resolve(entry.File, entry.Line, input.Id));
        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("input", runtime.Story.Kind);
        Assert.Equal(entry.Identity, runtime.Story.Identity);
    }

    [Fact]
    public async Task Analyzer_InputCallIsYielding_ButQuotedInputAndTightLoopsStillWarn()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                while true do
                    text_input('{}')
                end
            end
            function tight()
                while true do local n = 1 end
            end
            function quoted()
                while true do local text = "text_input('{}')" end
            end
            """);
        var analysis = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(0, analysis.ErrorCount);
        Assert.Equal(new int?[] { 7, 10 }, analysis.Findings.Where(item => item.File == "game/scenes/main.krl" && item.Code == "KNE401").Select(item => item.Line));
    }

    [Fact]
    public void LuaSyntax_InputApiIsFunctionToken_WithoutTokenizingQuotedOrCommentedCalls()
    {
        const string source = "local answer = text_input('{}'); local text = \"text_input('{}')\" -- text_input('{}')";
        var tokens = LuaSyntax.Parse(source);
        var input = Assert.Single(tokens, token => source.Substring(token.Start, token.Length) == "text_input");
        Assert.Equal(LuaTokenKind.Function, input.Kind);
        Assert.Equal(source.IndexOf("text_input", StringComparison.Ordinal), input.Start);
        Assert.Contains(tokens, token => token.Kind == LuaTokenKind.String && source.Substring(token.Start, token.Length).Contains("text_input", StringComparison.Ordinal));
        Assert.Contains(tokens, token => token.Kind == LuaTokenKind.Comment && source.Substring(token.Start, token.Length).Contains("text_input", StringComparison.Ordinal));
    }
}
