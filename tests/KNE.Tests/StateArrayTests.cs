using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class StateArrayTests
{
    [Fact]
    public async Task NestedInitialArrays_RunForEach_AndCheckpointRestoresDataAndOrder()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/state.json", """{"items":["first",{"name":"second","flags":[true,false]}],"empty":[]}""");
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, state.items[1])
                d(nil, state.items[2].name .. ':' .. tostring(state.items[2].flags[2]))
            end
            """);
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync();
        Assert.Equal("first", runtime.Story.Text);
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal("first", checkpoint.State.GetProperty("items")[0].GetString());
        Assert.False(checkpoint.State.GetProperty("items")[1].GetProperty("flags")[1].GetBoolean());
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(checkpoint);
        await restored.AdvanceAsync();
        Assert.Equal("second:false", restored.Story.Text);
        var restoredState = (await restored.GetCheckpointAsync()).State;
        Assert.Equal("first", restoredState.GetProperty("items")[0].GetString());
        Assert.Equal("second", restoredState.GetProperty("items")[1].GetProperty("name").GetString());
        Assert.Equal(new[] { true, false }, restoredState.GetProperty("items")[1].GetProperty("flags").EnumerateArray().Select(item => item.GetBoolean()));
        // Empty Lua tables have no key type; retain the compatible object representation.
        Assert.Equal(JsonValueKind.Object, restoredState.GetProperty("empty").ValueKind);
    }

    [Theory]
    [InlineData("{[0]='bad'}")]
    [InlineData("{[1.5]='bad'}")]
    [InlineData("{[10001]='bad'}")]
    [InlineData("{[2]='gap'}")]
    [InlineData("{[1]='first',[3]='gap'}")]
    [InlineData("{[1]='first',name='mixed'}")]
    public async Task NestedSparseMixedOrInvalidNumericKeys_AreRejectedWithoutPublishingBrokenSave(string table)
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.items=" + table + "; d(nil,'ready') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.GetCheckpointAsync());
        Assert.Equal("ready", runtime.Story.Text);
    }

    [Fact]
    public async Task NestedArray_ExactlyTenThousandElements_SaveSuccessfully()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() state.items={}; for i=1,10000 do state.items[i]=i end; d(nil,'ready') end");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var saved = (await runtime.GetCheckpointAsync()).State.GetProperty("items");
        Assert.Equal(10000, saved.GetArrayLength());
        Assert.Equal(1, saved[0].GetInt32());
        Assert.Equal(10000, saved[9999].GetInt32());
    }

    [Fact]
    public async Task InitialArray_MoreThanTenThousandElements_IsRejectedBeforeLuaStarts()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/state.json", "{\"items\":[" + string.Join(',', Enumerable.Repeat("1", 10001)) + "]}");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
        Assert.Contains("10000", error.Message);
    }
}
