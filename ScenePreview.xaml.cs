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

    public ScenePreview()
    {
        InitializeComponent();
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

    private async Task RunAsync(bool reset, int? choice = null)
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
        StepButton.IsEnabled = !value && preview != null && !preview.NeedsChoice && !preview.Story.Completed;
        ChoicePanel.IsEnabled = !value;
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
        CharacterImage.Width = theme.CharacterWidth;
        ClearScene();
    }

    private void ClearScene()
    {
        BackgroundImage.Source = CharacterImage.Source = null;
        DialoguePanel.Visibility = Visibility.Collapsed; ChoicePanel.Children.Clear();
        AnimatedBackdrop.Configure(project?.Engine.Theme ?? new(), false);
        StepButton.IsEnabled = false;
    }

    private void RenderSnapshot(EditorPreview value, bool animate)
    {
        if (project == null) return;
        var visuals = value.Visuals; var story = value.Story; var theme = project.Engine.Theme;
        SceneArea.Width = value.Window.Width; SceneArea.Height = value.Window.Height;
        SceneArea.Background = Brush(value.Window.Background);
        DefaultBackground.Visibility = value.Window.Background == theme.BackgroundColor ? Visibility.Visible : Visibility.Collapsed;
        DefaultBackgroundImage.Visibility = DefaultBackground.Visibility;
        BackgroundImage.Source = LoadImage(visuals.Background); CharacterImage.Source = LoadImage(visuals.Character);
        AnimatedBackdrop.Visibility = visuals.Background == null && theme.BackgroundImage == null && DefaultBackground.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        AnimatedBackdrop.Configure(theme, animate);
        CharacterImage.HorizontalAlignment = visuals.Position switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        DialoguePanel.Visibility = story.Text.Length > 0 || story.Options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeakerText.Text = story.Speaker; SpeakerText.Visibility = story.Speaker.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogueText.Text = story.Text; ChoicePanel.Children.Clear();
        for (int i = 0; i < story.Options.Count; i++)
        {
            int option = i + 1;
            var button = new Button { Content = new TextBlock { Text = story.Options[i], TextWrapping = TextWrapping.Wrap },
                FontSize = 16, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
                Foreground = Brush(theme.TextColor), Background = Brush(theme.ButtonColor), BorderBrush = Brush(theme.AccentColor),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Click += async (_, _) => await RunAsync(reset: false, choice: option);
            ChoicePanel.Children.Add(button);
        }
        PreviewStatus.Text = value.Status;
        PreviewStatus.ToolTip = null;
        StepButton.IsEnabled = !value.NeedsChoice && !story.Completed;
    }

    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    private static BitmapImage? LoadImage(string? path)
    {
        if (path == null) return null;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path); image.EndInit(); image.Freeze(); return image;
    }
}
