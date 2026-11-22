using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectLifecycleTests
{
    [Fact]
    public async Task WorkerRunsPreludeAndMenuSeparatelyFromMainAndCanRenderErrorText()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() error('main must not run') end");
        fixture.Write("game/scenes/intro.krl", "function intro() d(nil, 'intro') end");
        fixture.Write("game/scripts/menu.lua", "print('menu opened')");
        fixture.Write("game/scripts/error.vlua", VisualLua.ParseSource("return 'Custom: ' .. project_event.message").Serialize());
        var project = await Configure(fixture, entries =>
        {
            entries.PreMenuScene = "game/scenes/intro.krl"; entries.MenuScript = "game/scripts/menu.lua";
            entries.ErrorScript = "game/scripts/error.vlua";
        });
        await using (var scene = new ProcessGameSession(project, _ => { }))
        {
            await scene.StartPreMenuAsync(CancellationToken.None);
            Assert.Equal("intro", scene.Story.Text);
            await scene.AdvanceAsync();
            Assert.True(scene.Story.Completed);
        }
        var output = new List<string>();
        await using var menu = new ProcessGameSession(project, output.Add);
        await menu.RunProjectEventAsync(new("menu"), CancellationToken.None);
        Assert.Contains("menu opened", output);
        await using var error = new ProcessGameSession(project, _ => { });
        Assert.Equal("Custom: cause", await error.RunProjectEventAsync(new("error", "cause"), CancellationToken.None));
    }

    [Fact]
    public Task WindowShowsIntroBeforeMenuAndRunsExitScriptBeforeConfirmation() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/intro.krl", "function intro() d(nil, 'opening scene') end");
        fixture.Write("game/scripts/menu.lua", "kne.write_file('menu-event.txt', 'menu')");
        fixture.Write("game/scripts/exit.lua", "kne.write_file('exit-event.txt', 'exit')");
        var project = await Configure(fixture, entries =>
        {
            entries.PreMenuScene = "game/scenes/intro.krl"; entries.MenuScript = "game/scripts/menu.lua";
            entries.ExitScript = "game/scripts/exit.lua";
        });
        string menuFile = Path.Combine(project.Path, "menu-event.txt"), exitFile = Path.Combine(project.Path, "exit-event.txt");
        var game = new GameWindow(project, new GameSaveStore(project.Path).WithInstantDialogue()) { ShowActivated = false, ShowInTaskbar = false };
        bool confirmationReached = false;
        try
        {
            var menu = (Border)game.FindName("MenuOverlay");
            var next = (Button)game.FindName("NextButton");
            var dialogue = (TextBlock)game.FindName("DialogueText");
            game.ConfirmProjectExit = () => { confirmationReached = File.Exists(exitFile); return false; };
            Assert.Equal(Visibility.Collapsed, menu.Visibility);
            game.Show();
            await WaitUntil(() => dialogue.Text == "opening scene" && next.IsEnabled);
            Assert.Equal(Visibility.Collapsed, menu.Visibility);
            Assert.False(File.Exists(menuFile));
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => File.Exists(menuFile));
            Assert.Equal(Visibility.Visible, menu.Visibility);
            game.Close();
            await WaitUntil(() => confirmationReached);
            Assert.True(game.IsVisible);
        }
        finally { await game.CloseForProjectDeletionAsync(); }

        static async Task WaitUntil(Func<bool> done)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            while (!done()) await Task.Delay(20, timeout.Token);
        }
    });

    [Fact]
    public async Task NamedSaveEventRunsAfterWritingAndItsStateSurvivesCheckpointRestore()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                d(nil, 'start')
                save_named('after_intro')
                d(nil, state.last_slot)
            end
            """);
        fixture.Write("game/scripts/save.lua", "assert(save_exists(project_event.slot)); state.last_slot = project_event.slot");
        var project = await Configure(fixture, entries => entries.SaveScript = "game/scripts/save.lua");
        await using var session = new ProcessGameSession(project, _ => { });
        await session.StartAsync();
        Assert.Equal("start", session.Story.Text);
        await session.AdvanceAsync();
        Assert.Equal("after_intro", session.Story.Text);
        Assert.True(new GameSaveStore(project.Path).Exists("after_intro"));
        var checkpoint = await session.GetCheckpointAsync();
        await using var restored = new ProcessGameSession(project, _ => { });
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("after_intro", restored.Story.Text);
    }

    [Fact]
    public async Task KnegraphCallsVisualLuaEachTimeWithoutChangingItsExtension()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function unused() end");
        fixture.Write("game/scripts/step.vlua", VisualLua.ParseSource("state.steps = (state.steps or 0) + 1").Serialize());
        var graph = VisualScript.Create();
        var start = graph.Nodes.Single(node => node.Kind == ScriptNodeKind.Start);
        graph.Nodes.RemoveAll(node => node.Kind == ScriptNodeKind.Dialogue);
        start.Next = "call1";
        graph.Nodes.AddRange([
            new ScriptNode { Id = "call1", Kind = ScriptNodeKind.CallVisualLua, Path = "game/scripts/step.vlua", Next = "call2" },
            new ScriptNode { Id = "call2", Kind = ScriptNodeKind.CallVisualLua, Path = "game/scripts/step.vlua", Next = "read" },
            new ScriptNode { Id = "read", Kind = ScriptNodeKind.Dialogue, Text = "{steps}", Next = "end" }
        ]);
        fixture.Write("game/scenes/start.knegraph", graph.Serialize());
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.Equal("2", runtime.Story.Text);
    }

    private static async Task<ProjectInfo> Configure(TempProject fixture, Action<EntryPoints> change)
    {
        var project = await fixture.LoadAsync();
        change(project.Configuration.EntryPoints!);
        await File.WriteAllTextAsync(Path.Combine(project.Path, "project.json"), JsonSerializer.Serialize(project.Configuration, ProjectConfiguration.JsonOptions));
        return await fixture.LoadAsync();
    }
}
