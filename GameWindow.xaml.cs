using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KNE;

public partial class GameWindow : Window
{
    private IGameSession runtime;
    private readonly ProjectInfo project;
    private readonly GameSaveStore saves;
    private readonly DispatcherTimer renderTimer;
    private readonly LogBuffer pendingOutput = new();
    private readonly Queue<string> lines = new();
    private CancellationTokenSource? execution;
    private bool ready;
    private bool closed;
    private int textLength;
    private RuntimeWindowSettings? renderedSettings;
    private StoryFrame? renderedStory;
    private SceneVisuals? renderedVisuals;
    private readonly MediaPlayer music = new();
    private readonly MediaPlayer sound = new();

    public GameWindow(ProjectInfo project, GameSaveStore? saves = null)
    {
        InitializeComponent();
        this.project = project;
        this.saves = saves ?? new GameSaveStore(project.Path);
        Title = project.Title;
        runtime = new ProcessGameSession(project, QueueOutput);
        music.MediaEnded += (_, _) => { if (runtime.Visuals.LoopMusic) { music.Position = TimeSpan.Zero; music.Play(); } };
        music.MediaFailed += (_, e) => QueueOutput("Ошибка музыки: " + e.ErrorException.Message);
        sound.MediaFailed += (_, e) => QueueOutput("Ошибка звука: " + e.ErrorException.Message);
        renderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => RenderOutput(), Dispatcher);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ExecuteAsync(() => runtime.StartAsync(execution!.Token), starting: true);

    private async Task ExecuteAsync(Func<Task> action, bool starting = false)
    {
        if (closed || execution != null) return;
        execution = new CancellationTokenSource();
        StopButton.IsEnabled = true;
        CommandInput.IsEnabled = false;
        NextButton.IsEnabled = false;
        ChoicePanel.IsEnabled = false;
        StatusText.Text = starting ? "Загрузка сценария…" : "Выполнение команды…";
        try
        {
            await action();
            if (closed) return;
            if (starting) ready = true;
            if (ready && (!starting || !saves.Exists("auto")))
            {
                try { await saves.SaveAsync("auto", await runtime.GetCheckpointAsync(execution.Token), execution.Token); }
                catch (Exception ex) { QueueOutput("Автосохранение не выполнено: " + ex.Message); }
            }
            StatusText.Text = "Готово";
        }
        catch (OperationCanceledException)
        {
            QueueOutput("Выполнение остановлено.");
            StatusText.Text = "Остановлено";
        }
        catch (Exception ex)
        {
            QueueOutput("Ошибка: " + ex.Message);
            StatusText.Text = "Ошибка выполнения";
        }
        finally
        {
            execution.Dispose();
            execution = null;
            if (!closed)
            {
                StopButton.IsEnabled = false;
                CommandInput.IsEnabled = ready;
                RenderOutput();
                if (ready) CommandInput.Focus();
            }
        }
    }

    private async void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || execution != null || !ready) return;
        e.Handled = true;
        string command = CommandInput.Text.Trim();
        if (command.Length == 0) return;
        CommandInput.Clear();
        await ExecuteAsync(() => runtime.ProcessCommandAsync(command, execution!.Token));
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        execution?.Cancel();
        StopButton.IsEnabled = false;
        StatusText.Text = "Остановка…";
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (execution != null || !ready) return;
        await ExecuteAsync(async () =>
        {
            await saves.SaveAsync("quick", await runtime.GetCheckpointAsync(execution!.Token), execution.Token);
            QueueOutput("Игра сохранена.");
        });
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (execution != null) return;
        string slot = sender is Button { Tag: "auto" } ? "auto" : "quick";
        await ExecuteAsync(async () =>
        {
            var checkpoint = await saves.LoadAsync(slot, execution!.Token);
            var candidate = new ProcessGameSession(project, QueueOutput);
            try { await candidate.RestoreAsync(checkpoint, execution.Token); }
            catch { await candidate.DisposeAsync(); throw; }
            if (closed) { await candidate.DisposeAsync(); return; }
            await runtime.DisposeAsync();
            if (closed) { await candidate.DisposeAsync(); return; }
            runtime = candidate;
            ready = true;
            renderedStory = null;
            renderedSettings = null;
            renderedVisuals = null;
            music.Close(); sound.Close();
            QueueOutput("Сохранение загружено.");
        });
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (NextButton.IsEnabled) await ExecuteAsync(() => runtime.AdvanceAsync(execution!.Token));
        e.Handled = true;
    }

    private async void ChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int index } && execution == null)
            await ExecuteAsync(() => runtime.ChooseAsync(index, execution!.Token));
        e.Handled = true;
    }

    private void Dialogue_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Button && NextButton.IsEnabled) NextButton_Click(NextButton, e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.OriginalSource is not TextBox && NextButton.IsEnabled)
        {
            NextButton_Click(NextButton, e);
            e.Handled = true;
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        closed = true;
        renderTimer.Stop();
        music.Close();
        sound.Close();
        execution?.Cancel();
        try { await runtime.DisposeAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Lua cleanup failed: {0}", ex); }
    }

    private void QueueOutput(string text)
    {
        pendingOutput.Enqueue(text);
    }

    private void RenderOutput()
    {
        if (closed) return;
        bool changed = false;
        foreach (string line in pendingOutput.Drain())
        {
            string entry = line + Environment.NewLine;
            lines.Enqueue(entry);
            textLength += entry.Length;
            while (lines.Count > 1000 || textLength > 200_000) textLength -= lines.Dequeue().Length;
            changed = true;
        }
        if (changed)
        {
            var builder = new StringBuilder(textLength);
            foreach (string line in lines) builder.Append(line);
            ConsoleOutput.Text = builder.ToString();
            ConsoleOutputScroll.ScrollToEnd();
        }
        var settings = runtime.WindowSettings;
        if (settings != renderedSettings)
        {
            Title = settings.Title;
            Width = settings.Width;
            Height = settings.Height;
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.Background));
            renderedSettings = settings;
        }
        RenderStory();
        RenderVisuals();
    }

    private void RenderStory()
    {
        var frame = runtime.Story;
        if (frame != renderedStory)
        {
            SpeakerText.Text = frame.Speaker;
            DialogueText.Text = frame.Text;
            ChoicePanel.Children.Clear();
            for (int i = 0; i < frame.Options.Count; i++)
            {
                var button = new Button { Content = frame.Options[i], Tag = i + 1, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };
                button.ContentTemplate = (DataTemplate)FindResource("ChoiceLabelTemplate");
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                button.Click += ChoiceButton_Click;
                ChoicePanel.Children.Add(button);
            }
            renderedStory = frame;
        }
        NextButton.IsEnabled = execution == null && frame.Kind == "dialogue";
        ChoicePanel.IsEnabled = execution == null && frame.Kind == "choice";
        SaveButton.IsEnabled = execution == null && ready;
        LoadButton.IsEnabled = execution == null && saves.Exists("quick");
        LoadAutoButton.IsEnabled = execution == null && saves.Exists("auto");
        if (execution == null && ready)
            StatusText.Text = frame.Kind switch { "dialogue" => "Ожидание продолжения", "choice" => "Выберите ответ", "completed" => "История завершена", _ => "Готово" };
    }

    private void RenderVisuals()
    {
        var scene = runtime.Visuals;
        if (scene == renderedVisuals) return;
        if (scene.Background != renderedVisuals?.Background) BackgroundImage.Source = LoadImage(scene.Background);
        if (scene.Character != renderedVisuals?.Character) CharacterImage.Source = LoadImage(scene.Character);
        CharacterImage.HorizontalAlignment = scene.Position switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        try
        {
            if (scene.MusicVersion != renderedVisuals?.MusicVersion)
            {
                music.Close();
                if (scene.Music != null) { music.Open(new Uri(scene.Music)); music.Play(); }
            }
        }
        catch (Exception ex) { QueueOutput("Ошибка музыки: " + ex.Message); }
        try
        {
            if (scene.Sound != null && scene.SoundVersion != renderedVisuals?.SoundVersion)
            {
                sound.Open(new Uri(scene.Sound)); sound.Play();
            }
        }
        catch (Exception ex) { QueueOutput("Ошибка звука: " + ex.Message); }
        renderedVisuals = scene;
    }

    private BitmapImage? LoadImage(string? path)
    {
        if (path == null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) { QueueOutput("Ошибка изображения: " + ex.Message); return null; }
    }
}
