using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ResourceCacheSettingsTests
{
    [Fact]
    public void CacheBudget_DefaultIsEnabledAtSixtyFourMebibytesAndLegacyFilesRecoverThoseDefaults()
    {
        var value = new GraphicsSettings(); Assert.True(value.LimitResourceCache); Assert.Equal(64, value.ResourceCacheLimitMb);
        var legacy = JsonSerializer.Deserialize<GraphicsSettings>("{\"Api\":\"opengl\"}")!;
        Assert.True(legacy.LimitResourceCache); Assert.Equal(64, legacy.ResourceCacheLimitMb); Assert.Equal("opengl", legacy.Api);
        Assert.Equal(value, GraphicsSettings.Normalize(null));
    }

    [Theory]
    [InlineData(16)] [InlineData(32)] [InlineData(64)] [InlineData(128)] [InlineData(256)] [InlineData(512)]
    public void CacheBudget_AllSupportedChoicesRemainIndependentOfApiAndCanBeDisabled(int size)
    {
        foreach (string api in new[] { "auto", "vulkan", "opengl" })
        foreach (bool enabled in new[] { false, true })
        {
            var value = new GraphicsSettings { Api = api, LimitResourceCache = enabled, ResourceCacheLimitMb = size };
            value.Validate(); Assert.Equal(value, GraphicsSettings.Normalize(value));
        }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(15)] [InlineData(17)] [InlineData(63)] [InlineData(65)] [InlineData(511)] [InlineData(513)]
    public void CacheBudget_RejectsInvalidAndAdjacentSizesAndNormalizationDoesNotAlterOtherPreferences(int size)
    {
        var source = new GraphicsSettings { Api = "opengl", VSync = false, MaxFps = 144, MsaaSamples = 4, LimitResourceCache = false, ResourceCacheLimitMb = size };
        Assert.Throws<InvalidDataException>(source.Validate);
        var normalized = GraphicsSettings.Normalize(source);
        Assert.Equal(64, normalized.ResourceCacheLimitMb); Assert.False(normalized.LimitResourceCache);
        Assert.Equal("opengl", normalized.Api); Assert.False(normalized.VSync); Assert.Equal(144, normalized.MaxFps); Assert.Equal(4, normalized.MsaaSamples);
        Assert.Equal(size, source.ResourceCacheLimitMb);
    }

    [Fact]
    public async Task CacheBudget_EditorAndProjectPersistIndependentPoliciesWithoutLosingTheExplicitApi()
    {
        using var fixture = new TempProject(); using var user = new AccessibilitySettingsTests.SettingsFixture();
        var editor = new GraphicsSettings { Api = "vulkan", LimitResourceCache = true, ResourceCacheLimitMb = 16 };
        var game = new GraphicsSettings { Api = "opengl", LimitResourceCache = false, ResourceCacheLimitMb = 256 };
        await user.Store.SaveAsync(new() { Graphics = editor });
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = game });
        Assert.Equal(editor, (await user.Store.LoadAsync()).Graphics);
        Assert.Equal(game, PerformanceSettingsStore.LoadProject(fixture.ProjectPath).Graphics);
        Assert.Equal(game, (await fixture.LoadAsync()).Engine.Performance.Graphics);
    }

    [Fact]
    public async Task CacheBudget_InvalidUserPreferencesRecoverButInvalidProjectWritesCannotOverwriteThePreviousBudget()
    {
        using var fixture = new TempProject(); using var user = new AccessibilitySettingsTests.SettingsFixture();
        await File.WriteAllTextAsync(user.Store.SettingsPath, "{\"Graphics\":{\"Api\":\"opengl\",\"LimitResourceCache\":false,\"ResourceCacheLimitMb\":17}}");
        var recovered = (await user.Store.LoadAsync()).Graphics;
        Assert.Equal("opengl", recovered.Api); Assert.False(recovered.LimitResourceCache); Assert.Equal(64, recovered.ResourceCacheLimitMb);
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { ResourceCacheLimitMb = 32 } });
        string path = Path.Combine(fixture.ProjectPath, "engine", "performance.json"); byte[] original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { ResourceCacheLimitMb = 17 } }));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(32, PerformanceSettingsStore.LoadProject(fixture.ProjectPath).Graphics.ResourceCacheLimitMb);
    }

    [Fact]
    public Task CacheBudget_SharedEditorRoundTripsTheToggleAndSizeAndKeepsTheChosenBudgetWhenDisabled() => WorkspaceTabsTests.Sta(() =>
    {
        var input = new GraphicsSettingsEditor();
        var enabled = new GraphicsSettings { Api = "opengl", MaxFps = 144, LimitResourceCache = true, ResourceCacheLimitMb = 32 };
        input.Value = enabled; Assert.Equal(enabled, input.Value); Assert.True(input.CacheLimitInput.IsEnabled);
        Assert.Equal(new[] { 16, 32, 64, 128, 256, 512 }, input.CacheLimitInput.Items.Cast<GraphicsOption<int>>().Select(item => item.Value));
        var disabled = enabled with { LimitResourceCache = false, ResourceCacheLimitMb = 128 };
        input.Value = disabled; Assert.Equal(disabled, input.Value); Assert.False(input.CacheLimitInput.IsEnabled);
        input.LimitCacheInput.IsChecked = true; Assert.Equal(128, input.Value.ResourceCacheLimitMb); Assert.True(input.Value.LimitResourceCache); Assert.True(input.CacheLimitInput.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task SceneSettings_KrlPathsRoundTripNormalizeAndStayIndependentFromGraphOverrides()
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new()
        {
            ["game\\scenes\\chapter.krl"] = new() { EffectsQuality = "low", PostProcessing = false },
            ["game/scenes/chapter.knegraph"] = new() { EffectsQuality = "medium" }
        } });
        var loaded = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath);
        Assert.Equal(2, loaded.Scenes.Count); Assert.Contains("game/scenes/chapter.krl", loaded.Scenes.Keys);
        Assert.Equal("low", loaded.For("GAME/SCENES/CHAPTER.KRL").EffectsQuality); Assert.False(loaded.For("game/scenes/chapter.krl").PostProcessing);
        Assert.Equal("medium", loaded.For("game/scenes/chapter.knegraph").EffectsQuality); Assert.True(loaded.For("game/scenes/chapter.knegraph").PostProcessing);
        Assert.Equal("low", (await fixture.LoadAsync()).Engine.ScenePerformance.For("game/scenes/chapter.krl").EffectsQuality);
    }
}
