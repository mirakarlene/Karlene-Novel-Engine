using System.IO;
using System.Text;
using Xunit;

namespace KNE.Tests;

public sealed class SceneRenamingTests
{
    [Fact]
    public void CodeRenameChangesGlobalReferencesAndStaticTargetsWhilePreservingOtherNamespacesAndText()
    {
        const string source = """
            -- chapter and call_scene('chapter') remain a comment
            local description = "chapter"
            function chapter() return 'chapter' end
            function shadow(chapter)
                local values = { chapter = chapter }
                object.chapter(); object:chapter()
                goto chapter; ::chapter::
                return chapter
            end
            function uses()
                local alias = chapter
                local explicit = _G.chapter
                local indexed = _G["chapter"]
                local values = { chapter = chapter }
                call_scene(chapter); call_scene('chapter'); goto_scene([=[chapter]=])
                chapter()
            end
            """;
        string after = SceneRenaming.RenameCode(source, "chapter", "renamed", true);
        Assert.Contains("function renamed() return 'chapter' end", after);
        Assert.Contains("local alias = renamed", after); Assert.Contains("local explicit = _G.renamed", after); Assert.Contains("local indexed = _G[\"renamed\"]", after);
        Assert.Contains("local values = { chapter = renamed }", after); Assert.Contains("\n    renamed()\nend", after.Replace("\r\n", "\n"));
        Assert.Contains("-- chapter and call_scene('chapter') remain a comment", after); Assert.Contains("local description = \"chapter\"", after);
        Assert.Contains("function shadow(chapter)", after); Assert.Contains("local values = { chapter = chapter }", after); Assert.Contains("return chapter", after);
        Assert.Contains("object.chapter(); object:chapter()", after); Assert.Contains("goto chapter; ::chapter::", after);
        var links = SceneNavigationSource.Read(after, LuaSource.Functions(after)).References;
        Assert.Equal(3, links.Count(link => link.Target == "renamed" && link.Kind is "call_scene" or "goto_scene"));
        Assert.DoesNotContain(links, link => link.Target == "chapter" && link.Kind is "call_scene" or "goto_scene");
    }

    [Fact]
    public void CodeRenameUsesTheDeclarationTokenAndHonorsBlockLocalsAndParameters()
    {
        const string source = """
            local literal = "chapter"; chapter(); function chapter() end
            function runner(chapter) chapter() end
            function uses()
                do local chapter = function() end; chapter() end
                chapter()
            end
            """;
        string after = SceneRenaming.RenameCode(source, "chapter", "renamed", true);
        Assert.Contains("local literal = \"chapter\"; renamed(); function renamed() end", after);
        Assert.Contains("function runner(chapter) chapter() end", after);
        Assert.Contains("do local chapter = function() end; chapter() end\n    renamed()", after.Replace("\r\n", "\n"));
        Assert.Equal(new[] { "renamed", "runner", "uses" }, LuaSource.Functions(after).Select(function => function.Name));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BatchRenameAppliesChainsAndSwapsSimultaneouslyAcrossCodeAndGraphReferencesWithoutWriting(bool swap)
    {
        using var fixture = new TempProject();
        string codePath = fixture.Write("chapters.krl", "function alpha() call_scene(beta) end\nfunction beta() goto_scene(alpha) end");
        var graph = VisualScript.Create(); graph.FunctionName = "diagram";
        graph.Nodes.Add(new() { Id = "alpha", Kind = ScriptNodeKind.CallScene, Path = "alpha" });
        graph.Nodes.Add(new() { Id = "beta", Kind = ScriptNodeKind.ConditionalGoto, Path = "beta" });
        graph.Nodes.Add(new() { Id = "jump", Kind = ScriptNodeKind.GotoScene, Path = "alpha" });
        graph.Nodes.Add(new() { Id = "sub", Kind = ScriptNodeKind.Subsystem, Path = "alpha" });
        string graphPath = fixture.Write("diagram.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); var before = project.Files.ToDictionary(file => file.FilePath, file => File.ReadAllBytes(file.FilePath));
        var scenes = SceneDefinition.Read(project); string first = "beta", second = swap ? "alpha" : "gamma";
        var plan = SceneRenaming.Plan(project, new Dictionary<SceneDefinition, string> { [scenes.Single(scene => scene.Function == "alpha")] = first, [scenes.Single(scene => scene.Function == "beta")] = second });
        Assert.Equal(2, plan.Count);
        string code = Encoding.UTF8.GetString(plan[codePath]);
        Assert.Equal($"function {first}() call_scene({second}) end\nfunction {second}() goto_scene({first}) end", code);
        var changedGraph = VisualScript.Parse(Encoding.UTF8.GetString(plan[graphPath]));
        Assert.Equal("diagram", changedGraph.FunctionName); Assert.Equal(first, changedGraph.Nodes.Single(node => node.Id == "alpha").Path);
        Assert.Equal(second, changedGraph.Nodes.Single(node => node.Id == "beta").Path); Assert.Equal(first, changedGraph.Nodes.Single(node => node.Id == "jump").Path);
        Assert.Equal("alpha", changedGraph.Nodes.Single(node => node.Id == "sub").Path);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(before.Keys.Order(), project.Files.Select(file => file.FilePath).Order());
    }

    [Theory]
    [InlineData("alpha", "beta")] [InlineData("alpha", "not a name")] [InlineData("alpha", "end")] [InlineData("main", "new_main")]
    public async Task ConflictingInvalidAndReservedMainNamesAreRejectedBeforeAnySourceChanges(string oldName, string newName)
    {
        using var fixture = new TempProject(); fixture.Write("chapters.krl", "function alpha() end\nfunction beta() end");
        var project = await fixture.LoadAsync(); var before = project.Files.ToDictionary(file => file.FilePath, file => File.ReadAllBytes(file.FilePath));
        var scene = SceneDefinition.Read(project).Single(item => item.Function == oldName);
        Assert.Throws<InvalidDataException>(() => SceneRenaming.Plan(project, new Dictionary<SceneDefinition, string> { [scene] = newName }));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RenamePreservesUtf16ByteOrderAndBomAndDoesNotTouchUnrelatedGraphFormatting(bool bigEndian)
    {
        using var fixture = new TempProject(); string path = fixture.Write("chapter.krl", "placeholder");
        Encoding encoding = bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        File.WriteAllText(path, "-- Кириллица 🌸\r\nfunction chapter() end\r\n", encoding);
        var graph = VisualScript.Create(); graph.FunctionName = "untouched"; string unrelated = fixture.Write("untouched.knegraph", "\n " + graph.Serialize() + "\n");
        var project = await fixture.LoadAsync(); byte[] before = File.ReadAllBytes(path), graphBefore = File.ReadAllBytes(unrelated);
        var scene = SceneDefinition.Read(project).Single(item => item.Function == "chapter"); var plan = SceneRenaming.Plan(project, new Dictionary<SceneDefinition, string> { [scene] = "renamed" });
        byte[] after = Assert.Single(plan).Value;
        Assert.Equal(encoding.GetPreamble(), after[..encoding.GetPreamble().Length]);
        Assert.Equal("-- Кириллица 🌸\r\nfunction renamed() end\r\n", encoding.GetString(after.AsSpan(encoding.GetPreamble().Length)));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Equal(graphBefore, File.ReadAllBytes(unrelated)); Assert.DoesNotContain(unrelated, plan.Keys);
    }

    [Fact]
    public async Task GraphSceneFunctionAndOnlyItsStaticReferenceFieldsAreRenamed()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "chapter";
        graph.Nodes.Single(node => node.Id == "dialogue").Text = "chapter"; string path = fixture.Write("chapter.knegraph", graph.Serialize());
        string references = fixture.Write("references.lua", "function helper() local target=chapter; call_scene('chapter') end");
        var project = await fixture.LoadAsync(); var scene = SceneDefinition.Read(project).Single(item => item.Function == "chapter");
        var plan = SceneRenaming.Plan(project, new Dictionary<SceneDefinition, string> { [scene] = "renamed" });
        var changed = VisualScript.Parse(Encoding.UTF8.GetString(plan[path]));
        Assert.Equal("renamed", changed.FunctionName); Assert.Equal("chapter", changed.Nodes.Single(node => node.Id == "dialogue").Text);
        Assert.Contains("local target=renamed", Encoding.UTF8.GetString(plan[references]));
        Assert.True(File.Exists(path)); Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "renamed.knegraph")));
        Assert.Empty(SceneRenaming.Plan(project, new Dictionary<SceneDefinition, string>()));
    }

    [Theory]
    [InlineData("function uses() local chapter=chapter; chapter() end", "function uses() local chapter=renamed; chapter() end")]
    [InlineData("function uses() local x,chapter=chapter,chapter; chapter(); return x end", "function uses() local x,chapter=renamed,renamed; chapter(); return x end")]
    [InlineData("function uses() for chapter=chapter,chapter+1 do chapter() end; chapter() end", "function uses() for chapter=renamed,renamed+1 do chapter() end; renamed() end")]
    [InlineData("function uses() for chapter,x in pairs(chapter) do chapter() end; chapter() end", "function uses() for chapter,x in pairs(renamed) do chapter() end; renamed() end")]
    [InlineData("function uses() if true then local chapter=function() end; chapter() else chapter() end; chapter() end", "function uses() if true then local chapter=function() end; chapter() else renamed() end; renamed() end")]
    [InlineData("function uses() local chapter=function() end; _G.chapter(); _G[\"chapter\"](); chapter() end", "function uses() local chapter=function() end; _G.renamed(); _G[\"renamed\"](); chapter() end")]
    public void LexicalBindingsBeginAfterInitializersAndStayInsideTheirLoopOrConditionalBranch(string source, string expected)
        => Assert.Equal(expected, SceneRenaming.RenameCode(source, "chapter", "renamed", false));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GlobalLuaHelpersPreventNameCollisionsWhileLocalHelpersDoNot(bool global)
    {
        using var fixture = new TempProject(); string path = fixture.Write("chapter.krl", "function chapter() end"); fixture.Write("helper.lua", (global ? "" : "local ") + "function helper() end");
        var project = await fixture.LoadAsync(); var scene = SceneDefinition.Read(project).Single(item => item.Function == "chapter"); var before = project.Files.ToDictionary(file => file.FilePath, file => File.ReadAllBytes(file.FilePath));
        var names = new Dictionary<SceneDefinition, string> { [scene] = "helper" };
        if (global) Assert.Throws<InvalidDataException>(() => SceneRenaming.Plan(project, names));
        else Assert.Equal("function helper() end", Encoding.UTF8.GetString(SceneRenaming.Plan(project, names)[path]));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
}
