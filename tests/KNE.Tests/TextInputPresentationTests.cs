using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class TextInputPresentationTests
{
    [Fact]
    public Task InputPresenter_TicksAndFeedbackPreserveDraftCaretAndSelection_NewOccurrenceClearsDraft() => Sta(async () =>
    {
        var presenter = new TextInputPresenter(); var input = new TextInputSettings { AutoFocus = false, Placeholder = "Имя" };
        var story = new StoryFrame("input", "", "", []) { Input = input, PresentationId = 1 };
        presenter.Display(story); presenter.Answer.Text = "Игрок"; presenter.Answer.Select(1, 2);
        presenter.Display(story with { HasBackgroundTasks = true, InputFeedback = "Неверный код" });
        Assert.Equal("Игрок", presenter.Draft); Assert.Equal(1, presenter.Answer.SelectionStart); Assert.Equal(2, presenter.Answer.SelectionLength);
        Assert.Equal("Неверный код", presenter.Feedback.Text); Assert.Equal(Visibility.Collapsed, presenter.Placeholder.Visibility);
        presenter.Display(story with { PresentationId = 2 }); Assert.Equal("", presenter.Draft);
        Assert.Equal(Visibility.Visible, presenter.Placeholder.Visibility);
        presenter.Display(StoryFrame.Empty); Assert.Equal(Visibility.Collapsed, presenter.Visibility);
        await Task.CompletedTask;
    });

    [Fact]
    public Task InputPresenter_AutoFocusHonorsOptOut_AndPlaceholderHidesWhenFieldFocused() => Sta(async () =>
    {
        var presenter = new TextInputPresenter(); var other = new TextBox(); var panel = new StackPanel(); panel.Children.Add(other); panel.Children.Add(presenter);
        var host = new Window { Content = panel, Width = 400, Height = 300, ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            host.Show(); other.Focus();
            presenter.Display(new("input", "", "", []) { Input = new() { AutoFocus = false, Placeholder = "Подсказка" }, PresentationId = 1 });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(other, FocusManager.GetFocusedElement(host)); Assert.Equal(Visibility.Visible, presenter.Placeholder.Visibility);
            presenter.Display(new("input", "", "", []) { Input = new() { AutoFocus = true, Placeholder = "Подсказка" }, PresentationId = 2 });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(presenter.Answer, FocusManager.GetFocusedElement(host));
            Assert.Equal(Visibility.Collapsed, presenter.Placeholder.Visibility);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InputPresenter_FormatsMaskForTypingAndPaste_IncludingPassword(bool password) => Sta(async () =>
    {
        var presenter = new TextInputPresenter();
        presenter.Display(new("input", "", "", []) { Input = new() { InputMask = "000-000", MaximumLength = 7, IsPassword = password, AutoFocus = false } });
        if (password) presenter.Password.Password = "123456"; else presenter.Answer.Text = "123456";
        Assert.Equal("123-456", presenter.Draft);
        Assert.Equal(password ? Visibility.Visible : Visibility.Collapsed, presenter.Password.Visibility);
        Assert.Equal(password ? Visibility.Collapsed : Visibility.Visible, presenter.Answer.Visibility);
        if (password) presenter.Password.Password = "12a34567"; else presenter.Answer.Text = "12a34567";
        Assert.Equal("123-456", presenter.Draft);
        TextInputSubmission? received = null; presenter.Submitted += (_, submission) => received = submission;
        presenter.SubmitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new TextInputSubmission("123-456"), received);
        await Task.CompletedTask;
    });

    [Fact]
    public Task InputPresenter_EnforcesUnicodeLengthWithoutSplittingGraphemes_AndKeepsLuaTextAsData() => Sta(async () =>
    {
        var presenter = new TextInputPresenter(); presenter.Display(new("input", "", "", []) { Input = new() { MaximumLength = 2, AutoFocus = false } });
        presenter.Answer.Text = "😀е\u0301x"; Assert.Equal("😀е\u0301", presenter.Draft);
        presenter.Display(new("input", "", "", []) { Input = new() { MaximumLength = 256, AutoFocus = false }, PresentationId = 2 });
        const string raw = "\"); error('injected') --"; presenter.Answer.Text = raw;
        TextInputSubmission? result = null; presenter.Submitted += (_, answer) => result = answer;
        RaiseKey(presenter.Answer, Key.Enter); Assert.Equal(raw, result?.Text); Assert.False(result?.Cancel);
        await Task.CompletedTask;
    });

    [Fact]
    public Task InputPresenter_BoundsPathologicalCombiningGraphemesBeforeRuntimeSubmission() => Sta(async () =>
    {
        var presenter = new TextInputPresenter(); presenter.Display(new("input", "", "", []) { Input = new() { MaximumLength = 256, AutoFocus = false } });
        presenter.Answer.Text = "a" + new string('\u0301', 8192); Assert.Equal("", presenter.Draft);
        presenter.Answer.Text = "a" + new string('\u0301', 8191) + "b"; Assert.Equal(8192, presenter.Draft.Length);
        TextInputSubmission? submission = null; presenter.Submitted += (_, answer) => submission = answer;
        presenter.Submit(); Assert.NotNull(submission); Assert.True(new TextInputSettings { MaximumLength = 256 }.ValidateAnswer(submission!.Text));
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InputPresenter_EscapeAndBackOnlyCancelWhenCancelPortExists(bool cancel) => Sta(async () =>
    {
        var presenter = new TextInputPresenter(); presenter.Display(new("input", "", "", []) { Input = new() { BranchOnResult = true, HasCancelOutput = cancel, AutoFocus = false } });
        var answers = new List<TextInputSubmission>(); presenter.Submitted += (_, answer) => answers.Add(answer);
        var key = RaiseKey(presenter.Answer, Key.Escape); Assert.True(key.Handled);
        presenter.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(cancel ? Visibility.Visible : Visibility.Collapsed, presenter.CancelButton.Visibility);
        Assert.Equal(cancel ? 2 : 0, answers.Count); Assert.All(answers, answer => Assert.True(answer.Cancel));
        await Task.CompletedTask;
    });

    [Fact]
    public Task ScenePreview_InputDraftSurvivesRenderTicks_AndStaticFrameDisablesSubmission() => Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var preview = new ScenePreview();
        var frame = new StoryFrame("input", "", "", []) { Input = new() { AutoFocus = false }, PresentationId = 7 };
        preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, frame, false, true);
        var input = (TextInputPresenter)preview.FindName("PlayerInput"); input.Answer.Text = "draft"; input.Answer.CaretIndex = 2;
        preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, frame with { InputFeedback = "Retry" }, false, true);
        Assert.Equal("draft", input.Draft); Assert.Equal(2, input.Answer.CaretIndex); Assert.Equal("Retry", input.Feedback.Text);
        Assert.Equal(Visibility.Visible, ((Border)preview.FindName("DialoguePanel")).Visibility);
        Assert.Equal(project.Engine.Theme.Dialogue.FontSize, input.Answer.FontSize);
        TextInputSubmission? submitted = null; preview.SceneInputRequested += (_, answer) => submitted = answer;
        input.Submit(); Assert.Equal("draft", submitted?.Text);
        submitted = null; preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, frame, false, false);
        input.Submit(); Assert.Null(submitted); Assert.False(input.IsEnabled); preview.Stop();
    });

    [Fact]
    public Task GameWindow_InputKeepsFocusAndDraftAcrossBackgroundTicks_EscapeWithoutCancelCannotPause_AndEnterSubmits() => Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); project.Engine.Game.AutoSave = false;
        var window = new GameWindow(project) { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        var session = new InputSession(project.Engine.RuntimeDefaults(project.Title));
        var original = (IGameSession)Field("runtime").GetValue(window)!; await original.DisposeAsync(); Field("runtime").SetValue(window, session);
        Field("loaded").SetValue(window, true); Field("ready").SetValue(window, true);
        ((DispatcherTimer)Field("renderTimer").GetValue(window)!).Stop();
        try
        {
            window.Show(); Invoke("ShowGameplay"); Invoke("RenderOutput");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var input = (TextInputPresenter)window.FindName("PlayerInput");
            Assert.Same(input.Answer, FocusManager.GetFocusedElement(window));
            input.Answer.Text = "draft"; input.Answer.Select(1, 2);
            for (int i = 0; i < 3; i++) await (Task)Invoke("PumpSceneTimeAsync", .1)!;
            Assert.Equal(3, session.Ticks); Assert.True(input.Answer.IsEnabled); Assert.Equal("draft", input.Draft);
            Assert.Equal(1, input.Answer.SelectionStart); Assert.Equal(2, input.Answer.SelectionLength);
            Assert.Same(input.Answer, FocusManager.GetFocusedElement(window));
            Assert.True(RaiseKey(input.Answer, Key.Escape).Handled); Assert.False(session.Cancelled);
            Assert.Equal(Visibility.Collapsed, ((Border)window.FindName("MenuOverlay")).Visibility);
            RaiseKey(input.Answer, Key.Enter);
            Assert.Equal("draft", session.Answer); Assert.Equal("dialogue", session.Story.Kind);
            Assert.Equal(Visibility.Collapsed, input.Visibility); Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("DialogueText")).Visibility);
        }
        finally { window.Close(); }
        FieldInfo Field(string name) => typeof(GameWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Invoke(string name, params object[] args) => typeof(GameWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    });

    private sealed class InputSession(RuntimeWindowSettings settings) : IGameSession
    {
        public RuntimeWindowSettings WindowSettings => settings;
        public StoryFrame Story { get; private set; } = new("input", "", "Question", []) { Input = new(), PresentationId = 1, HasBackgroundTasks = true };
        public SceneVisuals Visuals => SceneVisuals.Empty;
        public int Ticks { get; private set; }
        public string? Answer { get; private set; }
        public bool Cancelled { get; private set; }
        public void SetStory(StoryFrame frame) => Story = frame;
        public Task StartAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task AdvanceAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ChooseAsync(int option, CancellationToken token = default) => Task.CompletedTask;
        public Task SubmitInputAsync(string text, bool cancel = false, CancellationToken token = default) { Answer = text; Cancelled = cancel; Story = new("dialogue", "", "Accepted", []); return Task.CompletedTask; }
        public Task TickAsync(double seconds, CancellationToken token = default) { Ticks++; return Task.CompletedTask; }
        public Task ProcessCommandAsync(string command, CancellationToken token = default) => Task.CompletedTask;
        public Task<GameCheckpoint> GetCheckpointAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task RestoreAsync(GameCheckpoint checkpoint, CancellationToken token = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public Task InputScenes_DefaultLayoutKeepsFieldAndButtonsInsideDialogue_AndUsesThemeTypography() => Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var preview = new ScenePreview(); var window = new GameWindow(project);
        var settings = new TextInputSettings { Prompt = "Введите пароль от сейфа:", Placeholder = "Введите код…", InputMask = "000-000", MaximumLength = 7, BranchOnResult = true, HasCancelOutput = true, AutoFocus = false };
        var story = new StoryFrame("input", "", settings.Prompt, []) { Input = settings, PresentationId = 1 };
        var session = new InputSession(project.Engine.RuntimeDefaults(project.Title)); session.SetStory(story);
        var field = typeof(GameWindow).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((IGameSession)field.GetValue(window)!).DisposeAsync(); field.SetValue(window, session);
        typeof(GameWindow).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        try
        {
            typeof(GameWindow).GetMethod("ShowGameplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            typeof(GameWindow).GetMethod("RenderOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            preview.DisplayScene(project, session.WindowSettings, SceneVisuals.Empty, story, false, true);
            foreach (var item in new[] { (Owner: (FrameworkElement)window, Root: (FrameworkElement)window.Content, Name: "input-game.png"), (Owner: (FrameworkElement)preview, Root: (FrameworkElement)preview, Name: "input.png") })
            {
                item.Root.Measure(new Size(1280, 720)); item.Root.Arrange(new Rect(0, 0, 1280, 720)); item.Root.UpdateLayout();
                var input = (TextInputPresenter)item.Owner.FindName("PlayerInput"); var dialogue = (Border)item.Owner.FindName("DialoguePanel");
                var scroll = Descendants(dialogue).OfType<ScrollViewer>().First();
                var position = input.CancelButton.TranslatePoint(new Point(), scroll);
                Assert.InRange(position.Y, 0, scroll.ViewportHeight); Assert.True(position.Y + input.CancelButton.ActualHeight <= scroll.ViewportHeight);
                Assert.Equal(project.Engine.Theme.Dialogue.FontSize, input.Answer.FontSize); Assert.True(input.Answer.ActualWidth > 500);
                if (Environment.GetEnvironmentVariable("KNE_INPUT_RENDER") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory); var image = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); image.Render(item.Root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(Path.Combine(directory, item.Name)); encoder.Save(output);
                }
            }
        }
        finally { preview.Stop(); window.Close(); }
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    internal static KeyEventArgs RaiseKey(UIElement target, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, new Source(), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args); return args;
    }
    private sealed class Source : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    internal static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () => { try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); } finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); } }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
