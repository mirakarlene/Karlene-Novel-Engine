using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class SceneSavePolicyTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, true)]
    public void AutosaveRequiresBothScenePermissionsWhileManualSaveDependsOnlyOnTheSavePermission(bool saves, bool autosave, bool allowed)
    {
        var scene = Scene(1, new() { AllowSaves = saves, AllowAutosave = autosave });
        Assert.Equal(saves, SceneSavePolicy.AllowManual(scene)); Assert.Equal(allowed, SceneSavePolicy.AllowAuto(scene));
        Assert.True(SceneSavePolicy.AllowManual(null)); Assert.True(SceneSavePolicy.AllowAuto(null));
    }

    [Theory]
    [InlineData("dialogue")] [InlineData("choice")] [InlineData("input")] [InlineData("wait")] [InlineData("text_pause")]
    public void EntryCheckpointUsesTheFirstInteractiveFrameOfEachVisitEvenWhenAutosaveIsDisabled(string kind)
    {
        var policy = new SceneSavePolicy(); var scene = Scene(1, new() { CheckpointOnEntry = true, AllowAutosave = false }); var frame = new StoryFrame(kind, "", "", []);
        Assert.False(policy.ShouldCreateEntry(scene, new("preview", "", "", []))); Assert.True(policy.ShouldCreateEntry(scene, frame)); Assert.False(policy.ShouldCreateEntry(scene, frame));
        Assert.True(policy.ShouldCreateEntry(scene with { Visit = 2 }, frame)); Assert.False(policy.ShouldCreateEntry(scene, frame));
    }

    [Theory]
    [InlineData("completed")] [InlineData("clear_text")] [InlineData("preview")] [InlineData("")]
    public void NoninteractiveFramesDoNotConsumeThePendingEntryCheckpoint(string kind)
    {
        var policy = new SceneSavePolicy(); var scene = Scene(1, new() { CheckpointOnEntry = true });
        Assert.False(policy.ShouldCreateEntry(scene, new(kind, "", "", []))); Assert.True(policy.ShouldCreateEntry(scene, new("dialogue", "", "first", [])));
        Assert.False(policy.ShouldCreateEntry(null, new("dialogue", "", "", [])));
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public void DisabledCheckpointsAndForbiddenSavesNeverCreateAnEntry(bool saves, bool checkpoint)
    {
        var policy = new SceneSavePolicy(); var scene = Scene(1, new() { AllowSaves = saves, CheckpointOnEntry = checkpoint });
        Assert.False(policy.ShouldCreateEntry(scene, new("dialogue", "", "", []))); Assert.False(policy.ShouldCreateEntry(scene, new("choice", "", "", [])));
    }

    [Fact]
    public void RestoringSuppressesAllReplayedVisitsIncludingCallersAndResetAllowsFreshVisitsAfterRollback()
    {
        var policy = new SceneSavePolicy(); var settings = new ScenePerformanceSettings { CheckpointOnEntry = true }; var frame = new StoryFrame("dialogue", "", "", []);
        policy.MarkRestored(Scene(2, settings) with { LastVisit = 5 });
        Assert.False(policy.ShouldCreateEntry(Scene(1, settings), frame)); Assert.False(policy.ShouldCreateEntry(Scene(2, settings), frame)); Assert.False(policy.ShouldCreateEntry(Scene(5, settings), frame));
        Assert.True(policy.ShouldCreateEntry(Scene(6, settings), frame)); Assert.False(policy.ShouldCreateEntry(Scene(6, settings), frame));
        policy.Reset(); policy.MarkRestored(Scene(1, settings) with { LastVisit = 2 });
        Assert.False(policy.ShouldCreateEntry(Scene(2, settings), frame)); Assert.True(policy.ShouldCreateEntry(Scene(3, settings), frame)); Assert.True(policy.ShouldCreateEntry(Scene(6, settings), frame));
        policy.Reset(); policy.MarkRestored(null); Assert.True(policy.ShouldCreateEntry(Scene(1, settings), frame));
    }

    [Fact]
    public void EntrySlotsHaveSafeBoundedLabelsAndStableIdentityForFunctionsWithinTheSameFile()
    {
        using var fixture = new TempProject(); var slots = new GameSaveStore(fixture.ProjectPath, Path.Combine(fixture.Root, "slots"));
        var first = Scene(1, new() { Name = "Глава /?*:..\n" }); string name = SceneSavePolicy.SlotName(first);
        Assert.Matches("^Вход — Глава — [A-F0-9]{16}$", name); Assert.Equal(name, SceneSavePolicy.SlotName(first with { Visit = 99 }));
        Assert.NotEqual(name, SceneSavePolicy.SlotName(first with { Function = "other" })); Assert.NotEqual(name, SceneSavePolicy.SlotName(first with { File = "other.krl" }));
        Assert.Equal(Path.Combine(slots.DirectoryPath, name + ".json"), slots.SlotPath(name));
        string longName = SceneSavePolicy.SlotName(first with { Settings = new() { Name = new string('x', 128) } }); Assert.StartsWith("Вход — " + new string('x', 40) + " — ", longName); Assert.True(longName.Length <= 80);
        Assert.StartsWith("Вход — chapter — ", SceneSavePolicy.SlotName(Scene(1, new())));
    }

    static SceneRuntimeState Scene(long visit, ScenePerformanceSettings settings) => new("chapters.krl", "chapter", visit, settings);
}
