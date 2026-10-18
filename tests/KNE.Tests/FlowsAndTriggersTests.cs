using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class FlowsAndTriggersTests
{
    [Fact]
    public void Catalog_FlowsAndTriggersFollowAudioAndAsync_AndLegacyTransitionsMigrateWithPreferences()
    {
        Assert.Equal(new[] { "audio", "flows", "calculations", "async", "triggers", "configurations" }, BlockLibrary.Groups.Skip(4).Take(6).Select(group => group.Id));
        Assert.Equal(new[] { ScriptNodeKind.GotoScene, ScriptNodeKind.CallScene, ScriptNodeKind.Subsystem }, BlockLibrary.Definitions.Where(entry => entry.Group == "flows").Select(entry => entry.Kind));
        Assert.Equal(new[] { ScriptNodeKind.TriggerKey, ScriptNodeKind.TriggerButton, ScriptNodeKind.TriggerHotspot, ScriptNodeKind.TriggerTextClosed, ScriptNodeKind.TriggerCombination }, BlockLibrary.Definitions.Where(entry => entry.Group == "triggers").Select(entry => entry.Kind));
        foreach (var kind in new[] { ScriptNodeKind.GotoScene, ScriptNodeKind.CallScene })
        {
            Assert.DoesNotContain(BlockLibrary.Definitions, entry => entry.Kind == kind && entry.Group == "narrative");
            var graph = VisualScript.Create();
            graph.Nodes.Add(new() { Id = "legacy", Kind = kind, Category = "narrative", Path = "chapter", Next = kind == ScriptNodeKind.CallScene ? "end" : null });
            var migrated = VisualScript.Parse(graph.Serialize()).Nodes.Single(node => node.Id == "legacy");
            Assert.Equal("flows", migrated.Category);
            Assert.Equal(kind, migrated.Kind);
            Assert.Equal("chapter", migrated.Path);
            Assert.Equal(kind == ScriptNodeKind.CallScene ? "end" : null, migrated.Next);
        }
        var preferences = new BlockLibraryPreferences
        {
            Favorites = ["narrative/GotoScene", "flows/GotoScene", "narrative/CallScene", "triggers/TriggerKey", "obsolete"],
            Recent = ["narrative/CallScene", "flows/CallScene", "narrative/GotoScene", "flows/Subsystem"]
        };
        preferences.Normalize();
        Assert.Equal(new[] { "flows/GotoScene", "flows/CallScene", "triggers/TriggerKey" }, preferences.Favorites);
        Assert.Equal(new[] { "flows/CallScene", "flows/GotoScene", "flows/Subsystem" }, preferences.Recent);
    }

    [Theory]
    [InlineData(" control + e + LEFTCTRL ", "Ctrl+E")]
    [InlineData("e+SHIFT+ctrl", "Ctrl+E+Shift")]
    [InlineData("RightAlt+e+LWin", "Alt+E+Win")]
    [InlineData("F12", "F12")]
    public void Keys_AliasesOrderCaseAndDuplicates_AreCanonical(string chord, string expected)
    {
        Assert.Equal(expected, TriggerSettings.NormalizeKeys(chord));
        Assert.True(new TriggerSettings { Keys = expected }.Matches(new TriggerSignal("key", chord)));
        Assert.False(new TriggerSettings { Keys = expected }.Matches(new TriggerSignal("button", Button: "next")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("NotAKey")]
    [InlineData("None")]
    [InlineData("System")]
    [InlineData("A+B+C+D+E+F+G+H+I")]
    public void Keys_InvalidChords_AreRejectedBySettingsAndSignals(string chord)
    {
        Assert.Throws<ArgumentException>(() => TriggerSettings.NormalizeKeys(chord));
        Assert.Throws<ArgumentException>(() => new TriggerSettings { Keys = chord }.Validate());
        Assert.Throws<ArgumentException>(() => new TriggerSignal("key", chord).Validate());
    }

    [Fact]
    public void TriggerModels_BoundsAndMatching_UseFiniteHalfOpenLogicalCoordinatesAndValidIds()
    {
        var hotspot = new TriggerSettings { Kind = "hotspot", X = 10, Y = 20, Width = 30, Height = 40 };
        hotspot.Validate();
        Assert.True(hotspot.Matches(new TriggerSignal("hotspot", X: 10, Y: 20)));
        Assert.True(hotspot.Matches(new TriggerSignal("hotspot", X: 39.999, Y: 59.999)));
        Assert.False(hotspot.Matches(new TriggerSignal("hotspot", X: 40, Y: 20)));
        Assert.False(hotspot.Matches(new TriggerSignal("hotspot", X: 10, Y: 60)));
        Assert.False(hotspot.Matches(new TriggerSignal("hotspot", X: 9.999, Y: 20)));
        Assert.False(hotspot.Matches(new TriggerSignal("hotspot", X: 10, Y: 19.999)));
        new TriggerSettings { X = 99999, Y = 99999, Width = 1, Height = 1 }.Validate();
        foreach (var invalid in new[] { hotspot with { X = -1 }, hotspot with { Y = -1 }, hotspot with { Width = 0 }, hotspot with { Height = 0 }, hotspot with { X = 100000 }, hotspot with { Y = 100000 }, hotspot with { Width = double.NaN }, hotspot with { Height = double.PositiveInfinity } })
            Assert.Throws<ArgumentException>(invalid.Validate);
        Assert.True(new TriggerSettings { Kind = "button", Button = "next" }.Matches(new TriggerSignal("button", Button: "NEXT")));
        Assert.False(new TriggerSettings { Kind = "button", Button = "next" }.Matches(new TriggerSignal("button", Button: "save")));
        Assert.True(new TriggerSettings { Kind = "text_closed" }.Matches(new TriggerSignal("text_closed")));
        Assert.Throws<ArgumentException>(() => new TriggerSignal("unknown").Validate());
        Assert.Throws<ArgumentException>(() => new TriggerSignal("button", Button: " ").Validate());
        new TriggerSignal("button", Button: new string('x', 256)).Validate();
        Assert.Throws<ArgumentException>(() => new TriggerSignal("button", Button: new string('x', 257)).Validate());
        Assert.Throws<ArgumentException>(() => new TriggerSignal("hotspot", X: double.NaN).Validate());
        Assert.Throws<ArgumentException>(() => new TriggerSignal("hotspot", Y: double.NegativeInfinity).Validate());
        Assert.Throws<ArgumentException>(() => TriggerSettings.NormalizeKeys(new string('A', 161)));
        Assert.Throws<ArgumentException>(() => TriggerSettings.NormalizeKeys(null!));
    }

    [Theory]
    [InlineData(ScriptNodeKind.TriggerKey)]
    [InlineData(ScriptNodeKind.TriggerButton)]
    [InlineData(ScriptNodeKind.TriggerHotspot)]
    [InlineData(ScriptNodeKind.TriggerTextClosed)]
    [InlineData(ScriptNodeKind.TriggerCombination)]
    public void Graph_TriggerRootsCompileIndependently_AndConnectedTriggersCompileAsWaits(ScriptNodeKind kind)
    {
        var graph = VisualScript.Create();
        var trigger = BlockLibrary.Create(kind); trigger.Id = "trigger"; trigger.Next = "handler";
        graph.Nodes.Add(trigger);
        graph.Nodes.Add(new() { Id = "handler", Kind = ScriptNodeKind.ConsoleWrite, Text = "event", Next = "handlerend" });
        graph.Nodes.Add(new() { Id = "handlerend", Kind = ScriptNodeKind.End });
        var parsed = VisualScript.Parse(graph.Serialize());
        string code = parsed.Compile();
        Assert.Contains("kne_on_event(", code);
        Assert.True(code.IndexOf("    kne_on_event(", StringComparison.Ordinal) < code.IndexOf("    __kne_run(\"start\"", StringComparison.Ordinal));
        Assert.Equal(("Далее", (string?)"handler"), Assert.Single(trigger.Outputs()));
        Assert.Equal("triggers", trigger.Category);
        Assert.False(trigger.Trigger!.Once);
        trigger.Trigger.Validate();
        trigger.SetOutput(0, null);
        Assert.Contains("соедините", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        trigger.SetOutput(0, "handler");
        graph.Nodes.Single(node => node.Id == "dialogue").Next = "trigger";
        graph.Nodes.Single(node => node.Id == "handler").Next = "end";
        graph.Nodes.RemoveAll(node => node.Id == "handlerend");
        string connected = graph.Compile();
        Assert.Contains("kne_await_event(", connected);
        Assert.DoesNotContain("    kne_on_event(", connected);
    }

    [Fact]
    public void Graph_TriggerKindMismatchAndSingleKeyChord_ReportAuthoringErrors()
    {
        var graph = VisualScript.Create();
        var trigger = BlockLibrary.Create(ScriptNodeKind.TriggerKey); trigger.Id = "key"; trigger.Next = "end";
        graph.Nodes.Add(trigger);
        trigger.Trigger = new TriggerSettings { Kind = "button" };
        Assert.Contains("тип события", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        trigger.Trigger = new TriggerSettings { Keys = "Ctrl+E" };
        Assert.Contains("для сочетания", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
        trigger.Trigger = null;
        Assert.Contains("настройте событие", Assert.Throws<InvalidDataException>(() => graph.Compile()).Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Graph_Subsystem_RoundTripsWaitModeAndCompilesAQuotedPath(bool wait)
    {
        var graph = VisualScript.Create();
        graph.Nodes.Single(node => node.Id == "dialogue").Next = "child";
        graph.Nodes.Add(new() { Id = "child", Kind = ScriptNodeKind.Subsystem, Category = "flows", Path = "game/scenes/Моя подсистема.knegraph", WaitForSubsystem = wait, Next = "end" });
        var parsed = VisualScript.Parse(graph.Serialize());
        var subsystem = parsed.Nodes.Single(node => node.Id == "child");
        Assert.Equal(wait, subsystem.WaitForSubsystem);
        Assert.Equal("Моя подсистема.knegraph", subsystem.Title);
        Assert.Contains($"kne_subsystem(\"game/scenes/Моя подсистема.knegraph\", {(wait ? "true" : "false")})", parsed.Compile());
        subsystem.Path = "game/scenes/child.lua";
        Assert.Contains(".knegraph", Assert.Throws<InvalidDataException>(() => parsed.Compile()).Message);
        Assert.True(BlockLibrary.Create(ScriptNodeKind.Subsystem).WaitForSubsystem);
    }
}
