using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class MetaPresentationRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SystemLayersAndBuildInformationSurviveWorkerTransportAndCheckpointReplay(bool worker)
    {
        using var fixture = Scene("""
            watermark('{b}build{/b}', 'bottom_right', 40)
            show_fps(true); show_input(true); global_timer('show', 'mm:ss:fff')
            state.build = build_info(); state.os = device_info()
            console_write('diagnostic')
            d(nil, 'shown')
            watermark(''); show_fps(false); show_input(false); global_timer('hide')
            d(nil, 'hidden')
            global_timer('reset', 'HH:mm:ss'); global_timer('show', 'HH:mm:ss')
            d(nil, 'reset')
            """);
        var project = await fixture.LoadAsync();
        var output = new List<string>();
        await using IGameSession runtime = worker ? new ProcessGameSession(project, output.Add) : new LuaRuntime(project, output.Add, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(new SceneWatermark("{b}build{/b}", "bottom_right", .4), runtime.Visuals.Watermark);
        Assert.True(runtime.Visuals.FpsVisible);
        Assert.True(runtime.Visuals.InputVisible);
        Assert.Equal(new SceneTimer(true, "mm:ss:fff", 0), runtime.Visuals.GlobalTimer);
        Assert.Contains(output, line => line.Contains("diagnostic", StringComparison.Ordinal));
        var checkpoint = await runtime.GetCheckpointAsync();
        var build = checkpoint.State.GetProperty("build");
        Assert.Equal(project.Title, build.GetProperty("name").GetString());
        Assert.Equal(project.Configuration.Metadata!.Version, build.GetProperty("version").GetString());
        Assert.Equal("vm", build.GetProperty("format").GetString());
        Assert.Equal("Windows", checkpoint.State.GetProperty("os").GetString());
        await runtime.AdvanceAsync();
        Assert.Null(runtime.Visuals.Watermark);
        Assert.False(runtime.Visuals.FpsVisible);
        Assert.False(runtime.Visuals.InputVisible);
        Assert.Equal(new SceneTimer(false, "mm:ss:fff", 0), runtime.Visuals.GlobalTimer);
        await runtime.AdvanceAsync();
        Assert.Equal(new SceneTimer(true, "HH:mm:ss", 0), runtime.Visuals.GlobalTimer);
        await runtime.RestoreAsync(checkpoint);
        Assert.Equal("shown", runtime.Story.Text);
        Assert.Equal(new SceneTimer(true, "mm:ss:fff", 0), runtime.Visuals.GlobalTimer);
        Assert.True(runtime.Visuals.InputVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverseDialogueRevealsFromTheEndBeforeEachAuthoredPauseAndKeepsTheFinalLine(bool worker)
    {
        using var fixture = Scene("state.person='Анна'; kne_say(nil, '{i}АБ{/i}{w} {b}{person}{/b}{w=0.5}!', nil, nil, nil, true, false, 1); d(nil, 'after')");
        var project = await fixture.LoadAsync();
        await using IGameSession runtime = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(0, runtime.Story.VisibleSuffix);
        var reveal = Assert.IsType<DialogueReveal>(runtime.Story.Reveal);
        Assert.Equal(0, reveal.First);
        Assert.Equal(2, reveal.Last);
        Assert.Equal(1, reveal.VisibleAt(reveal.Duration - .03));
        Assert.Equal(2, reveal.VisibleAt(0));
        Assert.Equal("АБ Анна!", InlineText.Parse(runtime.Story.Text).Text);
        long presentation = runtime.Story.PresentationId;
        await runtime.TickAsync(.03);
        Assert.Equal(1, runtime.Story.VisibleSuffix);
        var checkpoint = await runtime.GetCheckpointAsync();
        await runtime.AdvanceAsync();
        Assert.Equal(2, runtime.Story.VisibleSuffix);
        Assert.True(runtime.Story.CanAdvance);
        await runtime.RestoreAsync(checkpoint);
        Assert.Equal(1, runtime.Story.VisibleSuffix);
        await runtime.TickAsync(.03);
        Assert.Equal(2, runtime.Story.VisibleSuffix);
        await runtime.AdvanceAsync();
        Assert.Equal(2, runtime.Story.VisibleSuffix);
        await runtime.TickAsync(.2);
        Assert.Equal(.5, runtime.Story.RemainingSeconds);
        Assert.False(runtime.Story.CanAdvance);
        await runtime.TickAsync(.5);
        Assert.Equal("АБ Анна!", InlineText.Parse(runtime.Story.Text).Text);
        Assert.Equal(presentation, runtime.Story.PresentationId);
        await runtime.TickAsync(.1);
        Assert.Null(runtime.Story.VisibleSuffix);
        var line = InlineText.Parse(runtime.Story.Text);
        Assert.Equal("АБ Анна!", line.Text);
        Assert.True(line.Runs[0].Italic);
        Assert.True(line.Runs[1].Bold);
        await runtime.AdvanceAsync();
        Assert.Equal("after", runtime.Story.Text);
    }

    [Fact]
    public async Task DialogueSpeedUsesGraphemesAndRestoresAnExistingSegmentBeforeApplyingTheCurrentPreference()
    {
        using var fixture = Scene("d(nil, 'á🙂{w}XY'); d(nil, 'done')");
        var project = await fixture.LoadAsync();
        await using var player = new ProcessGameSession(project, _ => { }) { InitialDialogueSpeed = 10 };
        await player.StartAsync();
        Assert.Equal(0, player.Story.VisiblePrefix);
        Assert.Equal(new DialogueReveal(0, 2, .2, false), player.Story.Reveal);
        await player.TickAsync(.1);
        Assert.Equal(1, player.Story.VisiblePrefix);
        var first = await player.GetCheckpointAsync();
        Assert.Equal(10, first.InitialDialogueSpeed);
        await player.AdvanceAsync();
        Assert.Equal(2, player.Story.VisiblePrefix);
        Assert.Null(player.Story.Reveal);
        player.SetPlayerDialogueSpeed(0);
        await player.AdvanceAsync();
        Assert.Null(player.Story.Reveal);
        Assert.Equal("á🙂XY", InlineText.Parse(player.Story.Text).Text);

        await using var restored = new ProcessGameSession(project, _ => { }) { InitialDialogueSpeed = 20 };
        await restored.RestoreAsync(first);
        Assert.Equal(1, restored.Story.VisiblePrefix);
        Assert.Equal(.2, restored.Story.Reveal!.Duration);
        await restored.AdvanceAsync();
        await restored.AdvanceAsync();
        Assert.Equal(new DialogueReveal(2, 4, .1, false), restored.Story.Reveal);
        await restored.AdvanceAsync();
        await restored.AdvanceAsync();
        Assert.Equal(new DialogueReveal(0, 4, .2, false), restored.Story.Reveal);
    }

    [Theory]
    [InlineData(ScriptNodeKind.Dialogue, false)]
    [InlineData(ScriptNodeKind.EffectDialogue, false)]
    [InlineData(ScriptNodeKind.ReverseDialogue, true)]
    public async Task VisualDialogueUsesTheAuthoredSpeedAndManualSelectionCanOverrideIt(ScriptNodeKind kind, bool fromEnd)
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        var speed = BlockLibrary.Create(ScriptNodeKind.SetDialogueSpeed);
        speed.Id = "speed";
        speed.Amount = 4;
        speed.Next = "dialogue";
        graph.Nodes[0].Next = speed.Id;
        var line = BlockLibrary.Create(kind);
        line.Id = "dialogue";
        line.Text = "АБ";
        var second = BlockLibrary.Create(kind);
        second.Id = "second";
        second.Text = "ВГ";
        line.Next = second.Id;
        second.Next = "end";
        graph.Nodes[1] = line;
        graph.Nodes.Add(speed);
        graph.Nodes.Add(second);
        fixture.Write("game/scenes/main.knegraph", graph.Serialize());
        fixture.Write("game/scenes/main.krl", "");
        var project = await fixture.LoadAsync();
        project.Configuration.EntryPoints!.MainScene = "game/scenes/main.knegraph";
        await using var player = new LuaRuntime(project, _ => { }, interactive: true);
        await player.SetPlayerDialogueSpeedAsync(10);
        await player.StartAsync();
        Assert.Equal(4, player.CurrentDialogueSpeed);
        Assert.Equal(new DialogueReveal(0, 2, .5, fromEnd), player.Story.Reveal);
        await player.SetPlayerDialogueSpeedAsync(10, confirmed: true);
        await player.AdvanceAsync();
        await player.AdvanceAsync();
        Assert.Equal(new DialogueReveal(0, 2, .2, fromEnd), player.Story.Reveal);
    }

    [Fact]
    public async Task OnlyConnectedProjectFontAliasesCanBeUsedInDialogue()
    {
        using var fixture = Scene("""
            state.before = pcall(function() d(nil, '{font=sample}Строка{/font}') end)
            connect_font('fonts/sample.ttf', 'sample')
            d(nil, '{font=sample}{b}Строка{/b}{/font}')
            """);
        string directory = Path.Combine(fixture.ProjectPath, "fonts");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"), Path.Combine(directory, "sample.ttf"));
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.False((await runtime.GetCheckpointAsync()).State.GetProperty("before").GetBoolean());
        Assert.Equal("fonts/sample.ttf", runtime.Visuals.Fonts["sample"].Replace('\\', '/'));
        var run = Assert.Single(InlineText.Parse(runtime.Story.Text).Runs);
        Assert.Equal("sample", run.Font);
        Assert.True(run.Bold);
    }

    [Fact]
    public Task ReverseRevealKeepsTheLayoutAndMasksOnlyTheUnrevealedPrefix() => WorkspaceTabsTests.Sta(() =>
    {
        var text = new TextBlock();
        const string markup = "{size=24}А🙂Б{/size}";
        InlineTextPresentation.Apply(text, markup, visibleSuffix: 2, preserveLayout: true);
        Assert.Equal("А🙂Б", text.Text);
        var runs = text.Inlines.OfType<Run>().ToArray();
        Assert.Equal(new[] { "А", "🙂Б" }, runs.Select(run => run.Text));
        Assert.Equal(Brushes.Transparent, runs[0].Foreground);
        Assert.NotEqual(Brushes.Transparent, runs[1].Foreground);
        Assert.All(runs, run => Assert.Equal(24, run.FontSize));
        InlineTextPresentation.Apply(text, markup);
        Assert.NotEqual(Brushes.Transparent, Assert.Single(text.Inlines.OfType<Run>()).Foreground);
        return Task.CompletedTask;
    });

    [Fact]
    public Task DetachedConsoleRetainsTheJournalAndReturnsItsControlsToTheGameWhenClosed() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = Scene("d(nil, 'game')");
        var project = await fixture.LoadAsync();
        project.Engine.Performance.ConsoleMode = "window";
        project.Engine.Game.DebugEnabled = true;
        var game = new GameWindow(project)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            game.Show();
            var toggle = typeof(GameWindow).GetMethod("SetConsoleVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var panel = (Border)game.FindName("DebugOverlay");
            var output = (TextBlock)game.FindName("ConsoleOutput");
            output.Text = "journal";
            toggle.Invoke(game, [true, false]);
            Assert.NotSame(game, Window.GetWindow(panel));
            Assert.Same(game, Window.GetWindow(panel).Owner);
            Assert.Equal("journal", output.Text);
            toggle.Invoke(game, [false, false]);
            Assert.Same(game, Window.GetWindow(panel));
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.Equal("journal", output.Text);
        }
        finally { await game.CloseForProjectDeletionAsync(); }
    });

    private static TempProject Scene(string body)
    {
        var fixture = new TempProject();
        File.WriteAllText(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl"), "function main()\n" + body + "\nend\n");
        return fixture;
    }
}
