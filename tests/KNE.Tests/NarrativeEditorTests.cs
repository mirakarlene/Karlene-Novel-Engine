using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class NarrativeEditorTests
{
    [Fact]
    public void NarrativeCatalog_KeepsLegacyOrdinals_AndExposesSevenOrangeCommandsWithRunnableDefaults()
    {
        Assert.Equal(6, (int)ScriptNodeKind.Character);
        Assert.Equal(47, (int)ScriptNodeKind.ClearSprites);
        ScriptNodeKind[] kinds = [ScriptNodeKind.RegisterCharacter, ScriptNodeKind.TextPause, ScriptNodeKind.ClearText, ScriptNodeKind.AutoDialogue, ScriptNodeKind.ConsoleWrite, ScriptNodeKind.RandomBranch, ScriptNodeKind.EffectDialogue];
        foreach (var kind in kinds)
        {
            var entry = Assert.Single(BlockLibrary.Definitions, entry => entry.Kind == kind);
            Assert.Equal("narrative", entry.Group); Assert.Equal("#C65C16", entry.Color);
            var node = BlockLibrary.Create(kind); node.Id = "new"; node.Next = "end";
            var graph = VisualScript.Create(); graph.Nodes[0].Next = node.Id; graph.Nodes.RemoveAt(1); graph.Nodes.Add(node);
            if (kind == ScriptNodeKind.RandomBranch) { node.Next = null; foreach (var option in node.Options) option.Target = "end"; }
            string code = graph.Compile(); Assert.Contains("function main()", code);
            var reloaded = VisualScript.Parse(graph.Serialize()).Nodes.Single(item => item.Id == node.Id);
            Assert.Equal(kind, reloaded.Kind); Assert.Equal(node.Title, reloaded.Title);
        }
        Assert.Equal("Спрайт", new ScriptNode { Kind = ScriptNodeKind.Character }.Title);
        Assert.Equal("Персонаж", BlockLibrary.Create(ScriptNodeKind.RegisterCharacter).Title);
    }

    [Fact]
    public void Compiler_EmitsProfileDialogueTimingClearPrintAndEffects_WithoutConfusingManualNamesWithIds()
    {
        var profile = new CharacterProfile { Id = "anna", Name = "Анна", NameColor = "#FFEEDDCC", TextAlignment = "right", FontFamily = "Consolas", FontSize = 31 };
        var register = new ScriptNode { Id = "register", Kind = ScriptNodeKind.RegisterCharacter, Character = profile };
        var manual = new ScriptNode { Id = "manual", Kind = ScriptNodeKind.Dialogue, Speaker = "anna", Text = "Ручное имя" };
        var named = new ScriptNode { Id = "named", Kind = ScriptNodeKind.Dialogue, Speaker = "старое имя", SpeakerId = "anna", Text = "Профиль" };
        var auto = new ScriptNode { Id = "auto", Kind = ScriptNodeKind.AutoDialogue, SpeakerId = "anna", Text = "Авто", Duration = 1.25, Skippable = false };
        var pause = new ScriptNode { Id = "pause", Kind = ScriptNodeKind.TextPause, Duration = 2.5, Skippable = false };
        var clear = new ScriptNode { Id = "clear", Kind = ScriptNodeKind.ClearText, Animated = true, Duration = .75 };
        var log = new ScriptNode { Id = "log", Kind = ScriptNodeKind.ConsoleWrite, Text = "quote: \" \\ \n" };
        var effect = new ScriptNode { Id = "effect", Kind = ScriptNodeKind.EffectDialogue, Text = "Буквы", TextEffect = new() { Kind = "wave", Speed = 2.5, Loop = false, Color1 = "#FFAABBCC", Color2 = "#FF112233" } };
        var graph = Chain(register, manual, named, auto, pause, clear, log, effect); string code = graph.Compile();
        Assert.Contains("register_character(\"{", code); Assert.Contains("\\\"id\\\": \\\"anna\\\"", code);
        Assert.Contains("d(\"anna\", \"Ручное имя\")", code);
        Assert.Contains("kne_say(\"старое имя\", \"Профиль\", \"anna\", nil, nil, true)", code);
        Assert.Contains("kne_say(nil, \"Авто\", \"anna\", nil, 1.25, false)", code);
        Assert.Contains("text_pause(2.5, false)", code); Assert.Contains("clear_text(true, 0.75)", code);
        Assert.Contains("console_write(\"quote: \\\" \\\\ \\010\")", code);
        Assert.Contains("\\\"kind\\\": \\\"wave\\\"", code); Assert.Contains("\\\"loop\\\": false", code);
        var copy = VisualScript.Parse(graph.Serialize()); Assert.Equal(profile, copy.Nodes.Single(node => node.Id == "register").Character);
        Assert.Equal(effect.TextEffect, copy.Nodes.Single(node => node.Id == "effect").TextEffect);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task RandomBranch_AllowsBoundaryCounts_UsesWeightsAndOnlyExecutesSelectedDestination(int count)
    {
        using var fixture = new TempProject(); var random = new ScriptNode { Id = "random", Kind = ScriptNodeKind.RandomBranch };
        var graph = Chain(random); random.Next = null;
        for (int i = 0; i < count; i++)
        {
            random.Options.Add(new() { Text = "Branch " + i, Weight = i == count - 1 ? 60 : 0, Target = "line" + i });
            graph.Nodes.Add(new() { Id = "line" + i, Kind = ScriptNodeKind.Dialogue, Text = "line " + i, Next = "end" });
        }
        Assert.Equal(count, random.Outputs().Count()); Assert.DoesNotContain(random.Outputs(), output => output.Label == "Далее");
        Assert.Throws<ArgumentOutOfRangeException>(() => random.SetOutput(count, "end"));
        await using var runtime = new LuaRuntime(await VisualScriptTests.LoadGraphAsync(fixture, graph), _ => { }, interactive: true);
        await runtime.StartAsync(); Assert.Equal("line " + (count - 1), runtime.Story.Text);
        await runtime.AdvanceAsync(); Assert.True(runtime.Story.Completed);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(21, 1, 1)]
    [InlineData(2, 0, 0)]
    [InlineData(2, -1, 1)]
    [InlineData(2, double.NaN, 1)]
    [InlineData(2, double.PositiveInfinity, 1)]
    public void RandomBranch_RejectsInvalidCountsAndProbabilityWeights(int count, double first, double remaining)
    {
        var random = new ScriptNode { Id = "random", Kind = ScriptNodeKind.RandomBranch, Options = Enumerable.Range(0, count).Select(i => new ScriptOption { Text = "B" + i, Target = "end", Weight = i == 0 ? first : remaining }).ToList() };
        var graph = Chain(random); random.Next = null;
        Assert.Throws<InvalidDataException>(() => graph.Compile());
    }

    [Fact]
    public void RandomBranch_PreservesLegacyChoiceWeights_AndRejectsAnUnrepresentedContinuation()
    {
        var graph = VisualScript.Create(); graph.Nodes[1].Kind = ScriptNodeKind.Choice; graph.Nodes[1].Options = [new() { Text = "A", Target = "end" }];
        string legacy = graph.Serialize().Replace("\"Weight\": 1,", "");
        Assert.Equal(1, Assert.Single(VisualScript.Parse(legacy).Nodes[1].Options).Weight);
        var random = BlockLibrary.Create(ScriptNodeKind.RandomBranch); random.Id = "random"; foreach (var option in random.Options) option.Target = "end";
        var invalid = Chain(random); Assert.Contains("общего продолжения", Assert.Throws<InvalidDataException>(() => invalid.Compile()).Message);
    }

    [Fact]
    public async Task CharacterEditor_AppliesAllProfileFields_PersistsThemAndUndoesEditsAtomically()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/profiles.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.RegisterCharacter);
            string id = editor.Document!.Graph!.Nodes.Last().Id;
            Extended<TextBox>(editor, "CharacterIdInput").Text = "anna_2"; Extended<TextBox>(editor, "CharacterNameInput").Text = "Анна";
            Extended<TextBox>(editor, "CharacterNameColorInput").Text = "#FFAACCEE"; Extended<ComboBox>(editor, "CharacterAlignmentInput").SelectedItem = "center";
            Extended<TextBox>(editor, "CharacterFontInput").Text = "Consolas"; Extended<TextBox>(editor, "CharacterFontSizeInput").Text = "31.5";
            Click(editor, "ApplyPropertiesButton"); var expected = new CharacterProfile { Id = "anna_2", Name = "Анна", NameColor = "#FFAACCEE", TextAlignment = "center", FontFamily = "Consolas", FontSize = 31.5 };
            Assert.Equal(expected, editor.Document.Graph.Nodes.Single(node => node.Id == id).Character);
            Click(editor, "UndoButton"); Assert.Equal("character", editor.Document.Graph.Nodes.Single(node => node.Id == id).Character!.Id);
            Click(editor, "RedoButton"); Assert.Equal(expected, editor.Document.Graph.Nodes.Single(node => node.Id == id).Character);
            Extended<TextBox>(editor, "CharacterFontSizeInput").Text = "513"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(expected, editor.Document.Graph.Nodes.Single(node => node.Id == id).Character); Assert.Contains("512", Find<TextBlock>(editor, "EditorStatus").Text);
            Extended<TextBox>(editor, "CharacterFontSizeInput").Text = "31.5"; await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal(expected, editor.Document.Graph!.Nodes.Single(node => node.Id == id).Character);
            editor.AddBlock(ScriptNodeKind.RegisterCharacter); string second = editor.Document.Graph.Nodes.Last().Character!.Id;
            editor.AddBlock(ScriptNodeKind.RegisterCharacter); Assert.NotEqual(second, editor.Document.Graph.Nodes.Last().Character!.Id); editor.Clear();
        });
    }

    [Theory]
    [InlineData(ScriptNodeKind.Dialogue, "game/scenes/other-profile.knegraph")]
    [InlineData(ScriptNodeKind.AutoDialogue, "game/scenes/other-profile.knegraph")]
    [InlineData(ScriptNodeKind.EffectDialogue, "game/scenes/other-profile.knegraph")]
    [InlineData(ScriptNodeKind.Dialogue, "other-profile.knegraph")]
    public async Task DialogueEditor_ChoosesUnsavedCurrentAndProjectProfiles_CancelsWithoutChanges_AndSupportsManualNarrator(ScriptNodeKind kind, string profilePath)
    {
        using var fixture = new TempProject(); var graph = VisualScript.Create(); graph.Nodes[1].Kind = kind; graph.Nodes[1].Speaker = "Manual";
        graph.Nodes.Add(new() { Id = "profile", Kind = ScriptNodeKind.RegisterCharacter, Character = new() { Id = "anna", Name = "Old saved name" } });
        string path = fixture.Write("game/scenes/main-profile.knegraph", graph.Serialize());
        var external = Chain(new ScriptNode { Id = "external", Kind = ScriptNodeKind.RegisterCharacter, Character = new() { Id = "boris", Name = "Борис" } }); external.FunctionName = "other";
        fixture.Write(profilePath, external.Serialize());
        fixture.Write("game/scenes/broken.knegraph", "{ invalid JSON");
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); Select(editor, "profile");
            Extended<TextBox>(editor, "CharacterNameInput").Text = "Unsaved Anna"; Click(editor, "ApplyPropertiesButton"); Select(editor, "dialogue");
            Assert.Equal(Visibility.Visible, Find<StackPanel>(editor, "SpeakerField").Visibility);
            Click(editor, "SelectCharacterButton"); await Until(() => Find<Button>(editor, "ChooseCharacterButton").IsEnabled);
            Assert.Equal(Visibility.Visible, Find<Border>(editor, "CharacterPickerOverlay").Visibility);
            var list = Find<ListBox>(editor, "CharacterProfiles"); Assert.Equal(2, list.Items.Count);
            string[] labels = list.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!).ToArray();
            Assert.Contains(labels, label => label.Contains("Unsaved Anna (anna)")); Assert.DoesNotContain(labels, label => label.Contains("Old saved name")); Assert.Contains(labels, label => label.Contains("Борис (boris)"));
            Click(editor, "CancelCharacterButton"); Assert.Equal("Manual", editor.Document!.Graph!.Nodes.Single(node => node.Id == "dialogue").Speaker);
            Assert.Equal("", editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue").SpeakerId);
            Click(editor, "SelectCharacterButton"); await Until(() => Find<Button>(editor, "ChooseCharacterButton").IsEnabled);
            list.SelectedItem = list.Items.Cast<object>().Single(item => ((string)item.GetType().GetProperty("Label")!.GetValue(item)!).Contains("Unsaved Anna"));
            Click(editor, "ChooseCharacterButton"); var dialogue = editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue");
            Assert.Equal("anna", dialogue.SpeakerId); Assert.Equal("Unsaved Anna", dialogue.Speaker); Assert.Contains("anna", Find<TextBlock>(editor, "SpeakerReference").Text);
            Find<TextBox>(editor, "SpeakerInput").Text = "Custom name"; Click(editor, "ApplyPropertiesButton"); dialogue = editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue");
            Assert.Equal("", dialogue.SpeakerId); Assert.Equal("Custom name", dialogue.Speaker);
            Click(editor, "NarratorButton"); dialogue = editor.Document.Graph.Nodes.Single(node => node.Id == "dialogue");
            Assert.Equal("", dialogue.SpeakerId); Assert.Equal("", dialogue.Speaker);
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path); Assert.Equal("", editor.Document.Graph!.Nodes.Single(node => node.Id == "dialogue").Speaker); editor.Clear();
        });
    }

    [Fact]
    public async Task CharacterPicker_EmptyProjectExplainsRegistration_AndClearClosesOverlay()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/empty.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); Select(editor, "dialogue"); Click(editor, "SelectCharacterButton");
            await Until(() => !Find<TextBlock>(editor, "CharacterPickerStatus").Text.StartsWith("Поиск"));
            Assert.Empty(Find<ListBox>(editor, "CharacterProfiles").Items); Assert.False(Find<Button>(editor, "ChooseCharacterButton").IsEnabled);
            Assert.Contains("Добавьте блок", Find<TextBlock>(editor, "CharacterPickerStatus").Text);
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            editor.RaiseEvent(escape); Assert.True(escape.Handled); Assert.Equal(Visibility.Collapsed, Find<Border>(editor, "CharacterPickerOverlay").Visibility);
            Click(editor, "SelectCharacterButton"); editor.Clear(); Assert.Equal(Visibility.Collapsed, Find<Border>(editor, "CharacterPickerOverlay").Visibility);
        });
    }

    [Fact]
    public async Task EffectEditor_OffersAllSixteenEffects_AndKeepsEachSpeedAndLoopSettingOnReload()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/effects.knegraph", VisualScript.Create().Serialize());
        string[] expected = ["shake", "wave", "sway", "bounce", "float", "explode", "pulse", "spin", "squash", "glitch", "blur", "dissolve", "scramble", "rainbow", "color_cycle", "drip"];
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path); editor.AddBlock(ScriptNodeKind.EffectDialogue);
            Assert.Equal(16, Extended<ComboBox>(editor, "TextEffectKindInput").Items.Count);
            for (int index = 0; index < expected.Length; index++)
            {
                Extended<ComboBox>(editor, "TextEffectKindInput").SelectedIndex = index;
                Extended<TextBox>(editor, "TextEffectSpeedInput").Text = (index + .5).ToString(CultureInfo.InvariantCulture);
                Extended<CheckBox>(editor, "TextEffectLoopInput").IsChecked = index % 2 == 0;
                Click(editor, "ApplyPropertiesButton"); await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
                var effect = editor.Document!.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.EffectDialogue);
                Assert.Equal(expected[index], effect.TextEffect!.Kind); Assert.Equal(index + .5, effect.TextEffect.Speed); Assert.Equal(index % 2 == 0, effect.TextEffect.Loop);
                Select(editor, effect.Id); Assert.Equal(index, Extended<ComboBox>(editor, "TextEffectKindInput").SelectedIndex);
            }
            editor.Clear();
        });
    }

    [Fact]
    public async Task NarrativePropertyEditor_PersistsTimingsFlagsTextEffectsAndWeightedPorts_AndRejectsInvalidDrafts()
    {
        using var fixture = new TempProject(); string path = fixture.Write("game/scenes/commands.knegraph", VisualScript.Create().Serialize());
        await Sta(async () =>
        {
            var editor = new ScriptEditor(); await editor.OpenAsync(fixture.ProjectPath, path);
            foreach (var kind in new[] { ScriptNodeKind.TextPause, ScriptNodeKind.AutoDialogue, ScriptNodeKind.ClearText })
            {
                editor.AddBlock(kind); Extended<TextBox>(editor, "NarrativeDurationInput").Text = "1.75";
                if (kind == ScriptNodeKind.ClearText) Extended<CheckBox>(editor, "AnimatedClearInput").IsChecked = true; else Extended<CheckBox>(editor, "SkippableInput").IsChecked = false;
                Click(editor, "ApplyPropertiesButton"); var node = editor.Document!.Graph!.Nodes.Last(); Assert.Equal(1.75, node.Duration);
                if (kind == ScriptNodeKind.ClearText) Assert.True(node.Animated); else Assert.False(node.Skippable);
            }
            editor.AddBlock(ScriptNodeKind.EffectDialogue); Find<TextBox>(editor, "SpeakerInput").Text = "Анна"; Find<TextBox>(editor, "SceneTextInput").Text = "Эффект";
            Extended<ComboBox>(editor, "TextEffectKindInput").SelectedIndex = TextEffectSettings.Kinds.ToList().IndexOf("color_cycle");
            Extended<TextBox>(editor, "TextEffectSpeedInput").Text = "2.25"; Extended<CheckBox>(editor, "TextEffectLoopInput").IsChecked = false;
            Extended<TextBox>(editor, "TextEffectColor1Input").Text = "#FFFF0000"; Extended<TextBox>(editor, "TextEffectColor2Input").Text = "#FF000000"; Click(editor, "ApplyPropertiesButton");
            var effect = editor.Document!.Graph!.Nodes.Last(); Assert.Equal("Анна", effect.Speaker); Assert.Equal("Эффект", effect.Text);
            var settings = new TextEffectSettings { Kind = "color_cycle", Speed = 2.25, Loop = false, Color1 = "#FFFF0000", Color2 = "#FF000000" }; Assert.Equal(settings, effect.TextEffect);
            Extended<TextBox>(editor, "TextEffectSpeedInput").Text = "0"; Click(editor, "ApplyPropertiesButton"); Assert.Equal(settings, editor.Document.Graph.Nodes.Last().TextEffect);
            Assert.Contains("Скорость", Find<TextBlock>(editor, "EditorStatus").Text); Extended<TextBox>(editor, "TextEffectSpeedInput").Text = "2.25";
            editor.AddBlock(ScriptNodeKind.RandomBranch); var random = editor.Document.Graph.Nodes.Last(); random.Options[0].Target = "dialogue"; random.Options[1].Target = "end";
            Find<TextBox>(editor, "OptionsInput").Text = "Left\nRight"; Extended<TextBox>(editor, "RandomWeightsInput").Text = "3\n2"; Click(editor, "ApplyPropertiesButton");
            random = editor.Document.Graph.Nodes.Last(); Assert.Equal(new[] { "Left", "Right" }, random.Options.Select(option => option.Text)); Assert.Equal(new[] { 3d, 2d }, random.Options.Select(option => option.Weight)); Assert.Equal(new[] { "dialogue", "end" }, random.Options.Select(option => option.Target));
            Find<TextBox>(editor, "OptionsInput").Text = "Left\nRight\nMiddle"; Extended<TextBox>(editor, "RandomWeightsInput").Text = "6\n3\n1"; Click(editor, "ApplyPropertiesButton");
            random = editor.Document.Graph.Nodes.Last(); Assert.Equal(3, random.Options.Count); Assert.Equal(new string?[] { "dialogue", "end", null }, random.Options.Select(option => option.Target)); Assert.Equal(new[] { 6d, 3d, 1d }, random.Options.Select(option => option.Weight));
            Find<TextBox>(editor, "OptionsInput").Text = "Left\nRight"; Extended<TextBox>(editor, "RandomWeightsInput").Text = "3\n2"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal(new[] { "dialogue", "end" }, editor.Document.Graph.Nodes.Last().Options.Select(option => option.Target));
            Extended<TextBox>(editor, "RandomWeightsInput").Text = "1"; Click(editor, "ApplyPropertiesButton"); Assert.Equal(new[] { 3d, 2d }, editor.Document.Graph.Nodes.Last().Options.Select(option => option.Weight));
            Assert.Contains("Число весов", Find<TextBlock>(editor, "EditorStatus").Text); Extended<TextBox>(editor, "RandomWeightsInput").Text = "3\n2";
            editor.AddBlock(ScriptNodeKind.ConsoleWrite); Find<TextBox>(editor, "SceneTextInput").Text = "Консоль\nстрока 2"; Click(editor, "ApplyPropertiesButton");
            Assert.Equal("Консоль\nстрока 2", editor.Document.Graph.Nodes.Last().Text); Assert.Equal(Visibility.Collapsed, Find<StackPanel>(editor, "SpeakerField").Visibility);
            await editor.SaveAsync(); await editor.OpenAsync(fixture.ProjectPath, path);
            Assert.Equal(settings, editor.Document.Graph!.Nodes.Single(node => node.Kind == ScriptNodeKind.EffectDialogue).TextEffect);
            Assert.Equal(new[] { 3d, 2d }, editor.Document.Graph.Nodes.Single(node => node.Kind == ScriptNodeKind.RandomBranch).Options.Select(option => option.Weight)); editor.Clear();
        });
    }

    private static VisualScript Chain(params ScriptNode[] nodes)
    {
        var graph = new VisualScript { Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, Next = nodes.FirstOrDefault()?.Id ?? "end" }, .. nodes, new() { Id = "end", Kind = ScriptNodeKind.End }] };
        for (int i = 0; i < nodes.Length; i++) nodes[i].Next = i + 1 < nodes.Length ? nodes[i + 1].Id : "end";
        return graph;
    }
    private static T Find<T>(FrameworkElement owner, string name) => (T)owner.FindName(name);
    private static T Extended<T>(ScriptEditor editor, string name) where T : FrameworkElement => Find<StackPanel>(editor, "ExtendedProperties").Children.OfType<T>().Single(control => control.Name == name);
    private static void Select(ScriptEditor editor, string id) => typeof(ScriptEditor).GetMethod("SelectNode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, [id, true]);
    private static void Click(FrameworkElement owner, string name) => Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10, timeout.Token); }
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
    private static Task Sta(Func<Task> action)
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
