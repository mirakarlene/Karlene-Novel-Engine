using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class EditorPreviewTests
{
    [Fact]
    public async Task Seek_FromMainCarriesCallerVariablesArgumentsAndBackground_StandaloneStartsSelectedFunction()
    {
        using var fixture = new TempProject();
        string bg = fixture.Write("bg.png", "resource");
        fixture.Write("game/scenes/main.krl", "function main() state.score=9; background('bg.png'); call(chapter,'Вызов') end");
        string path = fixture.Write("game/scenes/chapter.krl", "function chapter(title) d(nil,'Старое') end");
        var project = await fixture.LoadAsync();
        const string source = "function chapter(title)\n local text = (title or 'Соло') .. ':' .. state.score\n d(nil,text)\nend";
        await using var preview = new EditorPreview(project, _ => { });
        var selection = new EditorSelection(path, source, 3, null, false, "chapter");
        await preview.SeekAsync(selection);
        Assert.True(preview.Reached); Assert.Equal("Вызов:9", preview.Story.Text);
        Assert.Equal(bg, preview.Visuals.Background, ignoreCase: true);
        await preview.SeekAsync(selection with { FromMain = false });
        Assert.True(preview.Reached); Assert.Equal("Соло:0", preview.Story.Text); Assert.Null(preview.Visuals.Background);
        Assert.Contains("Старое", File.ReadAllText(path));
        Assert.Equal("game/scenes/main.krl", (await fixture.LoadAsync()).Configuration.EntryPoints!.MainScene);
    }

    [Fact]
    public async Task Seek_UnsavedCodeShowsResourcesAtSelectedLine_AndDoesNotWriteSourcesOrSaves()
    {
        using var fixture = new TempProject();
        string bg = fixture.Write("game/backgrounds/bg.png", "resource");
        string character = fixture.Write("game/characters/actor.png", "resource");
        var project = await fixture.LoadAsync();
        string path = project.MainScenePath; string original = File.ReadAllText(path);
        const string source = "function main()\n background('game/backgrounds/bg.png')\n show_character('game/characters/actor.png', 'left')\n d('Алиса','Тест')\n hide_character()\nend";
        await using var preview = new EditorPreview(project, _ => { });
        await preview.SeekAsync(new(path, source, 3, null, false, "main"));
        Assert.True(preview.Reached); Assert.Equal(3, preview.Location!.Line);
        Assert.Equal(bg, preview.Visuals.Background, ignoreCase: true); Assert.Equal(character, preview.Visuals.Character, ignoreCase: true);
        Assert.Equal("left", preview.Visuals.Position); Assert.Equal("preview", preview.Story.Kind);
        await preview.SeekAsync(new(path, source, 4, null, false, "main"));
        Assert.Equal("Тест", preview.Story.Text); Assert.Equal("Алиса", preview.Story.Speaker);
        await preview.StepAsync(); Assert.Null(preview.Visuals.Character); Assert.Equal(bg, preview.Visuals.Background, ignoreCase: true);
        Assert.Equal(original, File.ReadAllText(path)); Assert.False(File.Exists(Path.Combine(project.Path, "saves", "auto.json")));
    }

    [Fact]
    public async Task Seek_BlockBeyondChoiceRequiresExplicitAnswer_ReplaysChoice_AndResetReopensBranch()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string path = fixture.Write("chapter.knegraph", new VisualScript { FunctionName = "chapter", Nodes =
        [
            new() { Id = "start", Kind = ScriptNodeKind.Start, Next = "choice" },
            new() { Id = "choice", Kind = ScriptNodeKind.Choice, Text = "Куда?", Options = [new() { Text = "Дом", Target = "home" }, new() { Text = "Лес", Target = "forest" }] },
            new() { Id = "home", Kind = ScriptNodeKind.Dialogue, Text = "Дома", Next = "end" },
            new() { Id = "forest", Kind = ScriptNodeKind.Dialogue, Text = "В лесу", Next = "end" },
            new() { Id = "end", Kind = ScriptNodeKind.End }
        ] }.Serialize());
        string code = VisualScript.Parse(File.ReadAllText(path)).Compile(editorPreview: true);
        var target = new EditorSelection(path, code, 1, "forest", false, "chapter", FromMain: false);
        await using var preview = new EditorPreview(project, _ => { });
        await preview.SeekAsync(target);
        Assert.True(preview.NeedsChoice); Assert.False(preview.Reached); Assert.Equal(new[] { "Дом", "Лес" }, preview.Story.Options);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => preview.ChooseAsync(0));
        await preview.ChooseAsync(2);
        Assert.True(preview.Reached); Assert.Equal("forest", preview.Location!.NodeId); Assert.Equal("В лесу", preview.Story.Text);
        await preview.SeekAsync(target); Assert.True(preview.Reached); Assert.Equal("В лесу", preview.Story.Text);
        preview.ResetChoices(); await preview.SeekAsync(target); Assert.True(preview.NeedsChoice);
        await preview.ChooseAsync(1); Assert.False(preview.Reached); Assert.True(preview.Story.Completed);
        Assert.Contains("не достигнут", preview.Status);
    }

    [Fact]
    public async Task Seek_SeparateFunctionAndCallReturnKeepVisualState_GotoSkipsCallerContinuation()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        string background = fixture.Write("bg.png", "resource");
        string path = project.MainScenePath;
        const string source = "function main()\n d(nil,'Начало')\nend\nfunction chapter()\n call(child)\n d(nil,'Возврат')\n goto(finish)\n d(nil,'Не должно быть')\nend\nfunction child() background('bg.png'); d(nil,'Вызов') end\nfunction finish() d(nil,'Конец') end";
        await using var preview = new EditorPreview(project, _ => { });
        await preview.SeekAsync(new(path, source, 6, null, false, "chapter", FromMain: false));
        Assert.True(preview.Reached); Assert.Equal("Возврат", preview.Story.Text);
        Assert.Equal(background, preview.Visuals.Background, ignoreCase: true);
        await preview.StepAsync(); Assert.Equal(7, preview.Location!.Line);
        await preview.StepAsync(); Assert.Equal("Конец", preview.Story.Text);
        await preview.StepAsync(); Assert.True(preview.Story.Completed);
    }

    [Fact]
    public async Task Seek_CancellationStopsWorker_AndAnotherSelectionStillRuns()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var preview = new EditorPreview(project, line => { if (line == "loop-entered") entered.TrySetResult(); });
        using var cancellation = new CancellationTokenSource();
        Task seeking = preview.SeekAsync(new(project.MainScenePath, "function main() print('loop-entered'); while true do end end", 2, null, false, "main"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seeking);
        await preview.SeekAsync(new(project.MainScenePath, "function main() d(nil,'После отмены') end", 1, null, false, "main"));
        Assert.True(preview.Reached); Assert.Equal("После отмены", preview.Story.Text);
    }
}
