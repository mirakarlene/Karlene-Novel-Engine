using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class SceneParametersEditingTests
{
    [Fact]
    public Task AllSceneCardsKeepIndependentFunctionDraftsAndSaveTheirOptionsWithAtomicStaticReferenceRenaming() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("music/theme.wav", "resource fixture"); fixture.Write("effects/entry.kneshader", ShaderGraphCompiler.Create().Serialize());
        string source = fixture.Write("chapters.krl", "function opening() d(nil,'opening text') end\nfunction ending() end");
        string main = fixture.Write("game/scenes/main.krl", "function main() call_scene(opening) end");
        var graph = VisualScript.Create(); graph.FunctionName = "visual"; graph.Nodes.Single(node => node.Id == "dialogue").Kind = ScriptNodeKind.CallScene; graph.Nodes.Single(node => node.Id == "dialogue").Path = "opening";
        string graphPath = fixture.Write("visual.knegraph", graph.Serialize()); var project = await fixture.LoadAsync();
        byte[] projectSettings = File.ReadAllBytes(Path.Combine(project.Path, "engine/performance.json")); var window = new SceneParametersWindow(project);
        try
        {
            window.SceneInput.SelectedValue = "chapters.krl::opening"; Assert.False(window.FunctionInput.IsReadOnly);
            window.NameInput.Text = "  Пролог  "; window.FunctionInput.Text = "prologue";
            window.InheritResolutionInput.IsChecked = false; window.WidthInput.Text = "1664"; window.HeightInput.Text = "936";
            window.QualityInput.SelectedValue = "medium"; window.PostProcessingInput.IsChecked = true; window.HideShadersInput.IsChecked = true;
            window.MusicInput.Text = "music/theme.wav"; window.LoopMusicInput.IsChecked = false; window.InheritVolumeInput.IsChecked = false; window.VolumeInput.Value = 37; window.StopMusicInput.IsChecked = true;
            window.EntryTransitionInput.KindInput.SelectedValue = "custom"; window.EntryTransitionInput.ShaderInput.SelectedValue = "effects/entry.kneshader"; window.EntryTransitionInput.DurationInput.Text = "2,75";
            window.ExitTransitionInput.KindInput.SelectedValue = "fade"; window.ExitTransitionInput.DurationInput.Text = "0.5";
            window.AllowSavesInput.IsChecked = true; window.AutosaveInput.IsChecked = false; window.CheckpointInput.IsChecked = true;
            window.StateInput.Text = """{"score":7,"hero":{"name":"Герой"},"flags":[true,false]}"""; window.LimitDepthInput.IsChecked = true; window.DepthInput.Text = "3"; window.TriggersInput.IsChecked = false;
            Assert.True(window.WidthInput.IsEnabled); Assert.True(window.VolumeInput.IsEnabled); Assert.True(window.DepthInput.IsEnabled);
            window.SceneInput.SelectedValue = "chapters.krl::ending"; Assert.Equal("", window.NameInput.Text); Assert.Equal("ending", window.FunctionInput.Text);
            window.NameInput.Text = "Финал"; window.AllowSavesInput.IsChecked = false;
            Assert.False(window.AutosaveInput.IsEnabled); Assert.False(window.CheckpointInput.IsEnabled);
            window.SceneInput.SelectedValue = "chapters.krl::opening";
            Assert.Equal("Пролог", window.NameInput.Text); Assert.Equal("prologue", window.FunctionInput.Text); Assert.Equal("1664", window.WidthInput.Text); Assert.Equal(37, window.VolumeInput.Value);
            Assert.Equal("custom", window.EntryTransitionInput.KindInput.SelectedValue); Assert.Equal("effects/entry.kneshader", window.EntryTransitionInput.ShaderInput.SelectedValue); Assert.Equal(2.75, window.EntryTransitionInput.Value.Duration);
            await window.SaveAsync();
            var saved = PerformanceSettingsStore.LoadScenes(project.Path); var opening = saved.For("chapters.krl", "prologue");
            Assert.Equal("Пролог", opening.Name); Assert.Equal(1664, opening.WindowWidth); Assert.Equal(936, opening.WindowHeight);
            Assert.Equal("medium", opening.EffectsQuality); Assert.False(opening.PostProcessing); Assert.True(opening.HideShaders);
            Assert.Equal("music/theme.wav", opening.Music); Assert.False(opening.LoopMusic); Assert.Equal(37, opening.MusicVolume); Assert.True(opening.StopMusicOnExit);
            Assert.Equal("custom", opening.EntryTransition.Kind); Assert.Equal("effects/entry.kneshader", opening.EntryTransition.Shader); Assert.Equal(2.75, opening.EntryTransition.Duration); Assert.Equal("fade", opening.ExitTransition.Kind); Assert.Equal(.5, opening.ExitTransition.Duration);
            Assert.True(opening.AllowSaves); Assert.False(opening.AllowAutosave); Assert.True(opening.CheckpointOnEntry); Assert.False(opening.AllowTriggers); Assert.Equal(3, opening.MaxCallDepth);
            Assert.Equal(7, opening.InitialState.GetProperty("score").GetInt32()); Assert.Equal("Герой", opening.InitialState.GetProperty("hero").GetProperty("name").GetString()); Assert.False(opening.InitialState.GetProperty("flags")[1].GetBoolean());
            Assert.Equal("Финал", saved.For("chapters.krl", "ending").Name); Assert.False(saved.For("chapters.krl", "ending").AllowSaves); Assert.False(saved.Functions["chapters.krl"].ContainsKey("opening"));
            Assert.Contains("function prologue() d(nil,'opening text') end", File.ReadAllText(source)); Assert.Contains("call_scene(prologue)", File.ReadAllText(main)); Assert.Equal("prologue", VisualScript.Parse(File.ReadAllText(graphPath)).Nodes.Single(node => node.Id == "dialogue").Path);
            Assert.Equal("chapters.krl::prologue", window.SceneInput.SelectedValue); Assert.Equal("prologue", window.FunctionInput.Text); Assert.Equal("Пролог", project.Engine.ScenePerformance.For("chapters.krl", "prologue").Name);
            Assert.Equal(projectSettings, File.ReadAllBytes(Path.Combine(project.Path, "engine/performance.json"))); Assert.False(File.Exists(Path.Combine(project.Path, "prologue.krl"))); AssertNoTemps(fixture.ProjectPath);
            window.SceneInput.SelectedValue = "game/scenes/main.krl::main"; Assert.True(window.FunctionInput.IsReadOnly); Assert.Equal("main", window.FunctionInput.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task InvalidJsonPreventsSwitchingWithoutLosingTheCurrentFunctionOrOtherDrafts() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapters.krl", "function first() end\nfunction second() end"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.SceneInput.SelectedValue = "chapters.krl::first"; window.NameInput.Text = "Draft first"; window.StateInput.Text = "{";
            window.SceneInput.SelectedValue = "chapters.krl::second"; Assert.Equal("chapters.krl::first", window.SceneInput.SelectedValue); Assert.Equal("{", window.StateInput.Text); Assert.Equal("Draft first", window.NameInput.Text);
            await Assert.ThrowsAsync<InvalidDataException>(window.SaveAsync); AssertSnapshot(before, fixture.ProjectPath);
            window.StateInput.Text = "{}"; window.SceneInput.SelectedValue = "chapters.krl::second"; window.NameInput.Text = "Draft second"; window.SceneInput.SelectedValue = "chapters.krl::first";
            Assert.Equal("Draft first", window.NameInput.Text); window.Close(); AssertSnapshot(before, fixture.ProjectPath);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("function_conflict")] [InlineData("invalid_shader")] [InlineData("missing_music")] [InlineData("invalid_size")] [InlineData("invalid_depth")]
    public Task InvalidSceneDraftRejectsTheWholeRenameAndKeepsEveryFileAndDraft(string problem) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapters.krl", "function first() end\nfunction second() end"); fixture.Write("broken.kneshader", "broken shader JSON"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.SceneInput.SelectedValue = "chapters.krl::first"; window.FunctionInput.Text = "renamed"; window.NameInput.Text = "Draft";
            switch (problem)
            {
                case "function_conflict": window.FunctionInput.Text = "second"; break;
                case "invalid_shader": window.EntryTransitionInput.KindInput.SelectedValue = "custom"; window.EntryTransitionInput.ShaderInput.SelectedValue = "broken.kneshader"; break;
                case "missing_music": window.MusicInput.Text = "missing.wav"; break;
                case "invalid_size": window.InheritResolutionInput.IsChecked = false; window.WidthInput.Text = "639"; break;
                case "invalid_depth": window.LimitDepthInput.IsChecked = true; window.DepthInput.Text = "64"; break;
            }
            if (problem == "missing_music") await Assert.ThrowsAsync<FileNotFoundException>(window.SaveAsync);
            else await Assert.ThrowsAsync<InvalidDataException>(window.SaveAsync);
            AssertSnapshot(before, fixture.ProjectPath); Assert.Equal("Draft", window.NameInput.Text); Assert.Equal("chapters.krl::first", window.SceneInput.SelectedValue); AssertNoTemps(fixture.ProjectPath);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task ConcurrentSourceEditsAndAddedScriptsRejectTheWholeRenameAndPreserveTheExternalVersion(bool added) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = fixture.Write("chapter.krl", "function chapter() end"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            window.SceneInput.SelectedValue = "chapter.krl::chapter"; window.FunctionInput.Text = "renamed"; window.NameInput.Text = "Draft";
            if (added) fixture.Write("external.krl", "function external() end"); else File.AppendAllText(path, "\n-- External edit");
            var before = Snapshot(fixture.ProjectPath); await Assert.ThrowsAsync<IOException>(window.SaveAsync); AssertSnapshot(before, fixture.ProjectPath); Assert.Equal("renamed", window.FunctionInput.Text); AssertNoTemps(fixture.ProjectPath);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OpenDocumentGuardCanRejectTheConcreteRenameBeforeSettingsOrSourcesAreWritten() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string source = fixture.Write("chapter.krl", "function chapter() end"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.SceneInput.SelectedValue = "chapter.krl::chapter"; window.FunctionInput.Text = "renamed"; IReadOnlyList<string>? proposed = null;
            window.ValidateWrites = paths => { proposed = paths; throw new IOException("Unsaved editor draft"); };
            await Assert.ThrowsAsync<IOException>(window.SaveAsync); Assert.Equal(new[] { source }, proposed); AssertSnapshot(before, fixture.ProjectPath); Assert.Equal("renamed", window.FunctionInput.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public Task LockedOrReadOnlySettingsRollBackAllRenamedSourcesAndKeepTheCurrentDraft(bool readOnly) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapter.krl", "function chapter() end"); fixture.Write("game/scenes/main.krl", "function main() call_scene(chapter) end");
        string path = Path.Combine(fixture.ProjectPath, "engine/scene-performance.json"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            var before = Snapshot(fixture.ProjectPath); window.SceneInput.SelectedValue = "chapter.krl::chapter"; window.FunctionInput.Text = "renamed"; window.NameInput.Text = "Draft";
            Exception? error;
            if (readOnly)
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                try { error = await Record.ExceptionAsync(window.SaveAsync); }
                finally { File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly); }
            }
            else { using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); error = await Record.ExceptionAsync(window.SaveAsync); }
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Expected a write failure."); AssertSnapshot(before, fixture.ProjectPath); Assert.Equal("renamed", window.FunctionInput.Text); Assert.Equal("Draft", window.NameInput.Text); AssertNoTemps(fixture.ProjectPath);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task FourScenePagesClampNavigationKeepTheSelectedFunctionAndDiscardEveryDraftOnCancel() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapter.krl", "function chapter() end"); var window = new SceneParametersWindow(await fixture.LoadAsync());
        try
        {
            Assert.Equal((780d, 640d, 740d, 600d), (window.Width, window.Height, window.MinWidth, window.MinHeight));
            window.ShowPage(-1); Assert.Equal(0, window.Page); Assert.False(window.PreviousPage.IsEnabled); Assert.True(window.NextPage.IsEnabled);
            window.SceneInput.SelectedValue = "chapter.krl::chapter"; window.NameInput.Text = "Draft while paging";
            for (int page = 1; page <= 3; page++) { window.NextPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(page, window.Page); Assert.Equal("chapter.krl::chapter", window.SceneInput.SelectedValue); }
            Assert.False(window.NextPage.IsEnabled); window.ShowPage(99); Assert.Equal(3, window.Page);
            window.StateInput.Text = "{";
            for (int page = 2; page >= 0; page--) { window.PreviousPage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(page, window.Page); }
            Assert.False(window.PreviousPage.IsEnabled); Assert.Equal("Draft while paging", window.NameInput.Text); Assert.Equal("{", window.StateInput.Text);
            var cancel = Descendants((DependencyObject)window.Content).OfType<Button>().Single(button => Equals(button.Content, "Отмена")); cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).Functions);
        }
        finally { window.Close(); }
    });
    static IEnumerable<DependencyObject> Descendants(DependencyObject owner)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject nested) { yield return nested; foreach (var descendant in Descendants(nested)) yield return descendant; }
    }
    static Dictionary<string, byte[]> Snapshot(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
    static void AssertSnapshot(Dictionary<string, byte[]> before, string path)
    {
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order()); foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
    static void AssertNoTemps(string path) => Assert.Empty(Directory.EnumerateFiles(path, "*.tmp", SearchOption.AllDirectories));
    static void Capture(FrameworkElement root, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); var bitmap = SceneTransitionPresenter.Capture(root)!;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
