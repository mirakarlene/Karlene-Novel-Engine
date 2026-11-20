using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class MetaSystemRuntimeTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PersistentFalseMissingAndNestedValuesSurviveAcrossDirectAndWorkerSessionsAndHeadlessResetIsDenied(bool worker)
    {
        using var fixture = new TempProject();
        Code(fixture, """
            persistent.player={name='Аня',items={'sword',false},score=7}
            persistent.enabled=false; persistent.temporary='remove'; persistent.temporary=nil
            state.missing=persistent_get('missing')==nil
            state.has_enabled=persistent_has('enabled'); state.enabled=persistent.enabled
            local ok,problem=pcall(persistent_clear)
            state.reset_allowed=ok; state.reset_error=tostring(problem)
            state.retained=persistent.player.name
            d(nil,'written')
            """);
        var project = await fixture.LoadAsync();
        await using (var first = Runtime(project, worker))
        {
            await first.StartAsync(); var state = (await first.GetCheckpointAsync()).State;
            Assert.True(state.GetProperty("missing").GetBoolean()); Assert.True(state.GetProperty("has_enabled").GetBoolean());
            Assert.False(state.GetProperty("enabled").GetBoolean()); Assert.False(state.GetProperty("reset_allowed").GetBoolean());
            Assert.Contains("подтверждения", state.GetProperty("reset_error").GetString()); Assert.Equal("Аня", state.GetProperty("retained").GetString());
        }
        Assert.True(File.Exists(Path.Combine(project.Path, "saves", "persistent")));
        Code(fixture, """
            state.profile=persistent.player; state.enabled=persistent.enabled
            state.present=persistent_has('player'); state.deleted=not persistent_has('temporary')
            persistent_delete('player'); state.removed=not persistent_has('player')
            d(nil,'read')
            """);
        await using var second = Runtime(await fixture.LoadAsync(), !worker); await second.StartAsync();
        var loaded = (await second.GetCheckpointAsync()).State;
        Assert.False(loaded.GetProperty("enabled").GetBoolean()); Assert.True(loaded.GetProperty("present").GetBoolean());
        Assert.True(loaded.GetProperty("deleted").GetBoolean()); Assert.True(loaded.GetProperty("removed").GetBoolean());
        var profile = loaded.GetProperty("profile"); Assert.Equal("Аня", profile.GetProperty("name").GetString()); Assert.Equal(7, profile.GetProperty("score").GetInt32());
        Assert.Equal("sword", profile.GetProperty("items")[0].GetString()); Assert.False(profile.GetProperty("items")[1].GetBoolean());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SystemJournalReplaysRecordedTypedResultsWithoutCallingTheHostAndNewCallsUseTheLiveHost(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, """
            persistent_set('visited',true)
            state.hour=real_time().hour; state.clipboard=clipboard_read()
            d(nil,'before:'..state.hour..':'..state.clipboard)
            state.later=real_time().hour; d(nil,'after')
            """);
        var calls = new List<SystemCall>(); int hour = 7; string clipboard = "исходный текст";
        Task<SystemReply> Host(SystemCall call, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); calls.Add(call);
            return Task.FromResult(call.Operation switch
            {
                "persistent_set" => SystemReply.From<object?>(null),
                "real_time" => SystemReply.From(new { hour, minute = 12, weekday = 3 }),
                "clipboard_read" => SystemReply.From(clipboard),
                _ => SystemReply.Failure("Unexpected operation: " + call.Operation)
            });
        }
        var project = await fixture.LoadAsync(); GameCheckpoint checkpoint;
        await using (var source = Runtime(project, worker, Host))
        {
            await source.StartAsync(); checkpoint = Roundtrip(await source.GetCheckpointAsync());
            Assert.Equal("before:7:исходный текст", source.Story.Text);
        }
        Assert.Equal(new[] { "persistent_set", "real_time", "clipboard_read" }, calls.Select(call => call.Operation));
        Assert.Equal("visited", calls[0].Arguments[0].GetString()); Assert.True(calls[0].Arguments[1].GetBoolean());
        Assert.Equal(3, checkpoint.SystemResults!.Count); Assert.Equal(7, checkpoint.SystemResults[1].Value.GetProperty("hour").GetInt32());
        hour = 23; clipboard = "changed"; calls.Clear();
        await using var restored = Runtime(project, worker, Host); await restored.RestoreAsync(checkpoint);
        Assert.Empty(calls); Assert.Equal("before:7:исходный текст", restored.Story.Text);
        var replayed = await restored.GetCheckpointAsync(); Assert.Equal(7, replayed.State.GetProperty("hour").GetInt32()); Assert.Equal("исходный текст", replayed.State.GetProperty("clipboard").GetString());
        await restored.AdvanceAsync(); Assert.Equal("after", restored.Story.Text); Assert.Equal("real_time", Assert.Single(calls).Operation);
        var continued = await restored.GetCheckpointAsync(); Assert.Equal(23, continued.State.GetProperty("later").GetInt32()); Assert.Equal(4, continued.SystemResults!.Count);
        Assert.False(File.Exists(Path.Combine(project.Path, "saves", "persistent")));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PlayedTimeIsStoredInMinutesAndCheckpointRoundTripsItsAccumulatedSeconds(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, "state.minutes=play_time(); d(nil,'time')");
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, worker, clock: () => 135);
        await runtime.StartAsync(); var saved = Roundtrip(await runtime.GetCheckpointAsync());
        Assert.Equal(2.25, saved.State.GetProperty("minutes").GetDouble()); Assert.Equal(135, saved.PlayedSeconds);
        await using var restored = Runtime(project, worker); await restored.RestoreAsync(saved);
        var reloaded = await restored.GetCheckpointAsync(); Assert.Equal(135, reloaded.PlayedSeconds); Assert.Equal(2.25, reloaded.State.GetProperty("minutes").GetDouble());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ARecordedHostDenialReplaysTheSameErrorWithoutAskingOrInvokingItsSideEffectAgain(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, "local ok,problem=pcall(clipboard_read); state.allowed=ok; state.problem=tostring(problem); d(nil,'denied')");
        int prompts = 0;
        Task<SystemReply> Deny(SystemCall call, CancellationToken token) { prompts++; Assert.Equal("clipboard_read", call.Operation); return Task.FromResult(SystemReply.Failure("Player denied clipboard access")); }
        var project = await fixture.LoadAsync(); GameCheckpoint checkpoint;
        await using (var original = Runtime(project, worker, Deny))
        {
            await original.StartAsync(); checkpoint = Roundtrip(await original.GetCheckpointAsync());
            Assert.False(checkpoint.State.GetProperty("allowed").GetBoolean()); Assert.Contains("Player denied clipboard access", checkpoint.State.GetProperty("problem").GetString());
        }
        Assert.Equal(1, prompts); Assert.Equal("Player denied clipboard access", Assert.Single(checkpoint.SystemResults!).Error);
        await using var restored = Runtime(project, worker, (_, _) => throw new InvalidOperationException("A recorded denial must not prompt again."));
        await restored.RestoreAsync(checkpoint); Assert.Equal("denied", restored.Story.Text); Assert.Equal(1, prompts);
        Assert.False((await restored.GetCheckpointAsync()).State.GetProperty("allowed").GetBoolean());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NamedSaveCapturesTheExactContinuationAndRestoringItDoesNotWriteTheSlotOrPersistentAgain(bool worker)
    {
        using var fixture = new TempProject(); Code(fixture, """
            state.counter=1; persistent_set('saved_once',true)
            save_named('BeforeChapter')
            state.counter=2; d(nil,'after:2')
            state.counter=3; d(nil,'later:3')
            """);
        var project = await fixture.LoadAsync(); var store = new GameSaveStore(project.Path, Path.Combine(project.Path, "saves"));
        var services = new GameSystemServices(project, store); var calls = new List<string>();
        Task<SystemReply> Host(SystemCall call, CancellationToken token) { calls.Add(call.Operation); return services.InvokeAsync(call, token); }
        await using (var runtime = Runtime(project, worker, Host))
        {
            await runtime.StartAsync(); Assert.Equal("after:2", runtime.Story.Text); Assert.Equal(2, (await runtime.GetCheckpointAsync()).State.GetProperty("counter").GetInt32());
        }
        Assert.Equal(new[] { "persistent_set", "save_checkpoint" }, calls);
        var saved = await store.LoadAsync("BeforeChapter"); Assert.Equal("system_save", saved.Frame.Kind); Assert.Equal("BeforeChapter", saved.Frame.Text); Assert.Equal(1, saved.State.GetProperty("counter").GetInt32());
        byte[] slot = File.ReadAllBytes(store.SlotPath("BeforeChapter")); byte[] persistent = File.ReadAllBytes(Path.Combine(store.DirectoryPath, "persistent"));
        calls.Clear(); await using var restored = Runtime(project, worker, Host); await restored.RestoreAsync(Roundtrip(saved));
        Assert.Empty(calls); Assert.Equal("after:2", restored.Story.Text); Assert.Equal(2, (await restored.GetCheckpointAsync()).State.GetProperty("counter").GetInt32());
        Assert.Equal(slot, File.ReadAllBytes(store.SlotPath("BeforeChapter"))); Assert.Equal(persistent, File.ReadAllBytes(Path.Combine(store.DirectoryPath, "persistent")));
        await restored.AdvanceAsync(); Assert.Equal("later:3", restored.Story.Text); Assert.Equal(3, (await restored.GetCheckpointAsync()).State.GetProperty("counter").GetInt32()); Assert.Empty(calls);
    }

    [Fact]
    public async Task SaveServicesReadAndDeleteOnlyOwnedSlotsAndExcludeDamagedOrForeignEntries()
    {
        using var fixture = new TempProject(); Code(fixture, "state.score=37; d(nil,'owned')");
        var project = await fixture.LoadAsync(); await using var runtime = Runtime(project, false); await runtime.StartAsync(); var checkpoint = await runtime.GetCheckpointAsync();
        var store = new GameSaveStore(project.Path, Path.Combine(project.Path, "saves")); var services = new GameSystemServices(project, store);
        await store.SaveAsync("owned", checkpoint); await store.SaveAsync("foreign", checkpoint with { ProjectPath = Path.Combine(fixture.Root, "another-game") });
        File.WriteAllText(store.SlotPath("damaged"), "{broken"); byte[] foreign = File.ReadAllBytes(store.SlotPath("foreign"));
        Task<SystemReply> Invoke(string operation, params object?[] args) => services.InvokeAsync(Call(operation, args), default);
        Assert.True((await Invoke("save_exists", "owned")).Value.GetBoolean()); Assert.False((await Invoke("save_exists", "absent")).Value.GetBoolean());
        Assert.Equal(37, (await Invoke("save_read", "owned", "score")).Value.GetInt32()); Assert.Equal(JsonValueKind.Null, (await Invoke("save_read", "owned", "missing")).Value.ValueKind);
        Assert.Equal("owned", Assert.Single((await Invoke("save_list")).Value.EnumerateArray()).GetString());
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke("save_read", "foreign", "score"));
        await Assert.ThrowsAsync<InvalidDataException>(() => Invoke("save_delete", "foreign")); Assert.Equal(foreign, File.ReadAllBytes(store.SlotPath("foreign")));
        await Invoke("save_delete", "owned"); Assert.False(store.Exists("owned")); Assert.True(store.Exists("foreign")); Assert.True(store.Exists("damaged"));
        Assert.Empty((await Invoke("save_list")).Value.EnumerateArray());
    }

    [Fact]
    public async Task FileServicesKeepUtf8AppendUnitsAndNonrecursiveSortedListingInsideTheProject()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Security.FileSystem = "project";
        var services = new GameSystemServices(project, new GameSaveStore(project.Path, Path.Combine(project.Path, "saves")));
        SystemReply FileCall(string operation, params object?[] args) => services.InvokeFile(Call(operation, args), default);
        FileCall("create_folder", "notes"); FileCall("create_folder", "notes/child");
        FileCall("write_text_file", "notes/log.txt", "я", "write"); FileCall("write_text_file", "notes/log.txt", "\nline", "append");
        FileCall("write_text_file", "notes/zeta.txt", "z"); FileCall("write_text_file", "notes/Alpha.txt", "a"); FileCall("write_text_file", "notes/child/hidden.txt", "nested");
        Assert.Equal("я\nline", FileCall("read_text_file", "notes/log.txt").Value.GetString());
        Assert.True(FileCall("path_exists", "notes/log.txt").Value.GetBoolean()); Assert.True(FileCall("path_exists", "notes").Value.GetBoolean()); Assert.False(FileCall("path_exists", "notes/missing").Value.GetBoolean());
        foreach (var (unit, divisor) in new[] { ("bytes", 1d), ("kib", 1024d), ("mib", 1048576d), ("gib", 1073741824d) })
            Assert.Equal(7 / divisor, FileCall("file_size", "notes/log.txt", unit).Value.GetDouble());
        Assert.Equal(new[] { "Alpha.txt", "log.txt", "zeta.txt" }, FileCall("list_files", "notes").Value.EnumerateArray().Select(item => item.GetString()));
        File.WriteAllBytes(Path.Combine(project.Path, "notes", "bom.txt"), [0xef, 0xbb, 0xbf, 0xd1, 0x8f]);
        Assert.Equal("я", FileCall("read_text_file", "notes/bom.txt").Value.GetString());
    }

    [Fact]
    public async Task FileServicesEnforceOneMebibyteBeforeWritesAndRejectEscapesAndReservedStorage()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Security.FileSystem = "project";
        var services = new GameSystemServices(project, new GameSaveStore(project.Path, Path.Combine(project.Path, "saves")));
        SystemReply FileCall(string operation, params object?[] args) => services.InvokeFile(Call(operation, args), default);
        string initial = new('a', 1_048_575); FileCall("write_text_file", "large.txt", initial); FileCall("write_text_file", "large.txt", "b", "append");
        Assert.Equal(initial + "b", FileCall("read_text_file", "large.txt").Value.GetString());
        Assert.Throws<InvalidDataException>(() => FileCall("write_text_file", "large.txt", "я", "append"));
        Assert.Throws<InvalidDataException>(() => FileCall("write_text_file", "large.txt", initial + "bc"));
        Assert.Equal(initial + "b", File.ReadAllText(Path.Combine(project.Path, "large.txt")));
        fixture.Write("oversize.txt", initial + "bc"); Assert.Throws<InvalidDataException>(() => FileCall("read_text_file", "oversize.txt"));
        Assert.Throws<ArgumentException>(() => FileCall("write_text_file", "large.txt", "bad", "truncate"));
        Assert.Throws<ArgumentException>(() => FileCall("file_size", "large.txt", "kilograms"));
        Assert.Throws<ArgumentException>(() => FileCall("write_text_file", "../escape.txt", "bad")); Assert.False(File.Exists(Path.Combine(fixture.Root, "escape.txt")));
        foreach (string path in new[] { "saves/persistent", GameSystemConsent.FileName })
            Assert.Throws<InvalidOperationException>(() => FileCall("write_text_file", path, "bad"));
        File.WriteAllBytes(Path.Combine(project.Path, "invalid-utf8.txt"), [0xc3, 0x28]);
        Assert.Throws<DecoderFallbackException>(() => FileCall("read_text_file", "invalid-utf8.txt"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => services.InvokeFile(Call("write_text_file", "canceled.txt", "bad"), canceled.Token));
        Assert.False(File.Exists(Path.Combine(project.Path, "canceled.txt")));
    }

    [Fact]
    public async Task ConsentRemembersBothDenialsAndApprovalsAcrossInstancesAndKeepsCategoriesAndGamesSeparate()
    {
        using var fixture = new TempProject(); string first = Path.Combine(fixture.Root, "permissions-one"), second = Path.Combine(fixture.Root, "permissions-two"); int prompts = 0;
        Task<bool> Deny() { prompts++; return Task.FromResult(false); }
        Task<bool> Allow() { prompts++; return Task.FromResult(true); }
        Task<bool> Unexpected() => throw new InvalidOperationException("Remembered decisions must not ask again.");
        Assert.False(await new GameSystemConsent(first).AllowAsync("files:project", Deny, default));
        var same = new GameSystemConsent(first); Assert.False(await same.AllowAsync("files:project", Unexpected, default));
        Assert.True(await same.AllowAsync("files:full", Allow, default));
        Assert.True(await same.AllowAsync("clipboard", Allow, default)); Assert.True(await new GameSystemConsent(first).AllowAsync("clipboard", Unexpected, default));
        Assert.True(await new GameSystemConsent(second).AllowAsync("files:project", Allow, default));
        Assert.False(await new GameSystemConsent(first).AllowAsync("files:project", Unexpected, default)); Assert.Equal(4, prompts);
        using var decisions = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, GameSystemConsent.FileName)));
        Assert.False(decisions.RootElement.GetProperty("files:project").GetBoolean()); Assert.True(decisions.RootElement.GetProperty("files:full").GetBoolean()); Assert.True(decisions.RootElement.GetProperty("clipboard").GetBoolean());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => same.AllowAsync("browser", Unexpected, canceled.Token)); Assert.Equal(4, prompts);
    }

    [Fact]
    public void MetaCatalogueContainsAllThirtyThreeCommandsInTheYellowGroupBetweenConfigurationsAndShaders()
    {
        string[] expected = ["persistent_set", "persistent_get", "persistent_has", "persistent_clear", "persistent_delete", "save_named", "save_exists", "save_read", "save_delete", "save_list", "real_time", "real_date", "play_time", "build_version", "launch_count", "restart_game", "exit_game", "return_to_menu", "minimize_game", "fullscreen_game", "windowed_game", "open_browser", "clipboard_write", "clipboard_read", "take_screenshot", "read_text_file", "write_text_file", "path_exists", "file_size", "list_files", "create_folder", "project_path", "freeze_and_scan"];
        Assert.Equal(expected.Order(), SystemBlocks.All.Values.Select(block => block.Function).Order());
        var groups = BlockLibrary.BuiltInGroups.ToArray(); int index = Array.FindIndex(groups, group => group.Id == "meta_system");
        Assert.True(index > 0 && index < groups.Length - 1); Assert.Equal("configurations", groups[index - 1].Id); Assert.Equal("shaders", groups[index + 1].Id); Assert.Equal("#E3B52D", groups[index].Color);
        Assert.Equal(33, BlockLibrary.Definitions.Count(block => block.Group == "meta_system"));
        foreach (var (kind, command) in SystemBlocks.All)
        {
            var definition = Assert.Single(BlockLibrary.Definitions, block => block.Kind == kind); Assert.Equal("meta_system", definition.Group); Assert.Equal(groups[index].Color, definition.Color);
            var node = BlockLibrary.Create(kind); node.Id = "system"; node.Variable = "result";
            node.SystemArguments = command.Parameters.ToDictionary(key => key, key => new LogicOperand(key == "seconds" ? "number" : "string", key == "seconds" ? "2.5" : SystemBlocks.Default(key)));
            var graph = new VisualScript { FunctionName = "main", Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = node.Id }, node] };
            string encoded = graph.Serialize(); var decoded = VisualScript.Parse(encoded); Assert.Equal(encoded, decoded.Serialize());
            string compiled = SystemBlocks.Compile(decoded.Nodes.Single(block => block.Id == "system")); Assert.Contains(command.Function + "(", compiled);
            Assert.Equal(command.Parameters, decoded.Nodes.Single(block => block.Id == "system").SystemArguments!.Keys);
        }
    }

    [Theory]
    [InlineData(false, ScriptNodeKind.RestartGame, "restart")] [InlineData(true, ScriptNodeKind.RestartGame, "restart")]
    [InlineData(false, ScriptNodeKind.MainMenu, "menu_reset")] [InlineData(true, ScriptNodeKind.MainMenu, "menu_reset")]
    [InlineData(false, ScriptNodeKind.ExitGame, "exit")] [InlineData(true, ScriptNodeKind.ExitGame, "exit")]
    public async Task TerminalSystemBlocksPublishTheirExactEndActionAndNeverExecuteTheConnectedContinuation(bool worker, ScriptNodeKind kind, string action)
    {
        using var fixture = new TempProject();
        string path = await ProjectCreator.CreateProjectAsync("Terminal " + kind, fixture.Root, ProjectAuthoringMode.Visual);
        await File.WriteAllTextAsync(Path.Combine(path, "forbidden.lua"), "state.continued=true; error('terminal continuation executed')");
        var terminal = BlockLibrary.Create(kind); terminal.Id = "terminal"; terminal.Next = "forbidden";
        Assert.Empty(terminal.Outputs());
        var graph = new VisualScript { FunctionName = "main", Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "before" },
            new() { Id = "before", Kind = ScriptNodeKind.Dialogue, Text = "before", Next = "choose" },
            new() { Id = "choose", Kind = ScriptNodeKind.Choice, Options = [new() { Text = "Finish", Target = terminal.Id }, new() { Text = "Other path", Target = "forbidden" }] },
            terminal,
            new() { Id = "forbidden", Kind = ScriptNodeKind.Script, Path = "forbidden.lua", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] };
        await File.WriteAllTextAsync(Path.Combine(path, "game/scenes/main.knegraph"), graph.Serialize());
        int hostCalls = 0;
        Task<SystemReply> Unexpected(SystemCall call, CancellationToken token) { hostCalls++; return Task.FromResult(SystemReply.Failure("Terminal actions must be handled by the scene scheduler.")); }
        await using var runtime = Runtime(await ProjectLoader.LoadProjectAsync(path), worker, Unexpected);
        await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text); Assert.False(runtime.Story.Completed);
        await runtime.AdvanceAsync(); Assert.Equal("choice", runtime.Story.Kind);
        await runtime.ChooseAsync(1); Assert.True(runtime.Story.Completed); Assert.Equal(action, runtime.Story.EndAction);
        Assert.False((await runtime.CaptureCheckpointAsync()).State.TryGetProperty("continued", out _)); Assert.Equal(0, hostCalls);
    }

    static SystemCall Call(string operation, params object?[] arguments) => new(operation, JsonSerializer.SerializeToElement(arguments));
    static void Code(TempProject fixture, string body) => NamedListRuntimeTests.Code(fixture, body);
    static GameCheckpoint Roundtrip(GameCheckpoint checkpoint) => JsonSerializer.Deserialize<GameCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
    static IGameSession Runtime(ProjectInfo project, bool worker, Func<SystemCall, CancellationToken, Task<SystemReply>>? host = null, Func<double>? clock = null) => worker
        ? new ProcessGameSession(project, _ => { }) { SystemHost = host, PlayClock = clock }
        : new LuaRuntime(project, _ => { }, interactive: true) { SystemHost = host, PlayedSeconds = clock?.Invoke() ?? 0 };
}
