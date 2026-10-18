using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class AccessibilitySettingsTests
{
    [Fact]
    public async Task AccessibilityPreferences_DefaultAndLegacySettingsStayNormalWithBubbles_AndInvalidValuesRecover()
    {
        using var fixture = new SettingsFixture();
        Assert.Equal("normal", new UserSettings().AccessibilityMode);
        Assert.Equal("bubbles", new UserSettings().WelcomeObjects);
        var missing = await fixture.Store.LoadAsync();
        Assert.Equal("normal", missing.AccessibilityMode); Assert.Equal("bubbles", missing.WelcomeObjects);
        foreach (string json in new[]
        {
            "{}", "{\"AccessibilityMode\":null,\"WelcomeObjects\":null}",
            "{\"AccessibilityMode\":\"unknown\",\"WelcomeObjects\":\"unknown\"}",
            "{\"AccessibilityMode\":\"\",\"WelcomeObjects\":\"\"}"
        })
        {
            await File.WriteAllTextAsync(fixture.Store.SettingsPath, json);
            var settings = await fixture.Store.LoadAsync();
            Assert.Equal("normal", settings.AccessibilityMode); Assert.Equal("bubbles", settings.WelcomeObjects);
            Assert.Equal("kne", settings.Theme);
        }
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("protanopia")]
    [InlineData("deuteranopia")]
    [InlineData("tritanopia")]
    [InlineData("achromatopsia")]
    [InlineData("high_contrast")]
    public async Task AccessibilityPreferences_AllSixModesRoundTripWithoutLosingProjectHistoryOrDesign(string mode)
    {
        using var fixture = new SettingsFixture();
        string recent = Path.Combine(fixture.Root, "recent"), hidden = Path.Combine(fixture.Root, "hidden");
        var opened = new DateTimeOffset(2026, 10, 15, 11, 12, 13, TimeSpan.Zero);
        await fixture.Store.SaveAsync(new UserSettings
        {
            AccessibilityMode = mode, WelcomeObjects = "petals", Theme = "ocean", UserName = "  Алиса  ",
            EditorPath = "editor.exe", ProjectsStoragePath = fixture.Root,
            RecentProjects = [recent], HiddenProjects = [hidden], ProjectLastOpened = new() { [recent] = opened }
        });
        var loaded = await fixture.Store.LoadAsync();
        Assert.Equal(mode, loaded.AccessibilityMode); Assert.Equal("petals", loaded.WelcomeObjects);
        Assert.Equal("ocean", loaded.Theme); Assert.Equal("Алиса", loaded.UserName);
        Assert.Equal("editor.exe", loaded.EditorPath); Assert.Equal(fixture.Root, loaded.ProjectsStoragePath);
        Assert.Equal([recent], loaded.RecentProjects); Assert.Equal([hidden], loaded.HiddenProjects);
        Assert.Equal(opened, loaded.ProjectLastOpened[recent]);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Store.SettingsPath));
        Assert.Equal(mode, stored.RootElement.GetProperty("AccessibilityMode").GetString());
    }

    [Theory]
    [InlineData("bubbles")]
    [InlineData("stars")]
    [InlineData("petals")]
    [InlineData("crystals")]
    [InlineData("fireflies")]
    public async Task WelcomeObjects_AllFiveKindsRoundTripWithoutReplacingAccessibilitySelection(string kind)
    {
        using var fixture = new SettingsFixture();
        await fixture.Store.SaveAsync(new UserSettings { WelcomeObjects = kind, AccessibilityMode = "tritanopia" });
        var settings = await fixture.Store.LoadAsync();
        Assert.Equal(kind, settings.WelcomeObjects); Assert.Equal("tritanopia", settings.AccessibilityMode);
    }

    internal sealed class SettingsFixture : IDisposable
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kne-accessibility-settings-tests");
        public string Root { get; } = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        public UserSettingsStore Store { get; }
        public SettingsFixture() { Directory.CreateDirectory(Root); Store = new(Path.Combine(Root, "settings.json")); }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(target, true);
        }
    }
}
