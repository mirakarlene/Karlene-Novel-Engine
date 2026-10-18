using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class CalculationTests
{
    public static IEnumerable<object[]> Operations()
    {
        yield return Case(ScriptNodeKind.Arithmetic, new() { A = "2", B = "3" }, 5);
        yield return Case(ScriptNodeKind.Arithmetic, new() { A = "2", B = "3", Operator = "-" }, -1);
        yield return Case(ScriptNodeKind.Arithmetic, new() { A = "2.5", B = "4", Operator = "*" }, 10);
        yield return Case(ScriptNodeKind.Arithmetic, new() { A = "5", B = "2", Operator = "/" }, 2.5);
        yield return Case(ScriptNodeKind.Modulo, new() { A = "-5", B = "3" }, 1);
        yield return Case(ScriptNodeKind.Increment, new() { A = "4" }, 5);
        yield return Case(ScriptNodeKind.Decrement, new() { A = "0" }, -1);
        yield return Case(ScriptNodeKind.Pow, new() { A = "2", B = "-2" }, .25);
        yield return Case(ScriptNodeKind.Sqrt, new() { A = "81" }, 9);
        yield return Case(ScriptNodeKind.Sqrt, new() { A = "0" }, 0);
        yield return Case(ScriptNodeKind.Trigonometry, new() { A = "half_pi", Function = "sin" }, 1);
        yield return Case(ScriptNodeKind.Trigonometry, new() { A = "pi", Function = "cos" }, -1);
        yield return Case(ScriptNodeKind.Trigonometry, new() { A = "0", Function = "tan" }, 0);
        yield return Case(ScriptNodeKind.Absolute, new() { A = "-5" }, 5);
        yield return Case(ScriptNodeKind.Clamp, new() { A = "150", B = "0", C = "100" }, 100);
        yield return Case(ScriptNodeKind.Clamp, new() { A = "-1", B = "0", C = "100" }, 0);
        yield return Case(ScriptNodeKind.Clamp, new() { A = "42", B = "0", C = "100" }, 42);
        yield return Case(ScriptNodeKind.Round, new() { A = "2.5" }, 3);
        yield return Case(ScriptNodeKind.Round, new() { A = "-2.5" }, -3);
        yield return Case(ScriptNodeKind.Round, new() { A = "2.49" }, 2);
        yield return Case(ScriptNodeKind.Floor, new() { A = "-1.2" }, -2);
        yield return Case(ScriptNodeKind.Ceil, new() { A = "-1.2" }, -1);
        yield return Case(ScriptNodeKind.Lerp, new() { A = "0", B = "100", T = "0.25" }, 25);
        yield return Case(ScriptNodeKind.Lerp, new() { A = "5", B = "100", T = "0" }, 5);
        yield return Case(ScriptNodeKind.Lerp, new() { A = "5", B = "100", T = "1" }, 100);
        yield return Case(ScriptNodeKind.Remap, new() { A = "5", B = "0", C = "10", D = "100", T = "200" }, 150);
        yield return Case(ScriptNodeKind.Remap, new() { A = "15", B = "0", C = "10", D = "100", T = "200" }, 250);
        yield return Case(ScriptNodeKind.Remap, new() { A = "2", B = "10", C = "0", D = "0", T = "100" }, 80);
        foreach (var (constant, expected) in new[] { ("pi", Math.PI), ("tau", Math.Tau), ("e", Math.E), ("phi", (1 + Math.Sqrt(5)) / 2), ("half_pi", Math.PI / 2), ("sqrt2", Math.Sqrt(2)) })
            yield return Case(ScriptNodeKind.Constant, new() { Constant = constant }, expected);
        yield return Case(ScriptNodeKind.WeightedRandom, new() { Weights = [new("99", 0), new("0.25", .01)] }, .25);
        yield return Case(ScriptNodeKind.Formula, new() { FormulaName = "Damage", Steps = [new("base", "(2+3)*4"), new("damage", "clamp(base^2,0,100)")] }, 100);
    }
    private static object[] Case(ScriptNodeKind kind, CalculationSettings settings, double value) => [kind, settings, value];

    [Fact]
    public async Task WeightedChoice_UsesExactFractionalThresholdsAndScaleInvariantWeights()
    {
        using var fixture = new Fixture();
        fixture.Code("""
            local random=math.random
            math.random=function() return 0.5999 end
            state.below=kne_weighted_number({{10,0.6},{20,0.4}})
            state.scaled_below=kne_weighted_number({{10,6},{20,4}})
            math.random=function() return 0.6 end
            state.at=kne_weighted_number({{10,0.6},{20,0.4}})
            state.scaled_at=kne_weighted_number({{10,6},{20,4}})
            math.random=random
            d(nil,'done')
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true); await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(10, state.GetProperty("below").GetDouble()); Assert.Equal(10, state.GetProperty("scaled_below").GetDouble());
        Assert.Equal(20, state.GetProperty("at").GetDouble()); Assert.Equal(20, state.GetProperty("scaled_at").GetDouble());
    }

    [Fact]
    public async Task Calculations_DoNotWrapLuaIntegers_AndRemapExtremeFiniteRangesWithoutOverflow()
    {
        using var fixture = new Fixture();
        fixture.Code("""
            state.edge=math.mininteger
            state.absolute=kne_math_op('abs',state.edge)
            state.sum=kne_math_op('add',math.maxinteger,1)
            state.remapped=kne_math_op('remap',0,-1e308,1e308,0,10)
            d(nil,'done')
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true); await runtime.StartAsync();
        var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(9223372036854775808d, state.GetProperty("absolute").GetDouble());
        Assert.Equal(9223372036854775808d, state.GetProperty("sum").GetDouble());
        Assert.Equal(5, state.GetProperty("remapped").GetDouble(), 10);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CalculationBlocks_ExecuteEveryOperationAndConstant_WithNumericStateResult(ScriptNodeKind kind, CalculationSettings settings, double expected)
    {
        using var fixture = new Fixture();
        var graph = fixture.Graph(kind, settings);
        var restored = VisualScript.Parse(graph.Serialize());
        Assert.Equal(graph.Serialize(), restored.Serialize());
        await using var runtime = new LuaRuntime(await fixture.LoadGraphAsync(restored), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("done", runtime.Story.Text);
        Assert.Equal(expected, (await runtime.GetCheckpointAsync()).State.GetProperty("result").GetDouble(), 10);
    }

    [Theory]
    [InlineData("-2^2 + 2^-2", -3.75)]
    [InlineData("2^3^2", 512)]
    [InlineData("state.score + score*2", 21)]
    [InlineData("max(2, abs(-4)) + sqrt(9)", 7)]
    [InlineData("1e-3 * 1000 + min(3,4)", 4)]
    [InlineData("mod(9,4) + round(1.5) + floor(1.8) + ceil(1.1)", 6)]
    public async Task FormulaExpressions_RespectPrecedenceStateReadsAndAllowedFunctions(string expression, double expected)
    {
        using var fixture = new Fixture();
        string calculation = CalculationCompiler.Compile(ScriptNodeKind.Formula, new() { Steps = [new("answer", expression)] }, "result");
        fixture.Code("state.score=7\n" + calculation + "\nd(nil,'done')");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true); await runtime.StartAsync();
        Assert.Equal(expected, (await runtime.GetCheckpointAsync()).State.GetProperty("result").GetDouble(), 10);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1; error('unsafe')")]
    [InlineData("state['score']")]
    [InlineData("os.execute(1)")]
    [InlineData("1/0;d(nil,'bad')")]
    [InlineData("sqrt(1,2)")]
    [InlineData("unknown(1)")]
    [InlineData("1e999")]
    [InlineData("1 +")]
    [InlineData("state.end")]
    public void FormulaExpressions_RejectMalformedOrExecutableTokens(string expression) => Assert.Throws<ArgumentException>(() => CalculationCompiler.Expression(expression));

    [Fact]
    public void Calculations_ValidateBoundsAndDiscriminatorsBeforeSavingOrCompiling()
    {
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Expression(new string('(', 40) + "1" + new string(')', 40)));
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Expression(new string('1', 2049)));
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.Arithmetic, new() { Operator = "execute" }, "result"));
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.Trigonometry, new() { Function = "acos" }, "result"));
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.Constant, new() { Constant = "unknown" }, "result"));
        Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.Sqrt, new(), "result;error()"));
        foreach (var rows in new List<WeightedNumber>[] { [], [new("1", 0)], [new("1", -1)], [new("1", double.NaN)], [new("1", double.PositiveInfinity)], Enumerable.Repeat(new WeightedNumber(), 101).ToList() })
            Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.WeightedRandom, new() { Weights = rows }, "result"));
        foreach (var steps in new List<FormulaStep>[] { [], [new("x", "1"), new("x", "2")], [new("pi", "1")], [new("bad name", "0")], Enumerable.Range(0, 65).Select(i => new FormulaStep("x" + i, "1")).ToList() })
            Assert.Throws<ArgumentException>(() => CalculationCompiler.Compile(ScriptNodeKind.Formula, new() { Steps = steps }, "result"));
        var bad = VisualScript.Create(); bad.Nodes.Single(n => n.Id == "dialogue").Next = "calc";
        bad.Nodes.Add(new() { Id = "calc", Kind = ScriptNodeKind.Sqrt, Variable = "not a variable", Next = "end" });
        Assert.Contains("calc", Assert.Throws<InvalidDataException>(() => bad.Serialize()).Message);
    }

    [Theory]
    [InlineData(ScriptNodeKind.Arithmetic, "1", "0", "1", "0.5", "/", "деление на ноль")]
    [InlineData(ScriptNodeKind.Modulo, "1", "0", "1", "0.5", "+", "деление на ноль")]
    [InlineData(ScriptNodeKind.Sqrt, "-1", "1", "1", "0.5", "+", "корень отрицательного")]
    [InlineData(ScriptNodeKind.Pow, "1e308", "2", "1", "0.5", "+", "конечное число")]
    [InlineData(ScriptNodeKind.Clamp, "1", "2", "0", "0.5", "+", "минимум больше")]
    [InlineData(ScriptNodeKind.Lerp, "1", "2", "0", "1.01", "+", "коэффициент")]
    [InlineData(ScriptNodeKind.Remap, "1", "2", "2", "1", "+", "нулевую длину")]
    [InlineData(ScriptNodeKind.Absolute, "missing", "1", "1", "0.5", "+", "missing")]
    public async Task CalculationDomainErrors_ReportTheOriginalGraphAndBlock(ScriptNodeKind kind, string a, string b, string c, string t, string op, string diagnostic)
    {
        using var fixture = new Fixture();
        await using var runtime = new LuaRuntime(await fixture.LoadGraphAsync(fixture.Graph(kind, new() { A = a, B = b, C = c, T = t, Operator = op })), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains(diagnostic, error.Message, StringComparison.OrdinalIgnoreCase);
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal("game/scenes/main.knegraph", report.File); Assert.Equal("calc", report.Frame!.BlockId);
    }

    [Theory]
    [InlineData("'7'")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task CalculationStateReads_RejectNumericStringsBooleansAndTablesInsteadOfCoercingThem(string value)
    {
        using var fixture = new Fixture();
        fixture.Code("state.score=" + value + "\n" + CalculationCompiler.Compile(ScriptNodeKind.Absolute, new() { A = "score" }, "result") + "\nd(nil,'unreachable')");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.StartAsync());
        Assert.Contains("конечное число", error.Message); Assert.Contains("score", error.Message);
    }

    [Fact]
    public async Task CalculationEditor_WeightTablePersistsFractionalValuesAndWeights_AndRejectsInvalidDraftWithoutChangingGraph()
    {
        using var fixture = new Fixture();
        string path = fixture.Write("game/scenes/weights.knegraph", fixture.Graph(ScriptNodeKind.WeightedRandom, new()).Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.Project, path); editor.SelectGraphBlocks(["calc"]);
            var table = FindExtended<DataGrid>(editor, "CalculationWeightsInput");
            Assert.Equal(new[] { "Значение", "Вес" }, table.Columns.Select(column => column.Header));
            var row = table.Items[0]; row.GetType().GetProperty("Value")!.SetValue(row, "2.25"); row.GetType().GetProperty("Weight")!.SetValue(row, "0.75");
            await editor.SaveAsync();
            Assert.Equal(new WeightedNumber("2.25", .75), VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == "calc").Calculation!.Weights[0]);
            table = FindExtended<DataGrid>(editor, "CalculationWeightsInput"); row = table.Items[0]; row.GetType().GetProperty("Weight")!.SetValue(row, "-1");
            await Assert.ThrowsAnyAsync<Exception>(() => editor.SaveAsync());
            Assert.Equal(.75, editor.Document!.Graph!.Nodes.Single(n => n.Id == "calc").Calculation!.Weights[0].Weight);
            Assert.Equal(.75, VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == "calc").Calculation!.Weights[0].Weight);
        });
    }

    [Fact]
    public async Task WeightedChoice_UsesFractionalRelativeWeightsAndZeroExclusion_AndCheckpointReplaysRandomDraws()
    {
        using var fixture = new Fixture();
        fixture.Code("""
            state.first=0; state.second=0
            for i=1,400 do
                local value=kne_weighted_number({{1.25,0.6},{2.5,0.4},{99,0}})
                if value==1.25 then state.first=state.first+1 elseif value==2.5 then state.second=state.second+1 else error('zero selected') end
            end
            d(nil,'before')
            state.sample=kne_weighted_number({{1.25,1e308},{2.5,1e308}})
            d(nil,'after')
            """);
        var project = await fixture.LoadAsync(); await using var runtime = new LuaRuntime(project, _ => { }, interactive: true); await runtime.StartAsync();
        var before = await runtime.GetCheckpointAsync();
        int first = before.State.GetProperty("first").GetInt32(), second = before.State.GetProperty("second").GetInt32();
        Assert.Equal(400, first + second); Assert.InRange(first, 150, 330); Assert.InRange(second, 70, 250);
        await runtime.AdvanceAsync(); var after = await runtime.GetCheckpointAsync();
        Assert.Contains(after.State.GetProperty("sample").GetDouble(), new[] { 1.25, 2.5 });
        await using var replay = new LuaRuntime(project, _ => { }, interactive: true); await replay.RestoreAsync(after);
        Assert.Equal("after", replay.Story.Text);
        var replayed = (await replay.GetCheckpointAsync()).State;
        Assert.Equal(after.State.EnumerateObject().Select(property => property.Name).Order(), replayed.EnumerateObject().Select(property => property.Name).Order());
        foreach (var property in after.State.EnumerateObject()) Assert.Equal(property.Value.GetDouble(), replayed.GetProperty(property.Name).GetDouble());
    }

    [Fact]
    public async Task FormulaRecipes_CopyAllStepsIndependently_AndDiscoverSavedProjectTemplates()
    {
        using var fixture = new Fixture();
        var settings = new CalculationSettings { FormulaName = "Шанс <успеха>", Steps = [new("base", "score*2"), new("chance", "clamp(base,0,100)")] };
        var copy = FormulaLibrary.Decode(FormulaLibrary.Encode(settings));
        Assert.Equal(settings.FormulaName, copy.FormulaName); Assert.Equal(settings.Steps, copy.Steps);
        copy.Steps.RemoveAt(0); Assert.Equal(2, settings.Steps.Count);
        Assert.Throws<ArgumentException>(() => FormulaLibrary.Decode("{}"));
        Assert.Throws<ArgumentException>(() => FormulaLibrary.Decode("KNE-FORMULA-1\n{}"));
        Assert.Throws<ArgumentException>(() => FormulaLibrary.Decode("KNE-FORMULA-1\n{\"unknown\":1}"));
        var graph = fixture.Graph(ScriptNodeKind.Formula, settings); graph.FunctionName = "saved_formula";
        fixture.Write("game/scenes/recipe.knegraph", graph.Serialize());
        var template = Assert.Single(await FormulaLibrary.LoadAsync(fixture.Project));
        Assert.Contains("recipe.knegraph", template.Label); Assert.Equal(settings.Steps, template.Settings.Steps);
        template.Settings.Steps.Clear();
        Assert.Equal(2, Assert.Single(await FormulaLibrary.LoadAsync(fixture.Project)).Settings.Steps.Count);
    }

    [Fact]
    public async Task CalculationEditor_AppliesOperandsAndReusesFormulaWithoutChangingLinks_AndSupportsUndoSave()
    {
        using var fixture = new Fixture();
        var graph = fixture.Graph(ScriptNodeKind.Arithmetic, new()); string path = fixture.Write("game/scenes/edit.knegraph", graph.Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.Project, path); editor.SelectGraphBlocks(["calc"]);
            FindExtended<TextBox>(editor, "CalculationAInput").Text = "score*2";
            FindExtended<TextBox>(editor, "CalculationBInput").Text = "4";
            FindExtended<TextBox>(editor, "CalculationResultInput").Text = "damage";
            WorkspaceTabsTests.Find<Button>(editor, "ApplyPropertiesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var changed = editor.Document!.Graph!.Nodes.Single(n => n.Id == "calc");
            Assert.Equal("score*2", changed.Calculation!.A); Assert.Equal("damage", changed.Variable); Assert.Equal("dialogue", changed.Next);
            WorkspaceTabsTests.Find<Button>(editor, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("result", editor.Document.Graph.Nodes.Single(n => n.Id == "calc").Variable);
            WorkspaceTabsTests.Find<Button>(editor, "RedoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await editor.SaveAsync();
            Assert.Equal("score*2", VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == "calc").Calculation!.A);
            var current = editor.Document.Graph.Nodes.Single(n => n.Id == "calc");
            current.Kind = ScriptNodeKind.Formula; current.Variable = "old_result"; current.Calculation = new();
            editor.SelectGraphBlocks(["dialogue"]); editor.SelectGraphBlocks(["calc"]);
            string id = "calc";
            FindExtended<TextBox>(editor, "CalculationResultInput").Text = "damage";
            editor.ApplyFormulaTemplate(new() { FormulaName = "Damage", Steps = [new("first", "score*2"), new("final", "first+5")] });
            var formula = editor.Document.Graph.Nodes.Single(n => n.Id == id);
            Assert.Equal("Damage", formula.Calculation!.FormulaName); Assert.Equal(2, formula.Calculation.Steps.Count); Assert.Equal("damage", formula.Variable); Assert.Equal("dialogue", formula.Next);
            Assert.NotNull(FindExtended<DataGrid>(editor, "FormulaStepsInput"));
            await editor.SaveAsync(); Assert.Equal("first+5", VisualScript.Parse(File.ReadAllText(path)).Nodes.Single(n => n.Id == id).Calculation!.Steps.Last().Expression);
        });
    }

    private static T FindExtended<T>(ScriptEditor editor, string name) where T : FrameworkElement =>
        WorkspaceTabsTests.Find<StackPanel>(editor, "ExtendedProperties").Children.OfType<T>().Single(view => view.Name == name);
    private sealed class Fixture : IDisposable
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kne-calculations-theme-tests");
        public string Root { get; } = Path.Combine(Scratch, "calc-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public Fixture() { Directory.CreateDirectory(Root); Project = ProjectCreator.CreateProject("Calculations", Root); }
        public string Write(string relative, string text) { string path = PathSafety.ResolveRelative(Project, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); return path; }
        public void Code(string body) => Write("game/scenes/main.krl", "function main()\n" + body + "\nend");
        public VisualScript Graph(ScriptNodeKind kind, CalculationSettings settings) => new() { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "calc" }, new() { Id = "calc", Kind = kind, Calculation = settings, Variable = "result", Next = "dialogue" }, new() { Id = "dialogue", Kind = ScriptNodeKind.Dialogue, Text = "done", Next = "end" }, new() { Id = "end", Kind = ScriptNodeKind.End }] };
        public Task<ProjectInfo> LoadAsync() => ProjectLoader.LoadProjectAsync(Project);
        public async Task<ProjectInfo> LoadGraphAsync(VisualScript graph) { Write("game/scenes/main.krl", "-- main graph"); Write("game/scenes/main.knegraph", graph.Serialize()); var project = await LoadAsync(); project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph"; return project; }
        public void Dispose() { string path = Path.GetFullPath(Root); if (!path.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(); Directory.Delete(path, true); }
    }
}
