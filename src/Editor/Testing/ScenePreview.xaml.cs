using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KNE;

/// <summary>Scaled rendering of an editor worker, with coalesced selection changes and explicit branch choices.</summary>
public partial class ScenePreview : UserControl
{
    private EditorPreview? preview;
    private ProjectInfo? project;
    private EditorSelection? pendingSelection;
    private CancellationTokenSource? operation;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private int revision;
    private bool active;
    private bool resetChoices;
    private bool captureSelection;
    public Func<EditorSelection?>? SelectionProvider { get; set; }
    public event EventHandler<int>? SceneChoiceRequested;
    public event EventHandler<TextInputSubmission>? SceneInputRequested;
    internal void StopChoicePresentation() { choicePresentation?.Stop(); ChoiceTimer.Visibility = Visibility.Collapsed; }
    /// <summary>Resets presentation clocks before replaying or restarting a scene, without creating a worker.</summary>
    public void ResetPresentation()
    {
        PlayerInput.Reset();
        choicePresentation?.Reset();
        ResetGameplayUi();
        DialogueEffects.Reset();
        sceneEffects?.Stop();
        AnimatedBackdrop.BeginAnimation(AnimatedBackground.PhaseProperty, null);
        renderedBackdropAnimate = null; renderedSceneStory = null;
        renderedOverlay = null; renderedOverlayVersion = -1;
    }
    private bool standalone;
    private SceneEffectsPresenter? sceneEffects;
    private string? renderedOverlay;
    private long renderedOverlayVersion = -1;
    private string? renderedBackgroundPath;
    private string? renderedCharacterPath;
    private bool? renderedBackdropAnimate;
    private StoryFrame? renderedSceneStory;
    private ChoicePresentation? choicePresentation;
    public void DisplayScene(ProjectInfo value, RuntimeWindowSettings window, SceneVisuals visuals, StoryFrame story, bool animate, bool allowChoices)
    {
        standalone = true; PreviewToolbar.Visibility = PreviewStatus.Visibility = Visibility.Collapsed;
        if (!ReferenceEquals(project, value)) { project = value; ApplyTheme(); }
        RenderScene(window, visuals, story, animate);
        ChoicePanel.IsHitTestVisible = allowChoices;
        ScriptScreenCanvas.IsHitTestVisible = MainScriptScreenCanvas.IsHitTestVisible = allowChoices && story.Triggers.Any(trigger => trigger.Kind is "button" or "hotspot");
        foreach (var entry in previewUiButtons)
            entry.Value.IsHitTestVisible = allowChoices && story.Triggers.Any(trigger => trigger.Matches(new("button", Button: entry.Key)) || trigger.Kind == "hotspot");
        PlayerInput.Display(story, allowChoices);
    }

    public ScenePreview()
    {
        InitializeComponent();
        sceneEffects = new(VisualLayer, BackgroundImage, CharacterImage, SceneFlash, SceneTitle);
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            try
            {
                if (captureSelection) { pendingSelection = SelectionProvider?.Invoke(); captureSelection = false; }
                await RunAsync(reset: true);
            }
            catch (Exception error) { ClearScene(); PreviewStatus.Text = error.Message; }
        };
    }

    public void Open(ProjectInfo value)
    {
        Stop(); project = value; active = true;
        preview = new EditorPreview(value, _ => { });
        ApplyTheme();
    }

    public void Select(EditorSelection? selection)
    {
        if (!active || selection == null) return;
        captureSelection = false; pendingSelection = selection; revision++;
        operation?.Cancel();
        debounce.Stop(); debounce.Start();
    }

    public void RequestSelection()
    {
        if (!active) return;
        captureSelection = true; revision++; operation?.Cancel();
        debounce.Stop(); debounce.Start();
    }

    public void Stop()
    {
        active = false; revision++; debounce.Stop(); operation?.Cancel();
        ResetPresentation();
        var previous = preview; preview = null; pendingSelection = null;
        AnimatedBackdrop.BeginAnimation(AnimatedBackground.PhaseProperty, null);
        if (previous != null) _ = DisposePreviousAsync(previous);
    }

    private async Task DisposePreviousAsync(EditorPreview previous)
    {
        await gate.WaitAsync();
        try { await previous.DisposeAsync(); }
        finally { gate.Release(); }
    }

    private async Task RunAsync(bool reset, int? choice = null, TextInputSubmission? input = null)
    {
        if (!active || preview == null || pendingSelection == null) return;
        var current = preview; var selected = pendingSelection with { FromMain = FromMainInput.IsChecked == true }; int version = revision;
        operation?.Cancel();
        var cancellation = new CancellationTokenSource(); operation = cancellation;
        bool entered = false;
        try
        {
            await gate.WaitAsync(cancellation.Token); entered = true;
            SetBusy(true); PreviewStatus.Text = "Подготовка кадра…";
            if (reset)
            {
                if (resetChoices) { current.ResetChoices(); resetChoices = false; }
                await current.SeekAsync(selected, cancellation.Token);
            }
            else if (choice != null) await current.ChooseAsync(choice.Value, cancellation.Token);
            else if (input != null) await current.SubmitInputAsync(input.Text, input.Cancel, cancellation.Token);
            else await current.StepAsync(cancellation.Token);
            if (version == revision && active) RenderSnapshot(current, selected.Animate);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version == revision && active)
            {
                ClearScene();
                var report = GameErrorReport.TryGet(error);
                PreviewStatus.Text = report == null ? error.Message : $"Ошибка {report.File}:{report.Line}: {report.Message}";
                PreviewStatus.ToolTip = report?.ToDisplayText();
            }
        }
        finally
        {
            if (entered) gate.Release();
            if (ReferenceEquals(operation, cancellation)) { operation = null; if (active) SetBusy(false); }
            cancellation.Dispose();
        }
    }

    private void Seek_Click(object sender, RoutedEventArgs e) => RequestSelection();
    private void FromMain_Changed(object sender, RoutedEventArgs e) => RequestSelection();
    private void Reset_Click(object sender, RoutedEventArgs e) { resetChoices = true; RequestSelection(); }
    private async void Step_Click(object sender, RoutedEventArgs e) => await RunAsync(reset: false);

    private void SetBusy(bool value)
    {
        StepButton.IsEnabled = !value && preview != null && !preview.NeedsChoice && preview.Story.CanAdvance;
        ChoicePanel.IsEnabled = !value;
        PlayerInput.IsEnabled = !value;
    }

    private void ApplyTheme()
    {
        if (project == null) return;
        var theme = project.Engine.Theme; var dialogue = theme.Dialogue;
        SceneArea.Width = project.Engine.Window.Width; SceneArea.Height = project.Engine.Window.Height;
        SceneArea.Background = EnginePresentation.CreateBackground(theme);
        DefaultBackground.Background = EnginePresentation.CreateBackground(theme);
        DefaultBackgroundImage.Source = LoadImage(theme.BackgroundImage == null ? null : ResourcePaths.Resolve(project.Path, theme.BackgroundImage));
        DialoguePanel.Height = dialogue.Height; DialoguePanel.Margin = new Thickness(dialogue.Margin);
        DialoguePanel.Padding = new Thickness(dialogue.Padding); DialoguePanel.CornerRadius = new CornerRadius(dialogue.CornerRadius);
        DialoguePanel.Background = Brush(dialogue.BackgroundColor); DialoguePanel.BorderBrush = Brush(dialogue.BorderColor);
        SpeakerText.FontSize = dialogue.SpeakerFontSize; SpeakerText.Foreground = Brush(dialogue.SpeakerColor);
        DialogueText.FontSize = dialogue.FontSize; DialogueText.Foreground = Brush(dialogue.TextColor);
        SpeakerText.FontFamily = DialogueText.FontFamily = new FontFamily(theme.FontFamily);
        SceneTitle.FontFamily = new FontFamily(theme.FontFamily); SceneTitle.Foreground = Brush(theme.TextColor);
        CharacterImage.Width = theme.CharacterWidth;
        ClearScene();
    }

    private void ClearScene()
    {
        ResetGameplayUi();
        DialogueEffects.Reset();
        choicePresentation?.Reset();
        sceneEffects?.Stop();
        ScriptScreenCanvas.Children.Clear(); ScriptScreenViewbox.Visibility = Visibility.Collapsed;
        renderedOverlay = null; renderedOverlayVersion = -1;
        BackgroundImage.Source = CharacterImage.Source = null;
        renderedBackgroundPath = renderedCharacterPath = null;
        renderedBackdropAnimate = null; renderedSceneStory = null;
        PlayerInput.Reset();
        choicePresentation?.Reset();
        DialoguePanel.Visibility = Visibility.Collapsed; ChoicePanel.Children.Clear();
        AnimatedBackdrop.Configure(project?.Engine.Theme ?? new(), false);
        StepButton.IsEnabled = false;
    }

    private void RenderSnapshot(EditorPreview value, bool animate)
    {
        RenderScene(value.Window, value.Visuals, value.Story, animate);
        PreviewStatus.Text = value.Status; PreviewStatus.ToolTip = null;
        StepButton.IsEnabled = !value.NeedsChoice && value.Story.CanAdvance;
    }

    private void RenderScene(RuntimeWindowSettings window, SceneVisuals visuals, StoryFrame story, bool animate)
    {
        if (project == null) return;
        var theme = project.Engine.Theme;
        SceneArea.Width = window.Width; SceneArea.Height = window.Height;
        SceneArea.Background = Brush(window.Background);
        DefaultBackground.Visibility = !visuals.BackgroundsCleared && window.Background == theme.BackgroundColor ? Visibility.Visible : Visibility.Collapsed;
        DefaultBackgroundImage.Visibility = DefaultBackground.Visibility;
        if (renderedBackgroundPath != visuals.Background) { BackgroundImage.Source = LoadImage(visuals.Background); renderedBackgroundPath = visuals.Background; }
        if (renderedCharacterPath != visuals.Character) { CharacterImage.Source = LoadImage(visuals.Character); renderedCharacterPath = visuals.Character; }
        AnimatedBackdrop.Visibility = visuals.Background == null && theme.BackgroundImage == null && DefaultBackground.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (renderedBackdropAnimate != animate) { AnimatedBackdrop.Configure(theme, animate); renderedBackdropAnimate = animate; }
        CharacterImage.HorizontalAlignment = visuals.Position switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        sceneEffects!.Apply(visuals, animate);
        if (renderedOverlay != visuals.OverlayScreen || renderedOverlayVersion != visuals.ScreenVersion)
        {
            ScriptScreenCanvas.Children.Clear();
            MainScriptScreenCanvas.Children.Clear();
            var layout = project.Engine.Screens.Screens.FirstOrDefault(item => item.Id == visuals.OverlayScreen);
            ScriptScreenViewbox.Visibility = layout != null && visuals.ScreenLayer == "overlay" ? Visibility.Visible : Visibility.Collapsed;
            MainScriptScreenViewbox.Visibility = layout != null && visuals.ScreenLayer == "main" ? Visibility.Visible : Visibility.Collapsed;
            ScriptScreenViewbox.Opacity = MainScriptScreenViewbox.Opacity = visuals.ScreenOpacity;
            var targetCanvas = visuals.ScreenLayer == "main" ? MainScriptScreenCanvas : ScriptScreenCanvas;
            if (layout != null)
                foreach (var element in layout.Elements)
                {
                    var view = ScreenRenderer.Create(element, theme, project.Path, text => text.Replace("{{title}}", project.Title)
                        .Replace("{{subtitle}}", project.Engine.Game.Subtitle).Replace("{{start_label}}", project.Engine.Game.StartLabel)
                        .Replace("{{continue_label}}", project.Engine.Game.ContinueLabel).Replace("{{exit_label}}", project.Engine.Game.ExitLabel));
                    view.Width = element.Width; view.Height = element.Height; Canvas.SetLeft(view, element.X); Canvas.SetTop(view, element.Y);
                    GameTriggerInput.SetButtonId(view, "screen:" + layout.Name + "/" + element.Id);
                    targetCanvas.Children.Add(view); ScreenRenderer.Animate(view, element, animate);
                }
            renderedOverlay = visuals.OverlayScreen; renderedOverlayVersion = visuals.ScreenVersion;
        }
        PlayerInput.Display(story, !standalone || PlayerInput.IsEnabled);
        choicePresentation ??= new(ChoicePanel, ChoiceTimer, button =>
        {
            button.FontSize = 16;
            button.Foreground = Brush(theme.TextColor); button.Background = Brush(theme.ButtonColor); button.BorderBrush = Brush(theme.AccentColor);
        }, async (sender, args) =>
        {
            if (sender is not Button { Tag: int option }) return;
            if (standalone) SceneChoiceRequested?.Invoke(this, option);
            else await RunAsync(reset: false, choice: option);
            args.Handled = true;
        });
        choicePresentation.Display(story, animate && theme.AnimationEnabled, Brush(theme.AccentColor));
        if (renderedSceneStory is { } previousStory && previousStory.Kind == story.Kind && previousStory.Speaker == story.Speaker &&
            previousStory.Text == story.Text && previousStory.Options.SequenceEqual(story.Options))
        {
            ApplyGameplayUi(visuals, story);
            RenderNarrative(story, visuals, animate);
            return;
        }
        renderedSceneStory = story;
        DialoguePanel.Visibility = story.Text.Length > 0 || story.Options.Count > 0 || story.Kind == "input" ? Visibility.Visible : Visibility.Collapsed;
        SpeakerText.Text = story.Speaker; SpeakerText.Visibility = story.Speaker.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogueText.Text = story.Text;
        ApplyGameplayUi(visuals, story);
        RenderNarrative(story, visuals, animate);
    }

    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static BitmapImage? LoadImage(string? path)
    {
        if (path == null) return null;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
    }
}
