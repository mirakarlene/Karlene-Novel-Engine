using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Xunit;

namespace KNE.Tests;

public sealed class InlineTextRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlinePauses_KeepCumulativeText_UseLogicalTime_AndRestoreInsideTimedFragment(bool worker)
    {
        using var fixture = Scene("state.name='Алиса'; d('Говорит {name}', '{color=red}А{name}{w}Б{w=0.5}В{/color}'); d(nil,'after')");
        var project = await fixture.LoadAsync();
        await using IGameSession runtime = worker ? new ProcessGameSession(project, _ => { }) : new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("ААлиса", Plain(runtime.Story.Text)); Assert.Equal("Говорит Алиса", Plain(runtime.Story.Speaker));
        Assert.True(runtime.Story.CanAdvance); Assert.Null(runtime.Story.RemainingSeconds);
        long presentation = runtime.Story.PresentationId;
        await runtime.AdvanceAsync(); Assert.Equal("ААлисаБ", Plain(runtime.Story.Text));
        Assert.Equal(presentation, runtime.Story.PresentationId); Assert.False(runtime.Story.CanAdvance);
        Assert.Equal(.5, runtime.Story.RemainingSeconds);
        await runtime.TickAsync(.2); var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal(.3, runtime.Story.RemainingSeconds!.Value, 7);
        await runtime.TickAsync(.3); Assert.Equal("ААлисаБВ", Plain(runtime.Story.Text)); Assert.True(runtime.Story.CanAdvance);
        await runtime.RestoreAsync(checkpoint); Assert.Equal("ААлисаБ", Plain(runtime.Story.Text));
        Assert.Equal(.3, runtime.Story.RemainingSeconds!.Value, 7);
        await runtime.TickAsync(.3); Assert.Equal("ААлисаБВ", Plain(runtime.Story.Text));
        Assert.Equal("#FF0000", Assert.Single(InlineText.Parse(runtime.Story.Text).Runs).Color);
        await runtime.AdvanceAsync(); Assert.Equal("after", runtime.Story.Text);
    }

    [Fact]
    public async Task Variables_DistinguishMissingNullEmptyInvalidAndLiteralValues_WithoutEvaluatingInsertedMarkup()
    {
        using var fixture = Scene("""
            state.none=nil; state.empty=''; state.flag=false; state.zero=0
            state.bad=1/0; state.array={[1]='a',[3]='c'}; state.valid={1,2}
            state.literal='{w}{color=blue}unsafe{/color}'
            d(nil,'{absent}|{none}|{empty}|{flag}|{zero}|{bad}|{array}|{valid}|{literal}')
            """);
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var document = InlineText.Parse(runtime.Story.Text);
        Assert.Equal("{missing}|{null}|{null}|false|0|{error}|{error}|[1,2]|{w}{color=blue}unsafe{/color}", document.Text);
        Assert.Equal(new[] { "#808080", "#808080", "#808080", "#FF0000", "#FF0000" }, document.Runs.Select(run => run.Color));
        Assert.Empty(document.Pauses); Assert.True(runtime.Story.CanAdvance);
    }

    [Theory]
    [InlineData("state.value={};state.value[1]=state.value")]
    [InlineData("state.value={[1]=1,name='mixed'}")]
    [InlineData("state.value={0/0}")]
    [InlineData("state.value=function()end")]
    [InlineData("state.value={1};for i=1,6 do local v=state.value;state.value={v,v,v,v,v,v,v,v,v,v} end")]
    public async Task InvalidValues_ShowRedErrorWithoutEvaluatingOrRecursingIndefinitely(string assignment)
    {
        using var fixture = Scene(assignment + ";d(nil,'{color=blue}{value}{/color}')");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync();
        var document = InlineText.Parse(runtime.Story.Text); Assert.Equal("{error}", document.Text);
        Assert.Equal("#FF0000", Assert.Single(document.Runs).Color);
    }

    [Theory]
    [InlineData(ScriptNodeKind.Dialogue)]
    [InlineData(ScriptNodeKind.AutoDialogue)]
    [InlineData(ScriptNodeKind.EffectDialogue)]
    public async Task AllVisualDialogueKinds_UseSharedSubstitutionsAndFormatting(ScriptNodeKind kind)
    {
        using var fixture = new TempProject();
        var graph = VisualScript.Create();
        graph.Nodes.Single(node => node.Id == "start").Next = "name";
        graph.Nodes.Add(new() { Id = "name", Kind = ScriptNodeKind.SetVariable, Variable = "name", ValueKind = ScriptValueKind.String, Value = "Алиса", Next = "dialogue" });
        var dialogue = graph.Nodes.Single(node => node.Id == "dialogue"); dialogue.Kind = kind;
        dialogue.Speaker = "{name}"; dialogue.Text = "{color=rgb(255,0,0)}Привет, {name}{/color}";
        if (kind == ScriptNodeKind.EffectDialogue) dialogue.TextEffect = new() { Kind = "wave" };
        await using var runtime = new ProcessGameSession(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { });
        await runtime.StartAsync(); Assert.Equal("Алиса", Plain(runtime.Story.Speaker));
        Assert.Equal("Привет, Алиса", Plain(runtime.Story.Text));
        Assert.Equal("#FF0000", Assert.Single(InlineText.Parse(runtime.Story.Text).Runs).Color);
        if (kind == ScriptNodeKind.EffectDialogue) Assert.Equal("wave", runtime.Story.TextEffect!.Kind);
        if (kind == ScriptNodeKind.AutoDialogue) Assert.NotNull(runtime.Story.RemainingSeconds);
    }

    [Theory]
    [InlineData("{w}", false)]
    [InlineData("{w=0.5}", true)]
    public async Task TrailingPause_DoesNotIntroduceAnExtraContinuation(string pause, bool timed)
    {
        using var fixture = Scene("d(nil,'before" + pause + "');d(nil,'after')");
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("before", runtime.Story.Text);
        if (timed) await runtime.TickAsync(.5); else await runtime.AdvanceAsync();
        Assert.Equal("after", runtime.Story.Text);
    }

    [Fact]
    public async Task WorkerAndPreview_RenderMarkersAndScreenLabelsWithColorsWithoutChangingLayout()
    {
        using var fixture = Scene("state.name='Алиса';state.empty='';state.bad=1/0;show_screen('Инлайн');show_title('{name}');d('{name}','Привет, {color=blue}{name}{/color}! {absent} {empty} {bad}')");
        var appearance = await AppearanceDocument.OpenAsync(await fixture.LoadAsync());
        var screen = appearance.Layout.AddScreen("Инлайн"); screen.Elements.Clear();
        const string label = "{color=red}Экран {name}{w=10}{/color}";
        screen.Elements.Add(new() { Id = "caption", Type = "text", Text = label, X = 30, Y = 40 }); await appearance.SaveAsync();
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { }); await runtime.StartAsync();
        Assert.Equal("Экран Алиса", Plain(runtime.Visuals.ScreenTexts[label]));
        await WorkspaceTabsTests.Sta(() =>
        {
            var preview = new ScenePreview();
            try
            {
                preview.DisplayScene(project, runtime.WindowSettings, runtime.Visuals, runtime.Story, false, false);
                var text = WorkspaceTabsTests.Find<TextBlock>(preview, "DialogueText");
                Assert.Equal("Привет, Алиса! {missing} {null} {error}", text.Text);
                Assert.Contains(text.Inlines.OfType<Run>(), run => run.Text == "Алиса" && ((SolidColorBrush)run.Foreground).Color == Colors.Blue);
                var canvas = WorkspaceTabsTests.Find<Canvas>(preview, "ScriptScreenCanvas");
                var caption = Assert.IsType<TextBlock>(Assert.Single(canvas.Children.Cast<System.Windows.UIElement>()));
                Assert.Equal("Экран Алиса", caption.Text);
                Assert.Equal(Colors.Red, ((SolidColorBrush)Assert.Single(caption.Inlines.OfType<Run>()).Foreground).Color);
                string? directory = Environment.GetEnvironmentVariable("KNE_INLINE_NAV_RENDER");
                if (directory != null)
                {
                    var area = WorkspaceTabsTests.Find<Grid>(preview, "SceneArea");
                    area.Measure(new System.Windows.Size(1280, 720)); area.Arrange(new System.Windows.Rect(0, 0, 1280, 720)); area.UpdateLayout();
                    System.IO.Directory.CreateDirectory(directory);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32); bitmap.Render(area);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, "inline-preview.png")); encoder.Save(file);
                }
            }
            finally { preview.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ChoiceInputTitleAndConsole_ExpandStateAndColors_WithoutPausingOutsideDialogue()
    {
        using var fixture = Scene("""
            state.name='Алиса'
            show_title('{color=blue}Глава {name}{/color}')
            console_write('Консоль {name}')
            choice('Вопрос {name}{w}', {'{color=red}Ответ {name}{/color}'})
            text_input('{"variable":"answer","prompt":"Имя {name}{w=10}","placeholder":"{name}","validation_mode":"Exact","expected_value":"ok","failure_message":"Ошибка {name}"}')
            d(nil,'Конец {answer}')
            """);
        var output = new List<string>();
        await using var runtime = new LuaRuntime(await fixture.LoadAsync(), output.Add, interactive: true);
        await runtime.StartAsync(); Assert.Equal("choice", runtime.Story.Kind);
        Assert.Equal("Вопрос Алиса", Plain(runtime.Story.Text));
        Assert.Equal("Ответ Алиса", Plain(Assert.Single(runtime.Story.Options))); Assert.Null(runtime.Story.RemainingSeconds);
        Assert.Equal("Глава Алиса", Plain(runtime.Visuals.Title)); Assert.Contains("Консоль Алиса", output);
        await runtime.ChooseAsync(1); Assert.Equal("input", runtime.Story.Kind);
        Assert.Equal("Имя Алиса", Plain(runtime.Story.Text)); Assert.Equal("Алиса", runtime.Story.Input!.Placeholder);
        Assert.Null(runtime.Story.RemainingSeconds);
        await runtime.SubmitInputAsync("bad"); Assert.Equal("Ошибка Алиса", Plain(runtime.Story.InputFeedback));
        await runtime.SubmitInputAsync("ok"); Assert.Equal("Конец ok", Plain(runtime.Story.Text));
    }

    [Fact]
    public Task Presentation_ColorsAccessibleTextAndAnimatedGlyphs_AndClipsMarkupByVisibleCharacters() => WorkspaceTabsTests.Sta(() =>
    {
        var text = new TextBlock { FontSize = 24, Foreground = Brushes.White, Width = 600 };
        InlineTextPresentation.Apply(text, "А{color=red}🙂Б{/color}В");
        Assert.Equal("А🙂БВ", text.Text);
        Assert.Equal(Colors.Red, ((SolidColorBrush)text.Inlines.OfType<Run>().Single(run => run.Text == "🙂Б").Foreground).Color);
        var effect = new EffectTextPresenter();
        effect.Present(text, new TextEffectSettings { Kind = "wave" }, false);
        effect.Measure(new System.Windows.Size(600, 300)); effect.Arrange(new System.Windows.Rect(0, 0, 600, 300));
        var letters = effect.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(new[] { "А", "🙂", "Б", "В" }, letters.Select(letter => letter.Text));
        Assert.Equal(Colors.Red, ((SolidColorBrush)letters[1].Foreground).Color);
        Assert.Equal(Colors.Red, ((SolidColorBrush)letters[2].Foreground).Color);
        effect.Present(text, new TextEffectSettings { Kind = "rainbow" }, false);
        effect.Measure(new System.Windows.Size(600, 300)); effect.Arrange(new System.Windows.Rect(0, 0, 600, 300)); effect.RenderAt(TimeSpan.FromSeconds(.25));
        Assert.Equal(Colors.Red, ((SolidColorBrush)effect.Children.OfType<TextBlock>().ElementAt(1).Foreground).Color);
        InlineTextPresentation.Apply(text, "А{color=red}🙂Б{/color}В", 3); Assert.Equal("А🙂", text.Text);
        effect.Stop(); return Task.CompletedTask;
    });

    [Theory]
    [InlineData("hide")]
    [InlineData("scramble")]
    public Task LockedChoice_RevealedTextRetainsInlineColorsAcrossAnimationTicks(string effect) => WorkspaceTabsTests.Sta(() =>
    {
        var button = new ChoiceOptionButton(new("{color=red}Ответ{/color}", false, effect), 1);
        var label = Assert.IsType<TextBlock>(button.Content);
        foreach (double seconds in new[] { .5, .6 })
        {
            button.ApplyEffect(seconds, true); Assert.Equal("Ответ", label.Text);
            Assert.Equal(Colors.Red, ((SolidColorBrush)Assert.Single(label.Inlines.OfType<Run>()).Foreground).Color);
        }
        Assert.False(button.IsAvailable); return Task.CompletedTask;
    });

    private static string Plain(string text) => InlineText.Parse(text).Text;
    private static TempProject Scene(string body)
    {
        var fixture = new TempProject(); fixture.Write("game/scenes/main.krl", "function main()\n" + body + "\nend"); return fixture;
    }
}
