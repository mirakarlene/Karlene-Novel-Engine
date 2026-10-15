using Xunit;

namespace KNE.Tests;

public sealed class ExtendedFlowGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncGraphFailure_ReportsBackgroundBlockIdentity_WithoutBlamingVisibleForeground(bool worker)
    {
        using var fixture = new TempProject();
        var graph = new VisualScript
        {
            Nodes =
            [
                new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "spawn" },
                new() { Id = "spawn", Kind = ScriptNodeKind.Async, Variable = "task", Next = "foreground", Options = [new() { Text = "background", Target = "delay" }] },
                new() { Id = "foreground", Kind = ScriptNodeKind.Dialogue, Text = "Visible foreground", Next = "end" },
                new() { Id = "end", Kind = ScriptNodeKind.End },
                new() { Id = "delay", Kind = ScriptNodeKind.Pause, Duration = .1, Next = "missing-background" },
                new() { Id = "missing-background", Kind = ScriptNodeKind.Background, Path = "game/backgrounds/not-present.png", Next = "branch-end" },
                new() { Id = "branch-end", Kind = ScriptNodeKind.End }
            ]
        };
        var project = await VisualScriptTests.LoadGraphAsync(fixture, graph);
        var catalog = FrameCatalog.Load(project);
        FrameIdentity expected = catalog.Entries.Single(entry => entry.NodeId == "missing-background").Identity;
        await using IGameSession runtime = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Visible foreground", runtime.Story.Text);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.TickAsync(.1));
        var report = Assert.IsType<GameErrorReport>(GameErrorReport.TryGet(error));
        Assert.Equal(expected, report.Frame);
        Assert.Equal("game/scenes/visual.knegraph", report.File);
        Assert.Contains("not-present.png", report.Message);
    }
}
