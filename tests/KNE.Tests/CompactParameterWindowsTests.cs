using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class CompactParameterWindowsTests
{
    [Theory]
    [InlineData(780, 640)] [InlineData(740, 600)]
    public Task EveryScenePageExposesItsExpandedFieldsWithoutScrollingAndKeepsTheSelectorAndActionsFixed(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapter.krl", "function chapter() end"); fixture.Write("effect.kneshader", ShaderGraphCompiler.Create().Serialize());
        var project = await fixture.LoadAsync(); SceneMetrics.Record(project.Path, "chapter.krl", 12.5, 7, 101); var window = new SceneParametersWindow(project);
        try
        {
            Assert.Equal((780d, 640d, 740d, 600d), (window.Width, window.Height, window.MinWidth, window.MinHeight));
            window.SceneInput.SelectedValue = "chapter.krl::chapter";
            window.InheritResolutionInput.IsChecked = false; window.InheritVolumeInput.IsChecked = false; window.LimitDepthInput.IsChecked = true;
            window.EntryTransitionInput.KindInput.SelectedValue = window.ExitTransitionInput.KindInput.SelectedValue = "custom";
            window.EntryTransitionInput.ShaderInput.SelectedValue = window.ExitTransitionInput.ShaderInput.SelectedValue = "effect.kneshader";
            window.StateInput.Text = "{\"notes\":\"" + new string('я', 10000) + "\"}";
            Show(window, width, height); var root = (FrameworkElement)window.Content;
            var save = Logical(root).OfType<Button>().Single(button => Equals(button.Content, "Сохранить"));
            var cancel = Logical(root).OfType<Button>().Single(button => Equals(button.Content, "Отмена"));
            var fixedBounds = new FrameworkElement[] { window.SceneInput, save, cancel }.ToDictionary(input => input, input => Bounds(input, root));
            FrameworkElement[][] pages =
            [
                [window.NameInput, window.FunctionInput, window.InheritResolutionInput, window.WidthInput, window.HeightInput, window.QualityInput, window.PostProcessingInput, window.HideShadersInput],
                [window.MusicInput, window.LoopMusicInput, window.InheritVolumeInput, window.VolumeInput, window.StopMusicInput, window.EntryTransitionInput.KindInput, window.EntryTransitionInput.ShaderInput, window.EntryTransitionInput.DurationInput, window.ExitTransitionInput.KindInput, window.ExitTransitionInput.ShaderInput, window.ExitTransitionInput.DurationInput],
                [window.AllowSavesInput, window.AutosaveInput, window.CheckpointInput, window.LimitDepthInput, window.DepthInput, window.TriggersInput],
                [window.StateInput]
            ];
            for (int page = 0; page < pages.Length; page++)
            {
                Assert.Equal(page, window.Page); Layout(window); Capture(root, $"scene-compact-{width}-{page + 1}"); Assert.Empty(Logical(root).OfType<ScrollViewer>());
                Assert.Equal(page > 0, window.PreviousPage.IsEnabled); Assert.Equal(page < pages.Length - 1, window.NextPage.IsEnabled);
                AssertVisibleInputs(root, pages[page]);
                AssertPageLabelsFit(root);
                foreach (var other in pages.SelectMany(p => p).Except(pages[page])) Assert.False(other.IsVisible, $"A field from another scene page is visible: {other.GetType().Name}/{other.Name}");
                foreach (var item in fixedBounds) { Fits(item.Key, root); Assert.Equal(item.Value, Bounds(item.Key, root)); }
                Fits(window.PreviousPage, root); Fits(window.NextPage, root);
                Assert.Equal(1, root.LayoutTransform.Value.M11); Assert.Equal(1, root.LayoutTransform.Value.M22);
                if (page < pages.Length - 1) Click(window.NextPage);
            }
            Assert.True(window.StateInput.ActualHeight >= 60); Assert.Equal(ScrollBarVisibility.Auto, window.StateInput.VerticalScrollBarVisibility);
            Assert.Equal("custom", window.EntryTransitionInput.KindInput.SelectedValue); Assert.Equal("effect.kneshader", window.ExitTransitionInput.ShaderInput.SelectedValue);
            Click(cancel); Assert.Empty(PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).Functions);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(780, 840)] [InlineData(740, 680)]
    public Task EveryProjectPageExposesItsConditionalFieldsWithoutScrollingAtTheSupportedWindowSizes(double width, double height) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var window = new ProjectParametersWindow(project);
        try
        {
            Assert.Equal((780d, 840d, 740d, 680d), (window.Width, window.Height, window.MinWidth, window.MinHeight));
            window.ResolutionInput.SelectedValue = "custom"; window.AutosaveInput.SelectedValue = "actions";
            window.BuildInput.TestMode.SelectedValue = "remote_debug"; window.BuildInput.ReleaseMode.SelectedValue = "single_file";
            window.MetadataInput.Additional.Text += new string('\n', 100);
            Show(window, width, height); var root = (FrameworkElement)window.Content;
            var save = Logical(root).OfType<Button>().Single(button => Equals(button.Content, "Сохранить"));
            var cancel = Logical(root).OfType<Button>().Single(button => Equals(button.Content, "Отмена"));
            var pageLabel = Logical(root).OfType<TextBlock>().Single(text => text.Text == "1 / 4");
            FitsPage(window.RecordConsoleShortcut, root);
            Assert.Equal(Path.Combine(project.Path, "project.json"), window.MetadataInput.ExternalEditorStartInfo(null).ArgumentList.Single());
            var fixedBounds = new FrameworkElement[] { save, cancel, window.PreviousPage, pageLabel, window.NextPage }.ToDictionary(input => input, input => Bounds(input, root));
            Assert.Equal(root.ActualWidth / 2, (fixedBounds[save].Left + fixedBounds[cancel].Right) / 2, 0);
            Assert.Equal(fixedBounds[save].Top, fixedBounds[cancel].Top);
            FrameworkElement[][] pages =
            [
                [window.GraphicsInput.ApiInput, window.GraphicsInput.VSyncInput, window.GraphicsInput.FpsInput, window.GraphicsInput.MsaaInput, window.TextureInput, window.ResolutionInput, window.CustomWidthInput, window.CustomHeightInput, window.PaletteInput, window.GraphicsInput.LimitCacheInput, window.GraphicsInput.CacheLimitInput, window.AsyncTasksInput, window.AudioBufferInput, window.PreloadInput, window.ConsoleModeInput, window.ConsoleHotkeyInput],
                [window.SavesInput, window.PersistentLocationInput, window.RollbackInput, window.AutosaveInput, window.AutosaveActionsInput, window.MenuMusicInput, window.CornerInput, window.DeleteCrashReportsButton, window.DangerZone],
                [window.BuildInput.TestMode, window.BuildInput.TestPath, window.BuildInput.ReleaseMode, window.BuildInput.ReleasePath, window.BuildInput.Saves, window.FileSystemInput],
                [window.TitleInput, window.BuildInput.Author, window.BuildInput.Team, window.BuildInput.Version,
                    window.MetadataInput["game_info.description"], window.MetadataInput["metadata.icon"], window.MetadataInput["metadata.default_language"],
                    window.BuildInput.UseProjectNameForBuild, window.MetadataInput["metadata.created_date"], window.MetadataInput["metadata.kne_version"],
                    window.MetadataInput["entry_points.main_scene"], window.MetadataInput["entry_points.init_script"],
                    .. window.MetadataInput.Lifecycle.Inputs, window.MetadataInput.OpenExternalEditor, window.MetadataInput.Additional]
            ];
            for (int page = 0; page < pages.Length; page++) { window.ShowPage(page); Layout(window); Capture(root, $"project-compact-{width}-{page + 1}"); }
            window.ShowPage(0); Layout(window);
            for (int page = 0; page < pages.Length; page++)
            {
                Assert.Equal(page, window.Page); Layout(window); Capture(root, $"project-compact-{width}-{page + 1}"); Assert.Empty(Logical(root).OfType<ScrollViewer>());
                Assert.Equal($"{page + 1} / 4", pageLabel.Text);
                Assert.Equal(page > 0, window.PreviousPage.IsEnabled); Assert.Equal(page < pages.Length - 1, window.NextPage.IsEnabled);
                if (page < 2) AssertStackedCardsFit(root, page == 0 ? 3 : 2);
                if (page == 3) for (int field = 1; field <= 7; field++)
                    Assert.True(Bounds(pages[page][field - 1], root).Bottom <= Bounds(pages[page][field], root).Top, "Metadata fields must follow one another in a single vertical column.");
                AssertVisibleInputs(root, pages[page]);
                AssertPageLabelsFit(root);
                foreach (var other in pages.SelectMany(p => p).Except(pages[page])) Assert.False(other.IsVisible, $"A field from another project page is visible: {other.GetType().Name}/{other.Name}");
                foreach (var check in Logical(root).OfType<CheckBox>().Where(check => check.IsVisible)) FitsPage(check, root);
                foreach (var item in fixedBounds) { Fits(item.Key, root); Assert.Equal(item.Value, Bounds(item.Key, root)); }
                Fits(window.PreviousPage, root); Fits(window.NextPage, root);
                if (page == 1)
                {
                    window.DangerZone.IsExpanded = true; Layout(window); Capture(root, $"project-compact-{width}-{page + 1}-danger"); FitsPage(window.DeleteProjectButton, root);
                    foreach (var button in Logical(window.DangerZone).OfType<Button>()) FitsPage(button, root);
                    window.ConfirmProjectDeletion = () => true; Click(window.DeleteProjectButton); Layout(window); Capture(root, $"project-compact-{width}-{page + 1}-armed"); Assert.True(window.DeletionConfirmation.IsArmed); Assert.Contains("72", window.DeleteCounter.Text); FitsPage(window.DeleteCounter, root); AssertPageLabelsFit(root);
                    window.DangerZone.IsExpanded = false; Layout(window);
                }
                Assert.Equal(1, root.LayoutTransform.Value.M11); Assert.Equal(1, root.LayoutTransform.Value.M22);
                if (page < pages.Length - 1) Click(window.NextPage);
            }
            Assert.True(window.MetadataInput.Additional.ActualHeight >= 40); Assert.Equal(ScrollBarVisibility.Auto, window.MetadataInput.Additional.VerticalScrollBarVisibility);
            Assert.Equal("remote_debug", window.BuildInput.Value.TestMode); Assert.Equal("single_file", window.BuildInput.Value.ReleaseMode);
            window.ShowPage(99); Assert.Equal(3, window.Page); Assert.False(window.NextPage.IsEnabled);
            for (int page = 2; page >= 0; page--) { Click(window.PreviousPage); Layout(window); Assert.Equal(page, window.Page); Assert.Equal($"{page + 1} / 4", pageLabel.Text); foreach (var item in fixedBounds) Assert.Equal(item.Value, Bounds(item.Key, root)); }
            window.ShowPage(-1); Assert.Equal(0, window.Page); Assert.False(window.PreviousPage.IsEnabled);
            Click(cancel); Assert.False(File.Exists(Path.Combine(project.Path, "engine/build.json")));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task ScenePageNavigationKeepsAllFourDraftsAndSaveOrCancelAppliesThemAsOneOperation() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("chapter.krl", "function chapter() end"); var project = await fixture.LoadAsync();
        var window = new SceneParametersWindow(project);
        try
        {
            window.SceneInput.SelectedValue = "chapter.krl::chapter"; window.NameInput.Text = "Глава"; window.InheritResolutionInput.IsChecked = false; window.WidthInput.Text = "800"; window.HeightInput.Text = "600";
            Click(window.NextPage); window.InheritVolumeInput.IsChecked = false; window.VolumeInput.Value = 23; window.ExitTransitionInput.KindInput.SelectedValue = "dissolve"; window.ExitTransitionInput.DurationInput.Text = "1.5";
            Click(window.NextPage); window.AutosaveInput.IsChecked = false; window.CheckpointInput.IsChecked = true; window.LimitDepthInput.IsChecked = true; window.DepthInput.Text = "2";
            Click(window.NextPage); window.StateInput.Text = """{"score":7,"hero":{"name":"Анна"}}""";
            for (int page = 3; page > 0; page--) Click(window.PreviousPage);
            Assert.Equal(0, window.Page); Assert.Equal("Глава", window.NameInput.Text); Assert.Equal(23, window.VolumeInput.Value); Assert.Equal("2", window.DepthInput.Text); Assert.Contains("Анна", window.StateInput.Text);
            await window.SaveAsync(); var saved = PerformanceSettingsStore.LoadScenes(project.Path).For("chapter.krl", "chapter");
            Assert.Equal("Глава", saved.Name); Assert.Equal(800, saved.WindowWidth); Assert.Equal(600, saved.WindowHeight); Assert.Equal(23, saved.MusicVolume);
            Assert.Equal("dissolve", saved.ExitTransition.Kind); Assert.Equal(1.5, saved.ExitTransition.Duration); Assert.False(saved.AllowAutosave); Assert.True(saved.CheckpointOnEntry); Assert.Equal(2, saved.MaxCallDepth);
            Assert.Equal(7, saved.InitialState.GetProperty("score").GetInt32()); Assert.Equal("Анна", saved.InitialState.GetProperty("hero").GetProperty("name").GetString());
        }
        finally { window.Close(); }
        string path = Path.Combine(project.Path, "engine/scene-performance.json"); byte[] original = File.ReadAllBytes(path); var canceled = new SceneParametersWindow(project);
        try { canceled.SceneInput.SelectedValue = "chapter.krl::chapter"; canceled.NameInput.Text = "Не сохранять"; for (int i = 0; i < 3; i++) Click(canceled.NextPage); canceled.StateInput.Text = "{"; canceled.Close(); Assert.Equal(original, File.ReadAllBytes(path)); }
        finally { canceled.Close(); }
    });

    static void Show(Window window, double width, double height)
    {
        EditorTheme.Apply(window, "kne"); window.Width = width; window.Height = height; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false; window.ShowActivated = false; window.Show(); Layout(window);
    }
    static void Layout(Window window) { window.UpdateLayout(); ((FrameworkElement)window.Content).UpdateLayout(); }
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static Rect Bounds(FrameworkElement input, FrameworkElement root) => input.TransformToAncestor(root).TransformBounds(new Rect(input.RenderSize));
    static void Fits(FrameworkElement input, FrameworkElement root)
    {
        string field = Describe(input);
        Assert.True(input.IsVisible, $"Hidden field: {field}"); Assert.True(input.ActualWidth > 1 && input.ActualHeight > 1, $"Empty field: {field}");
        var bounds = Bounds(input, root); Assert.True(bounds.Left >= -.1 && bounds.Top >= -.1 && bounds.Right <= root.ActualWidth + .1 && bounds.Bottom <= root.ActualHeight + .1, $"Clipped field: {field}, {bounds}, viewport {root.RenderSize}");
    }
    static string Describe(FrameworkElement input)
    {
        string page = Window.GetWindow(input) switch { ProjectParametersWindow project => "project page " + project.Page, SceneParametersWindow scene => "scene page " + scene.Page, _ => "unhosted" };
        string value = input switch { ComboBox combo => Convert.ToString(combo.SelectedValue) ?? "", TextBox text => text.Text, TextBlock text => text.Text, _ => "" };
        if (value.Length > 80) value = value[..80] + "…";
        return page + " · " + input.GetType().Name + "/" + input.Name + " · " + value;
    }
    static void AssertVisibleInputs(FrameworkElement root, FrameworkElement[] inputs)
    {
        foreach (var input in inputs) { FitsPage(input, root); Assert.True(input.IsEnabled && input.IsHitTestVisible, $"Unusable field: {input.GetType().Name}/{input.Name}"); }
        for (int left = 0; left < inputs.Length; left++) for (int right = left + 1; right < inputs.Length; right++)
            Assert.False(Rect.Intersect(Bounds(inputs[left], root), Bounds(inputs[right], root)) is { Width: > .5, Height: > .5 }, $"Overlapping fields: {inputs[left].GetType().Name}/{inputs[left].Name} and {inputs[right].GetType().Name}/{inputs[right].Name}");
    }
    static ContentControl PageViewport(FrameworkElement root) => Logical(root).OfType<ContentControl>().Single(control => control.GetType() == typeof(ContentControl));
    static void AssertStackedCardsFit(FrameworkElement root, int count)
    {
        var viewport = PageViewport(root); var stack = Assert.IsType<StackPanel>(viewport.Content);
        Assert.Equal(Orientation.Vertical, stack.Orientation); Assert.Equal(count, stack.Children.Count);
        var cards = stack.Children.Cast<FrameworkElement>().ToArray();
        foreach (var card in cards) FitsPage(card, root);
        Rect first = Bounds(cards[0], viewport), previous = first;
        foreach (var card in cards.Skip(1))
        {
            Rect current = Bounds(card, viewport);
            Assert.True(previous.Bottom <= current.Top, $"Cards overlap vertically: {previous}, {current}");
            Assert.Equal(first.Left, current.Left, 1); Assert.Equal(first.Right, current.Right, 1);
            previous = current;
        }
        Assert.True(first.Left > 0 && first.Right < viewport.ActualWidth, $"Cards must retain side margins: {first}, viewport {viewport.RenderSize}");
        Assert.Equal(first.Left, viewport.ActualWidth - first.Right, 1);
    }
    static void FitsPage(FrameworkElement input, FrameworkElement root)
    {
        var viewport = PageViewport(root); Fits(input, viewport);
        for (var ancestor = VisualTreeHelper.GetParent(input); ancestor != null && !ReferenceEquals(ancestor, viewport); ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is FrameworkElement { ClipToBounds: true } clip) Fits(input, clip);
    }
    static void AssertPageLabelsFit(FrameworkElement root)
    {
        foreach (var text in Logical(PageViewport(root)).OfType<TextBlock>().Where(text => text.IsVisible))
        {
            FitsPage(text, root);
            Assert.True(text.ActualHeight + .1 >= text.DesiredSize.Height - text.Margin.Top - text.Margin.Bottom, $"Truncated label: {text.Text}");
            Assert.True(text.ActualWidth + .1 >= text.DesiredSize.Width - text.Margin.Left - text.Margin.Right, $"Truncated label width: {text.Text}");
        }
    }
    static IEnumerable<DependencyObject> Logical(DependencyObject owner)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(owner)) if (child is DependencyObject nested) { yield return nested; foreach (var descendant in Logical(nested)) yield return descendant; }
    }
    static void Capture(FrameworkElement root, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_COMPACT_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); var image = SceneTransitionPresenter.Capture(root)!; var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
}
