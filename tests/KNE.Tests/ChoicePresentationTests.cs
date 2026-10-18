using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ChoicePresentationTests
{
    [Theory]
    [InlineData("dim")]
    [InlineData("strike")]
    [InlineData("glitch")]
    [InlineData("hide")]
    [InlineData("scramble")]
    public Task LockedChoice_AllEffectsAreVisibleAndCannotActivate_WhileOriginalIndicesRemainStable(string effect) => TextInputPresentationTests.Sta(async () =>
    {
        var panel = new StackPanel(); var timer = new ChoiceTimerPresenter(); int selected = 0;
        var presenter = new ChoicePresentation(panel, timer, _ => { }, (sender, _) => selected = (int)((Button)sender).Tag);
        var story = new StoryFrame("choice", "", "Choose", ["Locked", "Open"])
        { ChoiceOptions = [new("Locked", false, effect), new("Open", true)], PresentationId = 1 };
        presenter.Display(story, true, Brushes.Purple);
        var locked = Assert.IsType<ChoiceOptionButton>(panel.Children[0]); var available = Assert.IsType<ChoiceOptionButton>(panel.Children[1]);
        Assert.False(locked.IsEnabled); Assert.True(available.IsEnabled); Assert.Equal(2, available.Tag);
        locked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, selected);
        TextInputPresentationTests.RaiseKey(locked, Key.Enter); TextInputPresentationTests.RaiseKey(locked, Key.Space); Assert.Equal(0, selected);
        available.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, selected);
        var label = Assert.IsType<TextBlock>(locked.Content);
        presenter.AdvanceAnimation(.8);
        switch (effect)
        {
            case "dim": Assert.Equal(.45, label.Opacity); break;
            case "strike": Assert.Same(TextDecorations.Strikethrough, label.TextDecorations); break;
            case "glitch": Assert.IsType<TranslateTransform>(label.RenderTransform); Assert.NotEqual(0, ((TranslateTransform)label.RenderTransform).X); break;
            case "hide":
            case "scramble":
                Assert.Equal("Locked", label.Text); presenter.AdvanceAnimation(2); Assert.NotEqual("Locked", label.Text);
                presenter.AdvanceAnimation(.8); Assert.Equal("Locked", label.Text); break;
        }
        presenter.Display(story with { HasBackgroundTasks = true, ChoiceTimer = new(10, 8.5, "numeric") }, true, Brushes.Purple);
        Assert.Same(locked, panel.Children[0]); Assert.Equal("8.5 с", timer.Number.Text);
        presenter.Reset(); Assert.Empty(panel.Children); Assert.Equal(Visibility.Collapsed, timer.Visibility);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData("overlay", PlacementMode.Center)]
    [InlineData("side", PlacementMode.Right)]
    public Task LockedChoice_HoverExplanationWorksOnDisabledButton_AndHasRequestedPlacement(string placement, PlacementMode expected) => TextInputPresentationTests.Sta(async () =>
    {
        var button = new ChoiceOptionButton(new("Недоступно", false, "dim", "Нужен ключ", placement), 1);
        Assert.True(ToolTipService.GetShowOnDisabled(button));
        var tooltip = Assert.IsType<ToolTip>(button.ToolTip); Assert.Equal(expected, tooltip.Placement); Assert.Same(button, tooltip.PlacementTarget);
        Assert.Equal("Нужен ключ", Assert.IsType<TextBlock>(tooltip.Content).Text);
        var host = Host(button);
        try
        {
            host.Show(); tooltip.IsOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(tooltip.IsOpen); Assert.True(tooltip.IsVisible); Assert.False(button.IsEnabled);
        }
        finally { tooltip.IsOpen = false; host.Close(); }
        Assert.Null(new ChoiceOptionButton(new("Disabled", false), 2).ToolTip);
    });

    [Fact]
    public Task ChoiceAnimation_StartsOnlyWhenVisible_StopsOnPauseUnloadAndReset_AndSnapshotIsStatic() => TextInputPresentationTests.Sta(async () =>
    {
        var panel = new StackPanel(); var presenter = new ChoicePresentation(panel, new(), _ => { }, (_, _) => { });
        var story = new StoryFrame("choice", "", "", ["Hidden", "Open"]) { ChoiceOptions = [new("Hidden", false, "scramble"), new("Open", true)] };
        var host = Host(panel);
        try
        {
            presenter.Display(story, true, Brushes.Purple); Assert.False(presenter.IsAnimating);
            host.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.True(presenter.IsAnimating);
            panel.Visibility = Visibility.Collapsed; Assert.False(presenter.IsAnimating);
            panel.Visibility = Visibility.Visible; Assert.True(presenter.IsAnimating);
            presenter.Stop(); Assert.False(presenter.IsAnimating);
            presenter.Display(story, false, Brushes.Purple); Assert.False(presenter.IsAnimating);
            var label = Assert.IsType<TextBlock>(Assert.IsType<ChoiceOptionButton>(panel.Children[0]).Content);
            string frozen = label.Text; presenter.AdvanceAnimation(.8); Assert.Equal(frozen, label.Text);
            presenter.Display(story, true, Brushes.Purple); Assert.True(presenter.IsAnimating);
            host.Content = null; Assert.False(presenter.IsAnimating);
        }
        finally { presenter.Reset(); host.Close(); }
    });

    [Fact]
    public Task ChoiceTimer_UsesAuthoritativeTime_ForTopBarAndNumber_AndNeverSelectsLocally() => TextInputPresentationTests.Sta(async () =>
    {
        int selected = 0; var timer = new ChoiceTimerPresenter(); var panel = new StackPanel();
        var presenter = new ChoicePresentation(panel, timer, _ => { }, (_, _) => selected++);
        var story = new StoryFrame("choice", "", "", ["A"]) { ChoiceTimer = new(10, 7.25, "bar") };
        presenter.Display(story, true, Brushes.Orange);
        Assert.Equal(.725, timer.Bar.Value); Assert.Equal(Visibility.Visible, timer.Bar.Visibility); Assert.Equal(Visibility.Collapsed, timer.Number.Visibility);
        Assert.Equal(VerticalAlignment.Top, timer.VerticalAlignment); Assert.Equal(HorizontalAlignment.Stretch, timer.HorizontalAlignment);
        presenter.Display(story with { ChoiceTimer = new(10, 0, "numeric") }, true, Brushes.Orange);
        Assert.Equal("0.0 с", timer.Number.Text); Assert.Equal(Visibility.Visible, timer.Number.Visibility); Assert.Equal(Visibility.Collapsed, timer.Bar.Visibility); Assert.Equal(0, selected);
        presenter.Display(new("dialogue", "", "Continue", []), true, Brushes.Orange); Assert.Equal(Visibility.Collapsed, timer.Visibility);
        await Task.CompletedTask;
    });

    [Fact]
    public Task ChoiceEditor_PersistsAllLockAndTimerFields_PreservesConnectionsAndWeights_AndUndoesAtomically() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/choices.knegraph", VisualScript.Create().Serialize());
        var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.TimedChoice);
        var node = editor.Document!.Graph!.Nodes.Last(); string id = node.Id; node.Options[0].Target = "dialogue"; node.Options[0].Weight = 7;
        FindDynamic<CheckBox>(editor, "ChoiceLock0Enabled").IsChecked = true;
        FindDynamic<TextBox>(editor, "ChoiceLock0Variable").Text = "coins";
        FindDynamic<ComboBox>(editor, "ChoiceLock0Operator").SelectedIndex = (int)ScriptComparison.Less;
        FindDynamic<TextBox>(editor, "ChoiceLock0CompareVariable").Text = "price";
        FindDynamic<ComboBox>(editor, "ChoiceLock0ValueType").SelectedItem = "number";
        FindDynamic<TextBox>(editor, "ChoiceLock0Value").Text = "15";
        FindDynamic<ComboBox>(editor, "ChoiceLock0Effect").SelectedIndex = 4;
        FindDynamic<CheckBox>(editor, "ChoiceLock0ShowHint").IsChecked = true;
        FindDynamic<TextBox>(editor, "ChoiceLock0Hint").Text = "Недостаточно монет";
        FindDynamic<ComboBox>(editor, "ChoiceLock0HintPlacement").SelectedIndex = 0;
        FindDynamic<TextBox>(editor, "ChoiceSeconds").Text = "2.5";
        FindDynamic<TextBox>(editor, "ChoiceDefaultOption").Text = "2";
        FindDynamic<ComboBox>(editor, "ChoiceTimerDisplay").SelectedIndex = 1;
        ((TextBox)editor.FindName("OptionsInput")).Text = "Купить\nУйти";
        Click(editor, "ApplyPropertiesButton");
        node = editor.Document.Graph.Nodes.Single(n => n.Id == id);
        var expected = new OptionLockSettings { Variable = "coins", Operator = ScriptComparison.Less, CompareVariable = "price", ValueType = "number", Value = "15", Effect = "scramble", ShowHint = true, Hint = "Недостаточно монет", HintPlacement = "overlay" };
        Assert.Equal(expected, node.Options[0].Lock); Assert.Equal(new ChoiceSettings { Seconds = 2.5, DefaultOption = 2, TimerDisplay = "numeric" }, node.ChoiceSettings);
        Assert.Equal("dialogue", node.Options[0].Target); Assert.Equal(7, node.Options[0].Weight); Assert.Equal("Купить", node.Options[0].Text);
        Click(editor, "UndoButton"); Assert.Null(editor.Document.Graph.Nodes.Single(n => n.Id == id).Options[0].Lock);
        Click(editor, "RedoButton"); Assert.Equal(expected, editor.Document.Graph.Nodes.Single(n => n.Id == id).Options[0].Lock);
        await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path); Assert.Equal(expected, editor.Document.Graph!.Nodes.Single(n => n.Id == id).Options[0].Lock);
        editor.Clear();
    });

    [Fact]
    public Task ChoiceEditor_RejectsInvalidDraftWithoutDamage_AndDisablingLockRetainsOtherOptions() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/choices.knegraph", VisualScript.Create().Serialize());
        var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.Choice); string id = editor.Document!.Graph!.Nodes.Last().Id;
        FindDynamic<CheckBox>(editor, "ChoiceLock0Enabled").IsChecked = true;
        FindDynamic<TextBox>(editor, "ChoiceLock0Variable").Text = "bad-name";
        Click(editor, "ApplyPropertiesButton"); Assert.Null(editor.Document.Graph.Nodes.Single(n => n.Id == id).Options[0].Lock);
        Assert.Contains("Переменные блокировки", ((TextBlock)editor.FindName("EditorStatus")).Text);
        FindDynamic<TextBox>(editor, "ChoiceLock0Variable").Text = "flag";
        Click(editor, "ApplyPropertiesButton"); Assert.NotNull(editor.Document.Graph.Nodes.Single(n => n.Id == id).Options[0].Lock);
        FindDynamic<CheckBox>(editor, "ChoiceLock0Enabled").IsChecked = false;
        ((TextBox)editor.FindName("OptionsInput")).Text = "Only"; Click(editor, "ApplyPropertiesButton");
        Assert.Null(Assert.Single(editor.Document.Graph.Nodes.Single(n => n.Id == id).Options).Lock); editor.Clear();
    });

    [Fact]
    public Task ScenePreview_LockedOptionsAndTimerPreserveControlsAndConfiguredStyle_AndFrozenPreviewCannotChoose() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var preview = new ScenePreview(); int selected = 0;
        preview.SceneChoiceRequested += (_, option) => selected = option;
        var story = new StoryFrame("choice", "", "Ваш выбор", ["Купить", "Уйти"]) { PresentationId = 8, ChoiceOptions = [new("Купить", false, "strike", "Нужны монеты", "side"), new("Уйти", true)], ChoiceTimer = new(10, 9, "bar") };
        var style = GameplayUiStyle.ForTarget("choice") with { FontFamily = "Consolas", FontSize = 22, Width = 500, Height = 55, X = 80, Y = 180 };
        var visuals = SceneVisuals.Empty with { UiConfigurations = [new("choice", style)] };
        try
        {
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story, false, true);
            var panel = (StackPanel)preview.FindName("ChoicePanel"); var blocked = Assert.IsType<ChoiceOptionButton>(panel.Children[0]); var open = Assert.IsType<ChoiceOptionButton>(panel.Children[1]);
            Assert.False(blocked.IsEnabled); Assert.Equal("Consolas", open.FontFamily.Source); Assert.Equal(22, open.FontSize); Assert.Equal(500, open.Width);
            blocked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, selected); open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, selected);
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story with { ChoiceTimer = new(10, 5, "numeric") }, false, true);
            Assert.Same(blocked, panel.Children[0]); Assert.Same(open, panel.Children[1]); Assert.Equal("5.0 с", ((ChoiceTimerPresenter)preview.FindName("ChoiceTimer")).Number.Text);
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story, false, false); Assert.False(panel.IsHitTestVisible);
            selected = 0; open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, selected);
            preview.ResetPresentation(); Assert.Equal(Visibility.Collapsed, ((ChoiceTimerPresenter)preview.FindName("ChoiceTimer")).Visibility);
        }
        finally { preview.Stop(); }
    });

    [Fact]
    public Task ChoiceScenes_RasterShowsAllEffectsHoverAndTimerAtTop_WithDialogueUncovered() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var preview = new ScenePreview();
        var labels = new[] { "Затемнение", "Зачёркивание", "Глитч", "Скрытие букв", "Превращение букв", "Доступный ответ" };
        var effects = new[] { "dim", "strike", "glitch", "hide", "scramble" };
        var story = new StoryFrame("choice", "", "Выберите путь", labels) { ChoiceOptions = labels.Select((text, i) => new ChoiceOptionState(text, i == 5, i < 5 ? effects[i] : "dim", i < 5 ? "Требуется ключ" : "", "overlay")).ToArray(), ChoiceTimer = new(15, 9, "bar") };
        var style = GameplayUiStyle.ForTarget("choice") with { Width = 550, Height = 48, X = 365, Y = 180 };
        try
        {
            preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty with { UiConfigurations = [new("choice", style)] }, story, false, true);
            var scene = (Grid)preview.FindName("SceneArea"); scene.Measure(new Size(1280, 720)); scene.Arrange(new Rect(0, 0, 1280, 720)); scene.UpdateLayout();
            var timer = (ChoiceTimerPresenter)preview.FindName("ChoiceTimer"); Assert.True(timer.ActualWidth > 1100); Assert.InRange(timer.TranslatePoint(new Point(), scene).Y, 0, 40);
            var panel = (StackPanel)preview.FindName("ChoicePanel"); Assert.Equal(6, panel.Children.Count); var last = (Button)panel.Children[5];
            Assert.True(last.TranslatePoint(new Point(), scene).Y + last.ActualHeight < 650);
            if (Environment.GetEnvironmentVariable("KNE_CHOICE_RENDER") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(scene);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(directory, "choices.png")); encoder.Save(output);
            }
        }
        finally { preview.Stop(); }
        await Task.CompletedTask;
    });

    [Fact]
    public Task GameWindow_TimedChoiceTickKeepsControlsAndLocks_AndClickSubmitsOriginalIndex() => TextInputPresentationTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Game.AutoSave = false;
        var session = new ChoiceSession(project.Engine.RuntimeDefaults(project.Title));
        var window = new GameWindow(project) { ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
        var field = typeof(GameWindow).GetField("runtime", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await ((IGameSession)field.GetValue(window)!).DisposeAsync(); field.SetValue(window, session);
        typeof(GameWindow).GetField("ready", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        typeof(GameWindow).GetField("loaded", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        ((DispatcherTimer)typeof(GameWindow).GetField("renderTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
        try
        {
            window.Show(); Invoke("ShowGameplay"); Invoke("RenderOutput");
            var panel = (StackPanel)window.FindName("ChoicePanel"); var locked = Assert.IsType<ChoiceOptionButton>(panel.Children[0]); var open = Assert.IsType<ChoiceOptionButton>(panel.Children[1]);
            Assert.False(locked.IsEnabled); locked.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, session.Selected);
            await (Task)Invoke("PumpSceneTimeAsync", 2d)!;
            Assert.Equal("8.0 с", ((ChoiceTimerPresenter)window.FindName("ChoiceTimer")).Number.Text); Assert.Same(open, panel.Children[1]);
            var scene = (Grid)window.FindName("SceneArea"); var timer = (ChoiceTimerPresenter)window.FindName("ChoiceTimer"); scene.UpdateLayout();
            Assert.InRange(timer.TranslatePoint(new Point(), scene).Y, 0, 40);
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, session.Selected); Assert.Equal("Accepted", session.Story.Text);
            Assert.Equal(Visibility.Collapsed, timer.Visibility);
        }
        finally { window.Close(); }
        object? Invoke(string name, params object[] arguments) => typeof(GameWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, arguments);
    });

    [Fact]
    public async Task SceneTest_TimedChoiceBackAndRestartRestoreWholeFrameAndCountdownWithoutSaves()
    {
        using var fixture = new TempProject();
        const string source = "function main()\n local n=kne_choice('{\"prompt\":\"Pick\",\"options\":[{\"text\":\"A\"},{\"text\":\"B\"}],\"timer\":{\"seconds\":2,\"default_option\":2,\"timer_display\":\"bar\"}}')\n d(nil,'chosen:'..n)\nend";
        fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        await using var session = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await session.RestartAsync(); Assert.Equal(2, session.Story.ChoiceTimer!.RemainingSeconds);
        await session.TickAsync(.5); Assert.Equal(1.5, session.Story.ChoiceTimer!.RemainingSeconds); Assert.False(session.CanGoBack);
        await session.TickAsync(1.5); Assert.Equal("chosen:2", session.Story.Text); Assert.True(session.CanGoBack);
        await session.BackAsync(); Assert.Equal("choice", session.Story.Kind); Assert.Equal(2, session.Story.ChoiceTimer!.RemainingSeconds);
        await session.ChooseAsync(1); Assert.Equal("chosen:1", session.Story.Text);
        await session.RestartAsync(); Assert.Equal(2, session.Story.ChoiceTimer!.RemainingSeconds);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "saves"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EditorPreview_RejectsLockedChoiceWithoutRememberingIt_AndReplaysAvailableChoice()
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.FunctionName = "chapter";
        graph.Nodes[1].Kind = ScriptNodeKind.Choice; graph.Nodes[1].Options = [new() { Text = "Locked", Target = "end", Lock = new() { Variable = "score", ValueType = "number", Value = "0" } }, new() { Text = "Open", Target = "end" }];
        string path = fixture.Write("game/scenes/choice.knegraph", graph.Serialize()); var project = await fixture.LoadAsync();
        var target = new EditorSelection(path, graph.Compile(editorPreview: true), 1, "end", false, "chapter", false);
        await using var preview = new EditorPreview(project, _ => { }); await preview.SeekAsync(target);
        Assert.True(preview.NeedsChoice); Assert.False(preview.Story.ChoiceOptions![0].IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.ChooseAsync(1)); Assert.True(preview.NeedsChoice);
        await preview.ChooseAsync(2); Assert.True(preview.Reached);
        await preview.SeekAsync(target); Assert.True(preview.Reached);
        preview.ResetChoices(); await preview.SeekAsync(target); Assert.True(preview.NeedsChoice);
    }

    private sealed class ChoiceSession(RuntimeWindowSettings settings) : IGameSession
    {
        public RuntimeWindowSettings WindowSettings => settings;
        public StoryFrame Story { get; private set; } = new("choice", "", "Choose", ["Locked", "Open"]) { ChoiceOptions = [new("Locked", false, "glitch"), new("Open", true)], ChoiceTimer = new(10, 10, "numeric"), PresentationId = 1 };
        public SceneVisuals Visuals => SceneVisuals.Empty;
        public int Selected { get; private set; }
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task AdvanceAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ChooseAsync(int option, CancellationToken token = default) { Selected = option; Story = new("dialogue", "", "Accepted", []); return Task.CompletedTask; }
        public Task TickAsync(double seconds, CancellationToken token = default) { Story = Story with { ChoiceTimer = Story.ChoiceTimer! with { RemainingSeconds = Story.ChoiceTimer.RemainingSeconds - seconds } }; return Task.CompletedTask; }
        public Task ProcessCommandAsync(string command, CancellationToken token = default) => Task.CompletedTask;
        public Task<GameCheckpoint> GetCheckpointAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Window Host(object content) => new() { Content = content, Width = 600, Height = 400, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
    private static T FindDynamic<T>(ScriptEditor editor, string name) where T : FrameworkElement => Descendants((DependencyObject)editor.FindName("ExtendedProperties")).OfType<T>().Single(item => item.Name == name);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        foreach (object value in LogicalTreeHelper.GetChildren(parent))
            if (value is DependencyObject child) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
    private static void Click(FrameworkElement owner, string name) => ((Button)owner.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
