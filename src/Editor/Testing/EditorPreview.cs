using System.IO;
using System.Text.Json;

namespace KNE;

public sealed record EditorSelection(string FilePath, string Source, int Line, string? NodeId, bool Animate, string? Function, bool FromMain = true);

/// <summary>Replays only explicit user choices to reach the selected editor command without saving sources.</summary>
public sealed class EditorPreview : IAsyncDisposable
{
    private readonly ProjectInfo project;
    private readonly Action<string> output;
    private sealed record PreviewChoice(PreviewLocation? Location, string[] Options, ChoiceOptionState[]? States, int Answer);
    private readonly List<PreviewChoice> choices = [];
    private sealed record PreviewInput(PreviewLocation? Location, TextInputSettings Settings, string Text, bool Cancel);
    private readonly List<PreviewInput> inputs = [];
    private int inputIndex;
    private ProcessGameSession? session;
    private EditorSelection? target;
    private int choiceIndex;
    private int? targetFunctionLine;
    public StoryFrame Story => session?.Story ?? StoryFrame.Empty;
    public SceneVisuals Visuals => session?.Visuals ?? SceneVisuals.Empty;
    public double LogicMilliseconds => session?.LogicMilliseconds ?? 0;
    public RuntimeWindowSettings Window => session?.WindowSettings ?? project.Engine.RuntimeDefaults(project.Title);
    public PreviewLocation? Location => session?.Location;
    public bool NeedsChoice => Story.Kind == "choice";
    public bool NeedsInput => Story.Kind == "input";
    public bool Reached { get; private set; }
    public string Status { get; private set; } = "Выберите строку кода или блок.";

    public EditorPreview(ProjectInfo project, Action<string> output)
    {
        this.project = project;
        this.output = output;
    }

    public async Task SeekAsync(EditorSelection selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (target?.FilePath != selection.FilePath || target.Source != selection.Source || target.Function != selection.Function || target.FromMain != selection.FromMain) { choices.Clear(); inputs.Clear(); }
        inputIndex = 0;
        target = selection; choiceIndex = 0; Reached = false;
        targetFunctionLine = selection.Function == null ? null : LuaSource.Functions(selection.Source).FirstOrDefault(f => f.Name == selection.Function)?.Line;
        if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(
            JsonSerializer.Serialize(project.Configuration, ProjectConfiguration.JsonOptions), ProjectConfiguration.JsonOptions)!;
        configuration.EntryPoints ??= new();
        configuration.EntryPoints.MainScene = Path.GetRelativePath(project.Path, PathSafety.EnsureInside(project.Path, selection.FilePath)).Replace('\\', '/');
        var previewProject = new ProjectInfo { Path = project.Path, Configuration = configuration, Engine = project.Engine, ApplicationPlugins = project.ApplicationPlugins };
        session = new ProcessGameSession(previewProject, output, preview: !selection.FromMain,
            editorSource: new EditorPreviewSource(selection.FilePath, selection.Source, selection.FromMain ? null : selection.Function));
        await session.StartAsync(token).ConfigureAwait(false);
        await ReachAsync(token).ConfigureAwait(false);
    }

    public async Task ChooseAsync(int option, CancellationToken token = default)
    {
        if (session == null || !NeedsChoice) throw new InvalidOperationException("Предпросмотр не ожидает выбора.");
        if (option < 1 || option > Story.Options.Count) throw new ArgumentOutOfRangeException(nameof(option));
        if (Story.ChoiceOptions is { } options && !options[option - 1].IsEnabled) throw new InvalidOperationException("Вариант ответа заблокирован.");
        if (choiceIndex < choices.Count) choices.RemoveRange(choiceIndex, choices.Count - choiceIndex);
        choices.Add(new(Location, Story.Options.ToArray(), Story.ChoiceOptions?.ToArray(), option)); choiceIndex++;
        await session.ChooseAsync(option, token).ConfigureAwait(false);
        await ReachAsync(token).ConfigureAwait(false);
    }

    public async Task SubmitInputAsync(string text, bool cancel = false, CancellationToken token = default)
    {
        if (session == null || !NeedsInput || Story.Input is null) throw new InvalidOperationException("Предпросмотр не ожидает ввода.");
        if (inputIndex < inputs.Count) inputs.RemoveRange(inputIndex, inputs.Count - inputIndex);
        inputs.Add(new(Location, Story.Input, text, cancel)); inputIndex++;
        await session.SubmitInputAsync(text, cancel, token).ConfigureAwait(false);
        await ReachAsync(token).ConfigureAwait(false);
    }
    public async Task StepAsync(CancellationToken token = default)
    {
        if (session == null || Story.Completed || NeedsChoice || NeedsInput) return;
        if (Story.Kind == "wait" || (Story.NeedsTick && !Story.CanAdvance)) await session.TickAsync(PreviewTick(), token).ConfigureAwait(false);
        else await session.AdvanceAsync(token).ConfigureAwait(false);
        Reached = Matches(); Status = DescribeLocation();
    }

    public void ResetChoices() { choices.Clear(); inputs.Clear(); }

    private async Task ReachAsync(CancellationToken token)
    {
        for (int step = 0; step < 2000; step++)
        {
            token.ThrowIfCancellationRequested();
            if (Matches()) { Reached = true; Status = DescribeLocation(); return; }
            if (Story.Completed) { Status = "Сценарий завершён. Выбранный сегмент не достигнут в этой ветке. Сбросьте выборы, чтобы пройти другую."; return; }
            if (NeedsInput)
            {
                if (inputIndex >= inputs.Count) { Status = "Введите ответ в предпросмотре, чтобы продолжить к выделенному сегменту."; return; }
                var remembered = inputs[inputIndex];
                if (remembered.Location != Location || remembered.Settings != Story.Input)
                { inputs.RemoveRange(inputIndex, inputs.Count - inputIndex); Status = "Поле ввода изменилось. Введите ответ заново."; return; }
                inputIndex++;
                await session!.SubmitInputAsync(remembered.Text, remembered.Cancel, token).ConfigureAwait(false);
            }
            else if (NeedsChoice)
            {
                if (choiceIndex >= choices.Count) { Status = "Выберите ответ в предпросмотре, чтобы продолжить к выделенному сегменту."; return; }
                var remembered = choices[choiceIndex];
                if (remembered.Location != Location || !remembered.Options.SequenceEqual(Story.Options) ||
                    !(remembered.States is null && Story.ChoiceOptions is null || remembered.States is not null && Story.ChoiceOptions is not null && remembered.States.SequenceEqual(Story.ChoiceOptions)))
                {
                    choices.RemoveRange(choiceIndex, choices.Count - choiceIndex);
                    Status = "Развилка изменилась. Выберите ответ заново."; return;
                }
                choiceIndex++;
                await session!.ChooseAsync(remembered.Answer, token).ConfigureAwait(false);
            }
            else if (Story.Kind == "wait" || (Story.NeedsTick && !Story.CanAdvance)) await session!.TickAsync(PreviewTick(), token).ConfigureAwait(false);
            else await session!.AdvanceAsync(token).ConfigureAwait(false);
        }
        throw new InvalidDataException("Предпросмотр остановлен после 2000 шагов. Выберите более ранний сегмент или проверьте цикл.");
    }

    private double PreviewTick() => Math.Clamp(Story.RemainingSeconds ?? 1, 0.001, 60);

    private bool Matches()
    {
        if (target == null || Location == null) return false;
        string relative = Path.GetRelativePath(project.Path, target.FilePath).Replace('\\', '/');
        if (!Location.File.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase) &&
            !Location.File.Equals(target.FilePath, StringComparison.OrdinalIgnoreCase)) return false;
        if (target.NodeId == null && targetFunctionLine != null && Location.FunctionLine != null && targetFunctionLine != Location.FunctionLine)
            return false;
        return target.NodeId != null ? target.NodeId == Location.NodeId : Location.Line >= target.Line;
    }

    private string DescribeLocation() => Location == null ? "Начало сцены" :
        Location.File + (Location.NodeId == null ? $":{Location.Line} · ближайшая исполняемая команда" : " · блок " + Location.NodeId);

    public async ValueTask DisposeAsync()
    {
        if (session != null) await session.DisposeAsync().ConfigureAwait(false);
        session = null;
    }
}
