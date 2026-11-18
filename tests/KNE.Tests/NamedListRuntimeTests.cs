using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KNE.Tests;

public sealed class NamedListRuntimeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NamedListsAreIndependentFromStateAndAllZeroBasedMutationsAndQueriesHaveTheirDocumentedResults(bool worker)
    {
        using var fixture = new TempProject();
        Code(fixture, """
            state.inventory='ordinary state variable'
            create_list('inventory',{'sword','shield','sword'})
            create_list('empty')
            state.created=list_exists('inventory'); state.absent=list_exists('missing')
            state.empty=list_is_empty('empty'); state.empty_length=list_length('empty')
            state.removed=list_remove('inventory','sword')
            state.absent_value=list_remove('inventory','axe')
            state.after_remove=table.concat(inventory,'|')
            list_add('inventory','helm')
            list_insert('inventory',0,'potion')
            list_insert('inventory',list_length('inventory'),'boots')
            list_set('inventory',2,'new_sword')
            state.first=list_get('inventory',0); state.last=list_get('inventory',4)
            state.length=list_length('inventory'); state.found=list_contains('inventory','shield')
            state.not_found=list_contains('inventory','axe'); state.index=list_index_of('inventory','new_sword')
            state.missing_index=list_index_of('inventory','axe')
            list_remove_at('inventory',0); list_remove_at('inventory',list_length('inventory')-1)
            state.after_remove_at=table.concat(inventory,'|'); state.nonempty=list_is_empty('inventory')
            clear_list('empty'); list_add('empty',false); state.false_element=list_get('empty',0)
            clear_list('empty'); delete_list('empty'); state.deleted=list_exists('empty')
            d(nil,'done')
            """);
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync();
        var saved = await runtime.GetCheckpointAsync(); var state = saved.State;
        Assert.Equal("ordinary state variable", state.GetProperty("inventory").GetString());
        Assert.True(state.GetProperty("created").GetBoolean()); Assert.False(state.GetProperty("absent").GetBoolean());
        Assert.True(state.GetProperty("empty").GetBoolean()); Assert.Equal(0, state.GetProperty("empty_length").GetInt32());
        Assert.True(state.GetProperty("removed").GetBoolean()); Assert.False(state.GetProperty("absent_value").GetBoolean());
        Assert.Equal("shield|sword", state.GetProperty("after_remove").GetString());
        Assert.Equal("potion", state.GetProperty("first").GetString()); Assert.Equal("boots", state.GetProperty("last").GetString());
        Assert.Equal(5, state.GetProperty("length").GetInt32()); Assert.True(state.GetProperty("found").GetBoolean()); Assert.False(state.GetProperty("not_found").GetBoolean());
        Assert.Equal(2, state.GetProperty("index").GetInt32()); Assert.Equal(-1, state.GetProperty("missing_index").GetInt32());
        Assert.Equal("shield|new_sword|helm", state.GetProperty("after_remove_at").GetString()); Assert.False(state.GetProperty("nonempty").GetBoolean());
        Assert.False(state.GetProperty("false_element").GetBoolean()); Assert.False(state.GetProperty("deleted").GetBoolean());
        Assert.Equal(new[] { "shield", "new_sword", "helm" }, List(saved, "inventory").EnumerateArray().Select(v => v.GetString()));
        Assert.False(saved.Lists!.Value.TryGetProperty("empty", out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ListCreationCopyAndMergeDetachNestedValuesAndKeepTheSourceAndExistingCopiesIndependent(bool worker)
    {
        using var fixture = new TempProject();
        Code(fixture, """
            local values={{name='shield',flags={true,false}},7}
            create_list('inventory',values)
            values[1].name='external'; values[1].flags[1]=false
            list_copy('inventory','backup')
            create_list('replaced',{'old','old2','old3'}); local alias=replaced
            list_copy('backup','replaced'); state.same_alias=alias==replaced
            create_list('loot',{{name='potion',flags={true}}})
            list_merge('inventory','loot')
            inventory[1].name='changed inventory'; inventory[1].flags[2]=true
            inventory[3].name='changed merge'
            list_add('inventory','extra'); clear_list('loot')
            d(nil,'done')
            """);
        await using var runtime = Runtime(await fixture.LoadAsync(), worker); await runtime.StartAsync();
        var saved = await runtime.GetCheckpointAsync(); var inventory = List(saved, "inventory"); var backup = List(saved, "backup");
        Assert.Equal(4, inventory.GetArrayLength()); Assert.Equal(2, backup.GetArrayLength()); Assert.Equal(0, List(saved, "loot").GetArrayLength());
        Assert.Equal("changed inventory", inventory[0].GetProperty("name").GetString()); Assert.True(inventory[0].GetProperty("flags")[1].GetBoolean());
        Assert.Equal("shield", backup[0].GetProperty("name").GetString()); Assert.True(backup[0].GetProperty("flags")[0].GetBoolean()); Assert.False(backup[0].GetProperty("flags")[1].GetBoolean());
        Assert.Equal(7, backup[1].GetInt32()); Assert.Equal("changed merge", inventory[2].GetProperty("name").GetString());
        Assert.True(saved.State.GetProperty("same_alias").GetBoolean()); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(backup.GetRawText()), JsonNode.Parse(List(saved, "replaced").GetRawText())));
    }

    public static IEnumerable<object[]> InvalidOperations()
    {
        foreach (string command in new[] { "create_list('inventory')", "create_list('state')", "create_list('d')", "create_list('bad name')",
            "list_get('inventory',-1)", "list_get('inventory',2)", "list_get('inventory',.5)", "list_get('inventory','0')",
            "list_remove_at('inventory',-1)", "list_remove_at('inventory',2)", "list_insert('inventory',3,'bad')", "list_insert('inventory',-1,'bad')", "list_set('inventory',2,'bad')",
            "list_random('empty')", "list_add('inventory',nil)", "create_list('bad',{[2]='hole'})" }) yield return [command];
        foreach (string call in new[] { "delete_list('missing')", "clear_list('missing')", "list_add('missing','x')", "list_remove('missing','x')", "list_remove_at('missing',0)",
            "list_insert('missing',0,'x')", "list_set('missing',0,'x')", "list_length('missing')", "list_get('missing',0)", "list_contains('missing','x')", "list_index_of('missing','x')",
            "list_is_empty('missing')", "list_shuffle('missing')", "list_sort('missing')", "list_merge('inventory','missing')", "list_copy('missing','new')", "list_random('missing')" }) yield return [call];
    }

    [Theory]
    [MemberData(nameof(InvalidOperations))]
    public async Task InvalidListOperationsReportAnErrorAndLeaveExistingListsUnchanged(string command)
    {
        using var fixture = new TempProject();
        Code(fixture, "create_list('inventory',{'sword','shield'}); create_list('loot',{'coin'}); create_list('empty')\nlocal ok,problem=pcall(function() " + command + " end)\nstate.ok=ok; state.problem=tostring(problem); d(nil,'done')");
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        Assert.False(saved.State.GetProperty("ok").GetBoolean()); Assert.NotEmpty(saved.State.GetProperty("problem").GetString()!);
        Assert.Equal(new[] { "sword", "shield" }, List(saved, "inventory").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal("coin", List(saved, "loot")[0].GetString()); Assert.Equal(0, List(saved, "empty").GetArrayLength()); Assert.False(saved.Lists!.Value.TryGetProperty("bad", out _));
    }

    [Theory]
    [InlineData("{30,-2,7,7,0}", "-2,0,7,7,30")]
    [InlineData("{'pear','apple','plum','apple'}", "apple,apple,pear,plum")]
    [InlineData("{}", "")]
    [InlineData("{7}", "7")]
    public async Task SortingOrdersHomogeneousNumbersOrStringsAndHandlesEmptyAndSingletonLists(string values, string expected)
    {
        using var fixture = new TempProject(); Code(fixture, "create_list('inventory'," + values + "); list_sort('inventory'); state.sorted=table.concat(inventory,','); d(nil,'done')");
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); Assert.Equal(expected, (await runtime.GetCheckpointAsync()).State.GetProperty("sorted").GetString());
    }

    [Theory]
    [InlineData("{1,'2'}")]
    [InlineData("{true,false}")]
    [InlineData("{{value=1},{value=2}}")]
    public async Task SortingRejectsMixedAndUnsupportedElementTypesBeforeReorderingTheList(string values)
    {
        using var fixture = new TempProject(); Code(fixture, "create_list('inventory'," + values + "); list_copy('inventory','before'); state.ok=pcall(function() list_sort('inventory') end); d(nil,'done')");
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        Assert.False(saved.State.GetProperty("ok").GetBoolean()); Assert.Equal(List(saved, "before").GetRawText(), List(saved, "inventory").GetRawText());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CheckpointDiskRoundtripReplayAndRollbackRestoreNamedGlobalsDeletedListsAndEmptyArrays(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, """
            create_list('inventory',{'sword','shield'}); create_list('empty'); create_list('temporary',{'old'})
            d(nil,'first')
            list_add('inventory','potion'); delete_list('temporary'); d(nil,'second')
            state.observed=list_get('inventory',2); create_list('temporary',{'new'}); d(nil,'third')
            """);
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync(); var first = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync(); var second = await runtime.GetCheckpointAsync();
        Assert.Equal(2, List(first, "inventory").GetArrayLength()); Assert.Equal(3, List(second, "inventory").GetArrayLength()); Assert.False(second.Lists!.Value.TryGetProperty("temporary", out _));
        var slots = new GameSaveStore(project.Path, Path.Combine(fixture.Root, "slots")); await slots.SaveAsync("lists", second); var decoded = await slots.LoadAsync("lists");
        Assert.Equal(JsonValueKind.Array, List(decoded, "empty").ValueKind); Assert.Equal(0, List(decoded, "empty").GetArrayLength());
        await using var restored = Runtime(project, worker); await restored.RestoreAsync(decoded); Assert.Equal("second", restored.Story.Text);
        await restored.AdvanceAsync(); var third = await restored.GetCheckpointAsync(); Assert.Equal("potion", third.State.GetProperty("observed").GetString()); Assert.Equal("new", List(third, "temporary")[0].GetString());
        await restored.RestoreAsync(first); var rolled = await restored.GetCheckpointAsync(); Assert.Equal("first", restored.Story.Text); Assert.Equal("old", List(rolled, "temporary")[0].GetString()); Assert.Equal(2, List(rolled, "inventory").GetArrayLength());
        await restored.AdvanceAsync(); Assert.False((await restored.GetCheckpointAsync()).Lists!.Value.TryGetProperty("temporary", out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ShuffleAndRandomDrawsPreserveElementsAndReplayTheExactFutureRandomSequence(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, """
            create_list('inventory',{'sword','shield','potion','helm','boots','coin'})
            d(nil,'before')
            list_shuffle('inventory'); state.draw=list_random('inventory'); state.order=table.concat(inventory,'|'); d(nil,'after')
            state.next=list_random('inventory'); d(nil,'next')
            """);
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync(); var before = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync(); var after = await runtime.GetCheckpointAsync();
        Assert.Equal(new[] { "boots", "coin", "helm", "potion", "shield", "sword" }, List(after, "inventory").EnumerateArray().Select(v => v.GetString()).Order());
        Assert.Contains(after.State.GetProperty("draw").GetString(), List(after, "inventory").EnumerateArray().Select(v => v.GetString()));
        await runtime.AdvanceAsync(); string? next = (await runtime.GetCheckpointAsync()).State.GetProperty("next").GetString();
        await using var replay = Runtime(project, worker); await replay.RestoreAsync(before); await replay.AdvanceAsync(); var replayed = await replay.GetCheckpointAsync();
        Assert.Equal(after.State.GetProperty("order").GetString(), replayed.State.GetProperty("order").GetString()); Assert.Equal(after.State.GetProperty("draw").GetString(), replayed.State.GetProperty("draw").GetString());
        await replay.AdvanceAsync(); Assert.Equal(next, (await replay.GetCheckpointAsync()).State.GetProperty("next").GetString());
    }

    [Fact]
    public async Task ShuffleUsesEachFisherYatesDrawAndLeavesEmptyOrSingletonListsAlone()
    {
        using var fixture = new TempProject(); Code(fixture, """
            create_list('inventory',{'a','b','c','d'}); create_list('empty'); create_list('single',{'only'})
            local random=math.random; state.calls=0
            math.random=function(limit) state.calls=state.calls+1; return 1 end
            list_shuffle('inventory'); state.order=table.concat(inventory,'|'); state.draw=list_random('inventory')
            list_shuffle('empty'); list_shuffle('single'); math.random=random; d(nil,'done')
            """);
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        Assert.Equal("b|c|d|a", saved.State.GetProperty("order").GetString()); Assert.Equal("b", saved.State.GetProperty("draw").GetString()); Assert.Equal(4, saved.State.GetProperty("calls").GetInt32());
        Assert.Equal(0, List(saved, "empty").GetArrayLength()); Assert.Equal("only", List(saved, "single")[0].GetString());
    }

    [Fact]
    public async Task ListCapacityAcceptsExactlyTenThousandElementsAndRejectsAdjacentGrowthWithoutPartialWrites()
    {
        using var fixture = new TempProject(); Code(fixture, """
            local values={}; for i=1,10000 do values[i]=i end
            create_list('inventory',values); create_list('loot',{10001})
            state.add=pcall(function() list_add('inventory',10001) end)
            state.insert=pcall(function() list_insert('inventory',10000,10001) end)
            state.merge=pcall(function() list_merge('inventory','loot') end)
            values[10001]=10001; state.create=pcall(function() create_list('too_large',values) end)
            state.last=list_get('inventory',9999); d(nil,'done')
            """);
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        foreach (string key in new[] { "add", "insert", "merge", "create" }) Assert.False(saved.State.GetProperty(key).GetBoolean());
        var list = List(saved, "inventory"); Assert.Equal(10000, list.GetArrayLength()); Assert.Equal(1, list[0].GetInt32()); Assert.Equal(10000, saved.State.GetProperty("last").GetInt32());
        Assert.False(saved.Lists!.Value.TryGetProperty("too_large", out _)); Assert.Equal(10001, List(saved, "loot")[0].GetInt32());
    }

    [Fact]
    public async Task InvalidNestedValuesFailAtomicallyAndMergingAListWithItselfUsesADetachedSourceSnapshot()
    {
        using var fixture = new TempProject(); Code(fixture, """
            create_list('inventory',{'sword','shield'})
            local cyclic={}; cyclic[1]=cyclic
            state.cycle=pcall(function() list_add('inventory',cyclic) end)
            state.hole=pcall(function() list_set('inventory',0,{[2]=7}) end)
            state.mixed=pcall(function() list_insert('inventory',1,{[1]=7,name='bad'}) end)
            state.fn=pcall(function() list_add('inventory',function() end) end)
            state.infinity=pcall(function() list_add('inventory',math.huge) end)
            state.before=table.concat(inventory,'|'); list_merge('inventory','inventory'); d(nil,'done')
            """);
        await using var runtime = Runtime(await fixture.LoadAsync(), false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        foreach (string key in new[] { "cycle", "hole", "mixed", "fn", "infinity" }) Assert.False(saved.State.GetProperty(key).GetBoolean());
        Assert.Equal("sword|shield", saved.State.GetProperty("before").GetString()); Assert.Equal(new[] { "sword", "shield", "sword", "shield" }, List(saved, "inventory").EnumerateArray().Select(v => v.GetString()));
    }

    [Theory]
    [InlineData("null")] [InlineData("[]")] [InlineData("{\"inventory\":1}")]
    [InlineData("{\"inventory\":{\"bad\":2}}")]
    [InlineData("{\"inventory\":[null]}")] [InlineData("{\"state\":[]}")]
    public async Task MalformedCheckpointListPayloadsAreRejectedInsteadOfSilentlyDiscarded(string json)
    {
        using var fixture = new TempProject(); Code(fixture, "create_list('inventory',{'sword'}); d(nil,'done')"); var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project, false); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        await using var restored = Runtime(project, false);
        await Assert.ThrowsAnyAsync<Exception>(() => restored.RestoreAsync(saved with { Lists = JsonDocument.Parse(json).RootElement.Clone() }));
        Assert.Equal("sword", List(saved, "inventory")[0].GetString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LegacyCheckpointsWithoutTheOptionalListFieldStillRestoreOrdinaryState(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, "state.score=7; d(nil,'legacy'); state.score=state.score+1; d(nil,'continued')"); var project = await fixture.LoadAsync();
        await using var runtime = Runtime(project, worker); await runtime.StartAsync(); var legacy = (await runtime.GetCheckpointAsync()) with { Lists = null };
        var json = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject(); Assert.True(json.Remove(nameof(GameCheckpoint.Lists)));
        var decoded = JsonSerializer.Deserialize<GameCheckpoint>(json.ToJsonString())!; Assert.Null(decoded.Lists);
        await using var restored = Runtime(project, worker); await restored.RestoreAsync(decoded); Assert.Equal("legacy", restored.Story.Text); await restored.AdvanceAsync();
        Assert.Equal(8, (await restored.GetCheckpointAsync()).State.GetProperty("score").GetInt32()); Assert.Equal("continued", restored.Story.Text);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RestoreAppliesTheSavedListPayloadInPlaceAndReconcilesCreatedOrDeletedNamesInsteadOfOnlyReplayingCode(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, """
            create_list('inventory',{'sword','shield'}); create_list('temporary',{'old'})
            local alias=inventory; d(nil,'before')
            state.first=list_get('inventory',0); state.alias=alias[1]; state.length=list_length('inventory')
            state.created=list_exists('restored_empty'); state.deleted=list_exists('temporary'); d(nil,'after')
            """);
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker); await runtime.StartAsync(); var saved = await runtime.GetCheckpointAsync();
        var overridden = saved with { Lists = JsonDocument.Parse("""{"inventory":["restored"],"restored_empty":[]}""").RootElement.Clone() };
        await using var restored = Runtime(project, worker); await restored.RestoreAsync(overridden); await restored.AdvanceAsync(); var result = await restored.GetCheckpointAsync();
        Assert.Equal("after", restored.Story.Text); Assert.Equal("restored", result.State.GetProperty("first").GetString()); Assert.Equal("restored", result.State.GetProperty("alias").GetString());
        Assert.Equal(1, result.State.GetProperty("length").GetInt32()); Assert.True(result.State.GetProperty("created").GetBoolean()); Assert.False(result.State.GetProperty("deleted").GetBoolean());
        Assert.Equal(0, List(result, "restored_empty").GetArrayLength()); Assert.False(result.Lists!.Value.TryGetProperty("temporary", out _)); Assert.Equal(2, List(saved, "inventory").GetArrayLength());
    }

    internal static void Code(TempProject fixture, string body) => fixture.Write("game/scenes/main.krl", "function main()\n" + body + "\nend");
    internal static IGameSession Runtime(ProjectInfo project, bool worker) => worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
    internal static JsonElement List(GameCheckpoint checkpoint, string name) => checkpoint.Lists!.Value.GetProperty(name);
}
