using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectAnalyzerTests
{
    [Fact]
    public async Task Analyze_NewProject_HasNoErrors_AndDoesNotWriteRegistryOrSources()
    {
        using var fixture = new TempProject();
        string main = Path.Combine(fixture.ProjectPath, "game/scenes/main.krl");
        string original = await File.ReadAllTextAsync(main);
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal("Новелла с пробелами", result.ProjectName);
        Assert.Equal(original, await File.ReadAllTextAsync(main));
        Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "engine/frame-ids.json")));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, "reports")));
    }

    [Fact]
    public async Task Analyze_CompilesButNeverExecutesTopLevelProjectCode()
    {
        using var fixture = new TempProject();
        string marker = Path.Combine(fixture.ProjectPath, "MUST-NOT-EXIST.txt").Replace('\\', '/');
        fixture.Write("danger.lua", "local file = io.open('" + marker + "', 'w'); file:write('executed'); file:close()\nerror('PROJECT WAS EXECUTED')\nwhile true do end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.False(File.Exists(marker));
        Assert.Equal(0, result.ErrorCount);
        Assert.Contains(result.Findings, f => f.Code == "KNE401" && f.File == "danger.lua" && f.Line == 3);
        Assert.DoesNotContain(result.Findings, f => f.Message.Contains("PROJECT WAS EXECUTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Analyze_AccumulatesSyntaxErrorsAcrossFiles_EvenWithInvalidConfigurationAndMissingMain()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", "{\n invalid");
        fixture.Write("game/scenes/main.krl", "function helper()\n local broken = )\nend");
        fixture.Write("another.lua", "local other = )");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE003" && f.File == "project.json" && f.Line == 2);
        Assert.Contains(result.Findings, f => f.Code == "KNE100" && f.File == "game/scenes/main.krl" && f.Line == 2);
        Assert.Contains(result.Findings, f => f.Code == "KNE100" && f.File == "another.lua" && f.Line == 1);
        Assert.Contains(result.Findings, f => f.Code == "KNE200");
        Assert.True(result.ErrorCount >= 4);
    }

    [Fact]
    public async Task Analyze_DuplicateGlobalFunctions_ReportsEveryDeclarationAndStillChecksOtherSources()
    {
        using var fixture = new TempProject();
        fixture.Write("other.krl", "function main() end\nfunction helper() end");
        fixture.Write("third.lua", "function helper() end\nfunction broken( end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(2, result.Findings.Count(f => f.Code == "KNE201"));
        Assert.Equal(2, result.Findings.Count(f => f.Code == "KNE202"));
        Assert.Contains(result.Findings, f => f.Code == "KNE100" && f.File == "third.lua");
    }

    [Fact]
    public async Task Analyze_LocalMain_IsNotAValidEntryPoint()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "local function main() d(nil, 'local') end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE200");
        Assert.DoesNotContain(result.Findings, f => f.Code == "KNE100");
    }

    [Fact]
    public async Task Analyze_LiteralResources_UseRealLinesAndIgnoreCommentsStringsMethodsAndDynamicExpressions()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            -- background('fake-comment.png')
            local quoted = "show_character('fake-string.png')"
            local long = [=[ background('fake-long.png') ]=]
            function main()
                object.background('fake-method.png')
                background('game/backgrounds/missing.png')
                show_character(path .. '.png')
                play_music(dynamicPath)
                background(nil)
                d(nil, 'narration')
            end
            """);
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        var missing = Assert.Single(result.Findings, f => f.Code == "KNE302");
        Assert.Equal("game/scenes/main.krl", missing.File);
        Assert.Equal(6, missing.Line);
        Assert.Contains("missing.png", missing.Message);
        Assert.NotNull(missing.Frame);
        Assert.DoesNotContain(result.Findings, f => f.Code == "KNE303");
    }

    [Fact]
    public async Task Analyze_ExistingAndLongStringResourcePaths_AreResolvedInsideProject()
    {
        using var fixture = new TempProject();
        fixture.Write("game/backgrounds/real.png", "placeholder");
        fixture.Write("game/scenes/main.krl", "function main()\n background([=[game/backgrounds/real.png]=])\n play_sound([[game/audio/missing.wav]])\nend");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        var missing = Assert.Single(result.Findings, f => f.Code == "KNE302");
        Assert.Equal(3, missing.Line);
        Assert.Contains("game/audio/missing.wav", missing.Message);
    }

    [Fact]
    public async Task Analyze_PathEscape_IsAnError_AndRequiredNilIsOnlyAWarning()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n background('../outside.png')\n show_character(nil)\n d(nil, nil)\nend");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE204" && f.Line == 2 && f.Severity == ProjectDiagnosticSeverity.Error);
        Assert.Equal(new int?[] { 3, 4 }, result.Findings.Where(f => f.Code == "KNE303").Select(f => f.Line));
        Assert.All(result.Findings.Where(f => f.Code == "KNE303"), f => Assert.Equal(ProjectDiagnosticSeverity.Warning, f.Severity));
    }

    [Fact]
    public async Task Analyze_NilInLaterRequiredArgument_IsDetectedAfterNestedExpressions_OptionalNarratorIsAllowed()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n set_window_size(math.max(640, 800), nil)\n d(nil, 'allowed')\n d(get_speaker(1, 2), nil)\nend");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(new int?[] { 2, 4 }, result.Findings.Where(f => f.Code == "KNE303").Select(f => f.Line));
        Assert.All(result.Findings.Where(f => f.Code == "KNE303"), f => Assert.Contains("аргументе 2", f.Message));
    }

    [Fact]
    public async Task Analyze_LocalApiShadowAndParameterTransitionTarget_DoNotProduceInventedMissingFilesOrFunctions()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "local function background(value) return value end\nfunction main() background('../not-a-resource.png'); d(nil, 'ok') end\nfunction helper(target) call(target) end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.DoesNotContain(result.Findings, f => f.Code is "KNE204" or "KNE302" or "KNE203");
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task Analyze_ChoiceWarnsForNilOptionsButAllowsNilPrompt()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n choice(nil, {'one'})\n choice('prompt', nil)\nend");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        var diagnostic = Assert.Single(result.Findings, finding => finding.Code == "KNE303");
        Assert.Equal(3, diagnostic.Line);
        Assert.Contains("аргументе 2", diagnostic.Message);
        Assert.Contains("choice", diagnostic.Message);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(0, result.ErrorCount);
    }

    [Fact]
    public async Task Analyze_GotoAndCallNames_AreCheckedWithoutConfusingNativeGotoOrAssignedDynamicTargets()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                local target = known
                call(target)
                call(known)
                goto(missing)
                call("other_missing")
                goto native
                ::native::
                d(nil, 'ok')
            end
            function known() d(nil, 'known') end
            """);
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(new int?[] { 5, 6 }, result.Findings.Where(f => f.Code == "KNE203").Select(f => f.Line));
        Assert.DoesNotContain(result.Findings, f => f.Code == "KNE100");
    }

    [Fact]
    public async Task Analyze_EmptyFunctionsAndUnboundedLoops_AreConservativeWarnings()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                while false do end
                while counter < limit do counter = counter + 1 end
                while true do d(nil, 'yield') end
            end
            function empty() -- only a comment
            end
            """);
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        var loop = Assert.Single(result.Findings, f => f.Code == "KNE401");
        Assert.Equal(3, loop.Line);
        Assert.Equal(ProjectDiagnosticSeverity.Warning, loop.Severity);
        Assert.Contains(result.Findings, f => f.Code == "KNE301" && f.Line == 6);
        Assert.DoesNotContain(result.Findings, f => f.Code == "KNE100");
    }

    [Fact]
    public async Task Analyze_RecursiveDirectAndCoroutineCallsWithoutYield_AreWarnings()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() d(nil, 'entry') end\nfunction alpha() beta() end\nfunction beta() call(alpha) end\nfunction safe() d(nil, 'yield'); safe() end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(new int?[] { 2, 3 }, result.Findings.Where(f => f.Code == "KNE402").Select(f => f.Line));
        Assert.All(result.Findings.Where(f => f.Code == "KNE402"), f => Assert.Equal(ProjectDiagnosticSeverity.Warning, f.Severity));
    }

    [Fact]
    public async Task Analyze_DraftGraph_AccumulatesDanglingOutputsUnreachableBlocksResourcesAndTargets()
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create(); graph.FunctionName = "draft";
        graph.Nodes.Single(n => n.Id == "start").Next = "missing";
        graph.Nodes.Add(new() { Id = "character", Kind = ScriptNodeKind.Character, Path = "game/characters/absent.png", Next = "end" });
        graph.Nodes.Add(new() { Id = "call", Kind = ScriptNodeKind.CallScene, Path = "absent", Next = "end" });
        fixture.Write("draft.knegraph", graph.Serialize());
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE103" && f.BlockId == "start");
        Assert.Equal(4, result.Findings.Count(f => f.Code == "KNE304" && f.File == "draft.knegraph"));
        Assert.Contains(result.Findings, f => f.Code == "KNE302" && f.BlockId == "character");
        Assert.Contains(result.Findings, f => f.Code == "KNE203" && f.BlockId == "call");
        Assert.All(result.Findings.Where(f => f.BlockId != null), f => Assert.NotNull(f.Frame));
    }

    [Fact]
    public async Task Analyze_InvalidGraphAndEngineJson_DoNotPreventOtherDiagnostics()
    {
        using var fixture = new TempProject();
        fixture.Write("bad.knegraph", "{\n\"Nodes\": null }");
        fixture.Write("engine/theme.json", "{\n invalid }");
        fixture.Write("bad.lua", "local value = )");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE006" && f.File == "bad.knegraph");
        Assert.Contains(result.Findings, f => f.Code == "KNE006" && f.File == "engine/theme.json" && f.Line == 2);
        Assert.Contains(result.Findings, f => f.Code == "KNE100" && f.File == "bad.lua");
    }

    [Fact]
    public async Task Analyze_InvalidFrameRegistry_IsReportedWithoutLosingSourceDiagnostics()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/frame-ids.json", "{\"Version\":99,\"Entries\":[]}");
        fixture.Write("missing.lua", "function helper() background('no.png') end");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE008" && f.File == "engine/frame-ids.json");
        Assert.Contains(result.Findings, f => f.Code == "KNE302" && f.File == "missing.lua");
    }

    [Fact]
    public async Task Analyze_OversizedSource_IsReportedAndOtherFilesStillCompile()
    {
        using var fixture = new TempProject();
        fixture.Write("large.lua", new string(' ', 8 * 1024 * 1024 + 1));
        fixture.Write("bad.lua", "function (");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Contains(result.Findings, f => f.Code == "KNE004" && f.File == "large.lua");
        Assert.Contains(result.Findings, f => f.Code == "KNE100" && f.File == "bad.lua");
    }

    [Fact]
    public async Task Analyze_PreCancelled_DoesNotProduceFilesOrStartWork()
    {
        using var fixture = new TempProject();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, "reports")));
        Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "engine/frame-ids.json")));
    }

    [Fact]
    public async Task SaveReport_IsUniqueUtf8Text_WithCountsLocationsFrameIdsAndHeuristicLimitations_AndDoesNotRescanItself()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main()\n background('missing.png')\nend");
        var result = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        string first = await ProjectAnalyzer.SaveReportAsync(fixture.ProjectPath, result);
        string second = await ProjectAnalyzer.SaveReportAsync(fixture.ProjectPath, result);
        Assert.NotEqual(first, second);
        Assert.StartsWith(Path.Combine(fixture.ProjectPath, "reports") + Path.DirectorySeparatorChar, first, StringComparison.OrdinalIgnoreCase);
        string report = await File.ReadAllTextAsync(first);
        Assert.Equal(report, await File.ReadAllTextAsync(second));
        Assert.Contains("Новелла с пробелами", report);
        Assert.Contains("Ошибок: 0; предупреждений: 1", report);
        Assert.Contains("game/scenes/main.krl:2", report);
        Assert.Contains("фрейм сцены", report);
        Assert.Contains("Сценарии не запускались", report);
        Assert.Contains("Динамические пути", report);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(first)!, "*.tmp"));
        var after = await ProjectAnalyzer.AnalyzeAsync(fixture.ProjectPath);
        Assert.Equal(result.Findings, after.Findings);
    }

    [Fact]
    public async Task SaveReport_PreCancelled_DoesNotCreateReportDirectory()
    {
        using var fixture = new TempProject();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = new ProjectAnalysisResult("test", DateTimeOffset.Now, []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectAnalyzer.SaveReportAsync(fixture.ProjectPath, result, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.ProjectPath, "reports")));
    }
}
