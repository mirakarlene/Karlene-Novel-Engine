using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ScreenLayoutsTests
{
    [Fact]
    public async Task NewProject_DefinesGameScreensWithSmallerButtonsAndFunctionalSettings()
    {
        using var fixture = new TempProject();
        var layouts = (await fixture.LoadAsync()).Engine.Screens;
        Assert.Equal(new[] { "gameplay", "main_menu", "pause", "saves", "settings" }, layouts.Screens.Select(s => s.Kind).OrderBy(s => s));
        var main = layouts.Find("main_menu")!;
        Assert.Equal(new[] { "start", "continue", "settings", "saves", "exit" }, main.Elements.Where(e => e.Type == "button").Select(e => e.Action));
        Assert.All(main.Elements.Where(e => e.Type == "button"), e => Assert.Equal(300, e.Width));
        Assert.DoesNotContain(main.Elements, e => e.Text.Contains("ВИЗУАЛЬНАЯ НОВЕЛЛА"));
        Assert.Equal(new[] { "fullscreen", "animation", "music_volume", "sound_volume", "back" }, layouts.Find("settings")!.Elements.Where(e => e.Type != "text").Select(e => e.Action));
        Assert.Empty(layouts.Find("gameplay")!.Elements);
    }

    [Fact]
    public void ScreenCrud_RenamesAndDeletesCustomScreens_ProtectsReferencesAndGameplay()
    {
        var layouts = ScreenLayouts.CreateDefaults();
        var custom = layouts.AddScreen("Галерея");
        custom.Name = "Галерея иллюстраций";
        var link = new ScreenElement { Action = "navigate", Target = custom.Id };
        layouts.Find("main_menu")!.Elements.Add(link);
        layouts.Validate();
        Assert.Throws<InvalidOperationException>(() => layouts.RemoveScreen(custom.Id));
        Assert.Contains(custom, layouts.Screens);
        Assert.Throws<InvalidOperationException>(() => layouts.RemoveScreen("gameplay"));
        layouts.Find("main_menu")!.Elements.Remove(link);
        layouts.RemoveScreen(custom.Id);
        Assert.DoesNotContain(layouts.Screens, s => s.Id == custom.Id);
        layouts.RemoveScreen("settings");
        Assert.Null(layouts.Find("settings"));
        var replacement = layouts.AddScreen("Новые настройки"); replacement.Kind = "settings";
        layouts.Validate(); Assert.Same(replacement, layouts.Find("settings"));
    }

    [Theory]
    [InlineData("{\"x\":-1}")]
    [InlineData("{\"x\":1000,\"width\":300}")]
    [InlineData("{\"height\":721}")]
    [InlineData("{\"duration\":0}")]
    [InlineData("{\"duration\":61}")]
    [InlineData("{\"motion_x\":1281}")]
    [InlineData("{\"motion_y\":-721}")]
    [InlineData("{\"delay\":-1}")]
    [InlineData("{\"motion_order\":101}")]
    [InlineData("{\"font_size\":9}")]
    [InlineData("{\"type\":\"html\"}")]
    [InlineData("{\"action\":\"shell\"}")]
    [InlineData("{\"type\":\"toggle\",\"action\":\"start\"}")]
    [InlineData("{\"type\":\"slider\",\"action\":\"exit\"}")]
    [InlineData("{\"action\":\"navigate\",\"target\":\"missing\"}")]
    [InlineData("{\"text\":null}")]
    public void InvalidLayout_RejectsBoundsTypesAndDanglingTransitions(string element)
    {
        string json = "{\"screens\":[{\"id\":\"main\",\"name\":\"Main\",\"kind\":\"main_menu\",\"elements\":[" + element + "]}]}";
        Assert.Throws<InvalidDataException>(() => ScreenLayouts.Parse(json));
    }

    [Fact]
    public void Layout_RejectsDuplicateIdentitiesRolesAndCapacity()
    {
        var layouts = ScreenLayouts.CreateDefaults();
        var added = layouts.AddScreen("custom"); added.Kind = "pause";
        Assert.Throws<InvalidDataException>(layouts.Validate);
        added.Kind = "custom"; added.Id = "main_menu";
        Assert.Throws<InvalidDataException>(layouts.Validate);
        added.Id = "new";
        var main = layouts.Find("main_menu")!;
        main.Elements[1].Id = main.Elements[0].Id;
        Assert.Throws<InvalidDataException>(layouts.Validate);
        main.Elements[1].Id = "subtitle";
        while (layouts.Screens.Count < 32) layouts.AddScreen("custom " + layouts.Screens.Count);
        Assert.Throws<InvalidOperationException>(() => layouts.AddScreen("overflow"));
        layouts.Validate();
    }

    [Fact]
    public void ExactMotionBoundaries_AndLayerOrder_RoundTrip()
    {
        var layouts = ScreenLayouts.CreateDefaults();
        var main = layouts.Find("main_menu")!;
        var a = main.Elements[0];
        a.X = 0; a.Y = 0; a.Width = 1280; a.Height = 720; a.MotionX = -1280; a.MotionY = 720;
        a.Delay = 60; a.Duration = 0.1; a.MotionOrder = 100; a.Loop = true;
        main.Elements.Remove(a); main.Elements.Add(a);
        var parsed = ScreenLayouts.Parse(layouts.Serialize()).Find("main_menu")!.Elements.Last();
        Assert.Equal("title", parsed.Id); Assert.Equal(-1280, parsed.MotionX); Assert.Equal(720, parsed.MotionY);
        Assert.Equal(0.1, parsed.Duration); Assert.Equal(60, parsed.Delay); Assert.Equal(100, parsed.MotionOrder); Assert.True(parsed.Loop);
    }

    [Fact]
    public async Task AppearanceSave_WritesThemeAndVectorsAtomically_RejectsExternalChanges()
    {
        using var fixture = new TempProject();
        var document = await AppearanceDocument.OpenAsync(await fixture.LoadAsync());
        document.Layout.Theme!.BackgroundColor = "#FF102030";
        document.Layout.Find("main_menu")!.Elements.Single(e => e.Action == "start").MotionY = 40;
        Assert.True(document.IsDirty);
        await document.SaveAsync(); Assert.False(document.IsDirty);
        var loaded = await fixture.LoadAsync();
        Assert.Equal("#FF102030", loaded.Engine.Theme.BackgroundColor);
        Assert.Equal(40, loaded.Engine.Screens.Find("main_menu")!.Elements.Single(e => e.Action == "start").MotionY);
        string path = Path.Combine(fixture.ProjectPath, "engine", "screens.json");
        string external = document.Layout.Serialize() + "\n"; File.WriteAllText(path, external);
        document.Layout.Find("main_menu")!.Name = "draft";
        await Assert.ThrowsAsync<IOException>(() => document.SaveAsync());
        Assert.Equal(external, File.ReadAllText(path)); Assert.True(document.IsDirty);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }
}
