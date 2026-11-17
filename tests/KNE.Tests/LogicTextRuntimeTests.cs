using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class LogicTextRuntimeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TextOperationsAreUnicodeAwareAndSearchAndReplaceTreatPatternCharactersLiterally(bool worker)
    {
        using var fixture = new TempProject(); NamedListRuntimeTests.Code(fixture, """
            state.concat=string_concat('Привет, ','мир🙂')
            state.length=string_length('Привет🙂'); state.empty_length=string_length('')
            state.contains=string_contains('герой.*100%','.*'); state.absent=string_contains('герой ABC','.*')
            state.replaced=string_replace('.* a .* 100%','.*','%done')
            state.unchanged=string_replace('abc','z','q')
            state.upper=string_upper('ёж и юля🙂'); state.lower=string_lower('ЁЖ И ЮЛЯ🙂')
            d(nil,'done')
            """);
        await using var runtime = NamedListRuntimeTests.Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal("Привет, мир🙂", state.GetProperty("concat").GetString()); Assert.Equal(7, state.GetProperty("length").GetInt32()); Assert.Equal(0, state.GetProperty("empty_length").GetInt32());
        Assert.True(state.GetProperty("contains").GetBoolean()); Assert.False(state.GetProperty("absent").GetBoolean());
        Assert.Equal("%done a %done 100%", state.GetProperty("replaced").GetString()); Assert.Equal("abc", state.GetProperty("unchanged").GetString());
        Assert.Equal("ЁЖ И ЮЛЯ🙂", state.GetProperty("upper").GetString()); Assert.Equal("ёж и юля🙂", state.GetProperty("lower").GetString());
    }

    [Theory]
    [InlineData("string_concat('x',1)")]
    [InlineData("string_concat(1,2)")]
    [InlineData("string_length(12)")]
    [InlineData("string_contains('x',true)")]
    [InlineData("string_replace('abc',1,'x')")]
    [InlineData("string_upper(false)")]
    [InlineData("string_lower({})")]
    public async Task TextOperationsRejectOtherTypesInsteadOfSilentlyCoercingThem(string command)
    {
        using var fixture = new TempProject(); NamedListRuntimeTests.Code(fixture, "state.result='unchanged'; local ok,problem=pcall(function() state.result=" + command + " end); state.ok=ok; state.problem=tostring(problem); d(nil,'done')");
        await using var runtime = NamedListRuntimeTests.Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.False(state.GetProperty("ok").GetBoolean()); Assert.NotEmpty(state.GetProperty("problem").GetString()!); Assert.Equal("unchanged", state.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TypeConversionsAcceptOnlyTheAgreedNumberStringAndBooleanRepresentations(bool worker)
    {
        using var fixture = new TempProject(); NamedListRuntimeTests.Code(fixture, """
            state.number=to_number(' -12.5 '); state.numeric_identity=to_number(3)
            state.string_number=to_string(12.5); state.string_true=to_string(true); state.string_false=to_string(false); state.string_identity=to_string('ёж')
            state.true_bool=to_bool(true); state.false_bool=to_bool(false); state.true_number=to_bool(1); state.false_number=to_bool(0)
            state.true_text=to_bool('true'); state.false_text=to_bool('false'); state.true_digit=to_bool('1'); state.false_digit=to_bool('0')
            d(nil,'done')
            """);
        await using var runtime = NamedListRuntimeTests.Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(-12.5, state.GetProperty("number").GetDouble()); Assert.Equal(3, state.GetProperty("numeric_identity").GetDouble());
        Assert.Equal("12.5", state.GetProperty("string_number").GetString()); Assert.Equal("true", state.GetProperty("string_true").GetString()); Assert.Equal("false", state.GetProperty("string_false").GetString()); Assert.Equal("ёж", state.GetProperty("string_identity").GetString());
        foreach (string key in new[] { "true_bool", "true_number", "true_text", "true_digit" }) Assert.True(state.GetProperty(key).GetBoolean());
        foreach (string key in new[] { "false_bool", "false_number", "false_text", "false_digit" }) Assert.False(state.GetProperty(key).GetBoolean());
    }

    [Theory]
    [InlineData("to_number('bad')")] [InlineData("to_number('1e999')")] [InlineData("to_number(true)")] [InlineData("to_number({})")]
    [InlineData("to_string({})")] [InlineData("to_bool(2)")] [InlineData("to_bool(-1)")] [InlineData("to_bool('yes')")] [InlineData("to_bool('')")] [InlineData("to_bool({})")]
    public async Task InvalidConversionsFailWithoutReplacingTheDestination(string command)
    {
        using var fixture = new TempProject(); NamedListRuntimeTests.Code(fixture, "state.result='unchanged'; state.ok=pcall(function() state.result=" + command + " end); d(nil,'done')");
        await using var runtime = NamedListRuntimeTests.Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.False(state.GetProperty("ok").GetBoolean()); Assert.Equal("unchanged", state.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CopyDetachesNestedVariablesAndResetUsesCurrentSceneThenProjectDefaultsOrRemovesTheKey(bool worker)
    {
        using var fixture = new TempProject(); fixture.Write("engine/state.json", """{"score":1,"projectOnly":{"nested":2}}""");
        fixture.Write("game/scenes/main.krl", """
            function main()
                state.source={nested={7},flag=true}; copy_variable('copy','source'); state.source.nested[1]=99
                state.score=99; reset_variable('score'); state.main_score=state.score
                state.projectOnly.nested=99; reset_variable('projectOnly'); state.project_reset=state.projectOnly.nested
                state.transient='remove'; reset_variable('transient'); state.removed=state.transient==nil
                state.sceneOnly[1]=99; reset_variable('sceneOnly'); state.scene_reset=state.sceneOnly[1]
                call_scene(child)
                state.score=999; reset_variable('score'); state.back_score=state.score
                d(nil,'done')
            end
            function child()
                state.score=99; reset_variable('score'); state.child_score=state.score
            end
            """);
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new()
        {
            ["main"] = new() { InitialState = JsonSerializer.SerializeToElement(new { score = 7, sceneOnly = new[] { 3, 4 } }) },
            ["child"] = new() { InitialState = JsonSerializer.SerializeToElement(new { score = 11 }) }
        } } });
        await using var runtime = NamedListRuntimeTests.Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync(); var state = (await runtime.GetCheckpointAsync()).State;
        Assert.Equal(99, state.GetProperty("source").GetProperty("nested")[0].GetInt32()); Assert.Equal(7, state.GetProperty("copy").GetProperty("nested")[0].GetInt32()); Assert.True(state.GetProperty("copy").GetProperty("flag").GetBoolean());
        Assert.Equal(7, state.GetProperty("main_score").GetInt32()); Assert.Equal(11, state.GetProperty("child_score").GetInt32()); Assert.Equal(7, state.GetProperty("back_score").GetInt32());
        Assert.Equal(2, state.GetProperty("project_reset").GetInt32()); Assert.Equal(3, state.GetProperty("scene_reset").GetInt32()); Assert.True(state.GetProperty("removed").GetBoolean()); Assert.False(state.TryGetProperty("transient", out _));
    }
}
