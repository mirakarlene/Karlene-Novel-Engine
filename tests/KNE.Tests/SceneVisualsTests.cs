using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class SceneVisualsTests
{
    [Fact]
    public async Task LuaResourceCommands_PublishProjectRelativeVisualsAndAudio()
    {
        using var fixture = new TempProject();
        string background = fixture.Write("game/backgrounds/forest.png", "fixture");
        string character = fixture.Write("game/characters/alice.png", "fixture");
        string music = fixture.Write("game/audio/theme.wav", "fixture");
        fixture.Write("game/scenes/main.krl", """
            function main()
                background("game/backgrounds/forest.png")
                show_character("game/characters/alice.png", "left")
                play_music("game/audio/theme.wav", true)
                play_sound("game/audio/theme.wav")
                d(nil, "Scene")
                hide_character()
                stop_music()
                background(nil)
                d(nil, "Cleared")
            end
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal(background, runtime.Visuals.Background);
        Assert.Equal(character, runtime.Visuals.Character);
        Assert.Equal("left", runtime.Visuals.Position);
        Assert.Equal(music, runtime.Visuals.Music);
        Assert.True(runtime.Visuals.LoopMusic);
        Assert.Equal(music, runtime.Visuals.Sound);
        await runtime.AdvanceAsync();
        Assert.Null(runtime.Visuals.Character);
        Assert.Null(runtime.Visuals.Background);
        Assert.Null(runtime.Visuals.Music);
        Assert.Equal(2, runtime.Visuals.MusicVersion);
    }

    [Fact]
    public void ResourcePaths_RejectMissingWrongTypeAndEscapingFiles()
    {
        using var fixture = new TempProject();
        fixture.Write("game/resources/file.txt", "keep");
        Assert.Throws<ArgumentException>(() => ResourcePaths.Resolve(fixture.ProjectPath, "../outside.png"));
        Assert.Throws<ArgumentException>(() => ResourcePaths.Resolve(fixture.ProjectPath, "game/resources/file.txt"));
        Assert.Throws<FileNotFoundException>(() => ResourcePaths.Resolve(fixture.ProjectPath, "game/backgrounds/missing.png"));
    }
}
