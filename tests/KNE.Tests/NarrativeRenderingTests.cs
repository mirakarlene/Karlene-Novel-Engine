using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Xunit;
using Path = System.IO.Path;

namespace KNE.Tests;

public sealed class NarrativeRenderingTests
{
    public static IEnumerable<object[]> Effects => TextEffectSettings.Kinds.Select(kind => new object[] { kind });

    [Fact]
    public Task Effect_DisabledScrambleShowsReadableText_AndReenablingRestoresTheEffect() => Sta(() =>
    {
        var text = new TextBlock { Text = "Алиса 👩‍👩‍👧‍👦", FontSize = 24, Foreground = Brushes.White };
        var effect = new TextEffectSettings { Kind = "scramble" }; var presenter = new EffectTextPresenter();
        presenter.Present(text, effect, false); Arrange(presenter, 400, 100);
        Assert.Equal(text.Text, string.Concat(presenter.Children.OfType<TextBlock>().Select(letter => letter.Text)));
        presenter.Present(text, effect, true); presenter.RenderAt(TimeSpan.FromSeconds(.1));
        Assert.NotEqual(text.Text, string.Concat(presenter.Children.OfType<TextBlock>().Select(letter => letter.Text)));
        presenter.Present(text, effect, false);
        Assert.Equal(text.Text, string.Concat(presenter.Children.OfType<TextBlock>().Select(letter => letter.Text)));
        Assert.False(presenter.IsAnimating); return Task.CompletedTask;
    });

    [Theory]
    [MemberData(nameof(Effects))]
    public Task Effect_EveryKindChangesActualGlyphVisuals_AndSingleCycleSettles(string kind) => Sta(() =>
    {
        var plain = new TextBlock { Text = "Abc def", FontFamily = new FontFamily("Consolas"), FontSize = 24, Foreground = Brushes.White };
        var presenter = new EffectTextPresenter();
        presenter.Present(plain, new() { Kind = kind, Loop = false, Color1 = "#FF0000", Color2 = "#000000" }, false);
        Arrange(presenter, 250, 120); presenter.RenderAt(TimeSpan.FromSeconds(.237));
        var glyphs = presenter.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(7, presenter.GlyphCount); Assert.Equal("Abc def", plain.Text); Assert.Equal(0, plain.Opacity);
        switch (kind)
        {
            case "shake": case "wave": case "bounce": case "float": case "explode":
                Assert.Contains(glyphs, glyph => !glyph.RenderTransform.Value.IsIdentity); break;
            case "sway": case "spin": case "pulse": case "squash":
                Assert.Contains(glyphs, glyph => !glyph.RenderTransform.Value.IsIdentity); break;
            case "glitch": Assert.All(glyphs, glyph => Assert.IsType<DropShadowEffect>(glyph.Effect)); break;
            case "blur": Assert.All(glyphs, glyph => Assert.True(Assert.IsType<BlurEffect>(glyph.Effect).Radius > 1)); break;
            case "dissolve": Assert.All(glyphs, glyph => Assert.Equal(.763, glyph.Opacity, 6)); break;
            case "scramble": Assert.NotEqual("Abc def", string.Concat(glyphs.Select(glyph => glyph.Text))); break;
            case "rainbow": Assert.True(glyphs.Select(glyph => Assert.IsType<SolidColorBrush>(glyph.Foreground).Color).Distinct().Count() > 1); break;
            case "color_cycle":
                presenter.RenderAt(TimeSpan.FromSeconds(.125));
                var color = Assert.IsType<SolidColorBrush>(glyphs[0].Foreground).Color;
                Assert.InRange(color.R, (byte)1, (byte)254); Assert.Equal(0, color.G); Assert.Equal(0, color.B); break;
            case "drip":
                var drops = presenter.Children.OfType<Ellipse>().Where(drop => drop.Visibility == Visibility.Visible).ToArray();
                Assert.Equal(6, drops.Length); Assert.All(drops, drop => Assert.Same(plain.Foreground, drop.Fill));
                Assert.Contains(drops, drop => Canvas.GetTop(drop) > 24); break;
            default: throw new InvalidOperationException("Missing effect assertion: " + kind);
        }
        presenter.RenderAt(TimeSpan.FromSeconds(1.001));
        Assert.Equal("Abc def", string.Concat(glyphs.Select(glyph => glyph.Text)));
        Assert.All(glyphs, glyph =>
        {
            Assert.True(glyph.RenderTransform.Value.IsIdentity); Assert.Null(glyph.Effect);
            Assert.Same(plain.Foreground, glyph.Foreground); Assert.Equal(kind == "dissolve" ? 0 : 1, glyph.Opacity);
        });
        Assert.All(presenter.Children.OfType<Ellipse>(), drop => Assert.Equal(Visibility.Collapsed, drop.Visibility));
        Assert.False(presenter.IsAnimating);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("wave")]
    [InlineData("scramble")]
    [InlineData("dissolve")]
    [InlineData("rainbow")]
    public Task Effect_SpeedUsesSameLogicalPhase_AndLoopRepeatsExactly(string kind) => Sta(() =>
    {
        var plain = new TextBlock { Text = "Speed", FontSize = 24, Foreground = Brushes.White };
        var first = new EffectTextPresenter(); first.Present(plain, new() { Kind = kind, Speed = 1 }, false); Arrange(first, 300, 80);
        var second = new EffectTextPresenter(); second.Present(plain, new() { Kind = kind, Speed = 2 }, false); Arrange(second, 300, 80);
        first.RenderAt(TimeSpan.FromSeconds(.25)); second.RenderAt(TimeSpan.FromSeconds(.125));
        string before = VisualState(first); Assert.Equal(before, VisualState(second));
        first.RenderAt(TimeSpan.FromSeconds(1.25)); Assert.Equal(before, VisualState(first));
        second.Present(plain, null, false); Assert.Equal(1, plain.Opacity); Assert.Equal(Visibility.Collapsed, second.Visibility);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Effect_UnicodeGraphemesWrapAndAlign_WithoutSplittingEmojiOrCombiningMarks() => Sta(() =>
    {
        const string content = "A👩‍👩‍👧‍👦e\u0301\nБ 😃";
        Assert.Equal(new[] { "A", "👩‍👩‍👧‍👦", "e\u0301", "\n", "Б", " ", "😃" }, EffectTextPresenter.TextElements(content));
        var plain = new TextBlock { Text = content, FontSize = 24, FontFamily = new FontFamily("Segoe UI"), TextAlignment = TextAlignment.Right, Foreground = Brushes.White };
        var presenter = new EffectTextPresenter(); presenter.Present(plain, new() { Kind = "wave" }, false); Arrange(presenter, 80, 200);
        var glyphs = presenter.Children.OfType<TextBlock>().ToArray();
        Assert.Equal(6, glyphs.Length); Assert.Contains(glyphs, glyph => glyph.Text == "👩‍👩‍👧‍👦"); Assert.Contains(glyphs, glyph => glyph.Text == "e\u0301");
        Assert.True(Canvas.GetTop(glyphs[^1]) > Canvas.GetTop(glyphs[0]));
        Assert.All(glyphs, glyph => Assert.InRange(Canvas.GetLeft(glyph), 0, 80));
        Assert.Equal(content, plain.Text); Assert.Equal(content, presenter.PlainText);
        var stable = glyphs[0]; presenter.Present(plain, new() { Kind = "wave" }, false); Arrange(presenter, 80, 200);
        Assert.Same(stable, presenter.Children.OfType<TextBlock>().First());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Effect_ClockStopsWhenHiddenAndUnloaded_AndFrozenSnapshotHasNoClock() => Sta(async () =>
    {
        var plain = new TextBlock { Text = "Clock", FontSize = 24, Foreground = Brushes.White };
        var effects = new EffectTextPresenter(); var grid = new Grid(); grid.Children.Add(plain); grid.Children.Add(effects);
        var window = new Window { Content = grid, Width = 300, Height = 150, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        try
        {
            effects.Present(plain, new() { Kind = "wave" }, false); window.Show(); await Task.Delay(50);
            Assert.False(effects.IsAnimating);
            effects.Present(plain, new() { Kind = "wave" }, true); Assert.True(effects.IsAnimating);
            window.Visibility = Visibility.Hidden; Assert.False(effects.IsAnimating);
            window.Visibility = Visibility.Visible; Assert.True(effects.IsAnimating);
            effects.Present(plain, new() { Kind = "wave", Loop = false, Speed = 100 }, true);
            await Task.Delay(80); Assert.False(effects.IsAnimating);
            effects.Present(plain, new() { Kind = "wave" }, true); Assert.True(effects.IsAnimating);
        }
        finally { window.Close(); }
        Assert.False(effects.IsAnimating);
    });

    [Fact]
    public Task Effect_LongTextKeepsAllAccessibleText_VirtualizesViewportAndFallsBackBeyondCapacity() => Sta(() =>
    {
        string content = string.Join('\n', Enumerable.Range(0, 1000).Select(index => "Line " + index + " ABCDE"));
        var plain = new TextBlock { Text = content, FontSize = 24, Foreground = Brushes.White };
        var presenter = new EffectTextPresenter(); presenter.Present(plain, new() { Kind = "wave" }, false); Arrange(presenter, 200, 100);
        Assert.InRange(presenter.GlyphCount, 1, 150); Assert.Equal(content, plain.Text); Assert.False(presenter.IsEffectSuppressedForCapacity);
        var first = presenter.Children.OfType<TextBlock>().First();
        for (int tick = 0; tick < 5; tick++) { presenter.Present(plain, new() { Kind = "wave" }, false); Arrange(presenter, 200, 100); }
        Assert.Same(first, presenter.Children.OfType<TextBlock>().First());
        string dense = new('W', EffectTextPresenter.MaximumAnimatedGlyphs + 1);
        plain.Text = dense; plain.FontSize = 1; presenter.Present(plain, new() { Kind = "wave" }, true); Arrange(presenter, 20000, 100);
        Assert.True(presenter.IsEffectSuppressedForCapacity); Assert.Equal(0, presenter.GlyphCount);
        Assert.Equal(dense, plain.Text); Assert.Equal(dense, presenter.PlainText); Assert.Equal(1, plain.Opacity); Assert.False(presenter.IsAnimating);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Effect_AllSixteenKindsRenderRealPixels_InSharedDeterministicMontage() => Sta(() =>
    {
        var montage = new Grid { Background = new SolidColorBrush(Color.FromRgb(32, 17, 52)) };
        for (int i = 0; i < 4; i++) { montage.RowDefinitions.Add(new RowDefinition()); montage.ColumnDefinitions.Add(new ColumnDefinition()); }
        var presenters = new List<EffectTextPresenter>();
        for (int i = 0; i < TextEffectSettings.Kinds.Count; i++)
        {
            string kind = TextEffectSettings.Kinds[i];
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = kind, FontSize = 15, Foreground = Brushes.Plum, Margin = new Thickness(0, 0, 0, 20) });
            var area = new Grid(); var plain = new TextBlock { Text = "Алиса: A😀é\nHello, world!", FontSize = 26, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
            var effects = new EffectTextPresenter(); effects.Present(plain, new() { Kind = kind }, false);
            area.Children.Add(plain); area.Children.Add(effects); panel.Children.Add(area); presenters.Add(effects);
            Grid.SetRow(panel, i / 4); Grid.SetColumn(panel, i % 4); montage.Children.Add(panel);
        }
        Arrange(montage, 1400, 800);
        foreach (var presenter in presenters) { presenter.RenderAt(TimeSpan.FromSeconds(.25)); Assert.True(presenter.GlyphCount > 10); }
        var bitmap = Capture(montage, "effect-montage.png", 1400, 800);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        Assert.True(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 2] > 100) > 5000);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Effect_MotionKindsHaveDistinctTranslationRotationAndScaleContracts() => Sta(() =>
    {
        var states = new List<string>();
        foreach (string kind in new[] { "shake", "wave", "sway", "bounce", "float", "explode", "pulse", "spin", "squash" })
        {
            var text = new TextBlock { Text = "Distinct", FontSize = 24, Foreground = Brushes.White };
            var effects = new EffectTextPresenter(); effects.Present(text, new() { Kind = kind }, false); Arrange(effects, 300, 80);
            effects.RenderAt(TimeSpan.FromSeconds(.237));
            var glyphs = effects.Children.OfType<TextBlock>().ToArray();
            var groups = glyphs.Select(glyph => Assert.IsType<TransformGroup>(glyph.RenderTransform)).ToArray();
            var translations = groups.Select(group => group.Children.OfType<TranslateTransform>().Single()).ToArray();
            var rotations = groups.Select(group => group.Children.OfType<RotateTransform>().Single()).ToArray();
            var scales = groups.Select(group => group.Children.OfType<ScaleTransform>().Single()).ToArray();
            if (kind is "sway" or "spin")
            {
                Assert.Contains(rotations, rotation => rotation.Angle != 0);
                Assert.All(translations, translation => Assert.True(translation.Value.IsIdentity));
                Assert.All(scales, scale => Assert.True(scale.Value.IsIdentity));
                if (kind == "sway") Assert.All(rotations, rotation => Assert.InRange(rotation.Angle, -12, 12));
                else Assert.All(rotations, rotation => Assert.Equal(.237 * 360, rotation.Angle, 5));
            }
            else if (kind is "pulse" or "squash")
            {
                Assert.All(rotations, rotation => Assert.Equal(0, rotation.Angle));
                Assert.All(translations, translation => Assert.True(translation.Value.IsIdentity));
                Assert.All(scales, scale =>
                {
                    if (kind == "pulse") { Assert.Equal(scale.ScaleX, scale.ScaleY); Assert.True(scale.ScaleX > 1); }
                    else { Assert.Equal(2, scale.ScaleX + scale.ScaleY, 5); Assert.NotEqual(scale.ScaleX, scale.ScaleY); }
                });
            }
            else
            {
                Assert.All(rotations, rotation => Assert.Equal(0, rotation.Angle));
                Assert.All(scales, scale => Assert.True(scale.Value.IsIdentity));
                if (kind is "wave" or "bounce") Assert.All(translations, translation => Assert.Equal(0, translation.X));
                else Assert.Contains(translations, translation => translation.X != 0);
                if (kind == "bounce") Assert.All(translations, translation => Assert.True(translation.Y <= 0));
                if (kind == "wave") { Assert.Contains(translations, translation => translation.Y > 0); Assert.Contains(translations, translation => translation.Y < 0); }
            }
            states.Add(VisualState(effects));
        }
        Assert.Equal(9, states.Distinct().Count());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Effect_FrameIdentityRestartsSameTextInNewBlock_WhileTransportCopiesAndErasingRetainPhase() => Sta(() =>
    {
        var plain = new TextBlock { Text = "Repeated", FontSize = 24, Foreground = Brushes.White };
        var effects = new EffectTextPresenter(); var effect = new TextEffectSettings { Kind = "wave" };
        var identity = new FrameIdentity("main", 1, 1, "first");
        effects.Present(plain, effect, true, identity); Arrange(effects, 300, 100);
        effects.RenderAt(TimeSpan.FromSeconds(.25)); string phase = VisualState(effects);
        var first = effects.Children.OfType<TextBlock>().First();
        effects.Present(plain, effect with { }, true, identity with { });
        Assert.Equal(phase, VisualState(effects)); Assert.Same(first, effects.Children.OfType<TextBlock>().First());
        effects.Present(plain, effect, true, identity with { SceneFrameId = 2, GlobalFrameId = 2, BlockId = "second" });
        Assert.NotEqual(phase, VisualState(effects)); Assert.Same(first, effects.Children.OfType<TextBlock>().First());
        effects.RenderAt(TimeSpan.FromSeconds(.25));
        plain.Text = "Repe";
        effects.Present(plain, effect, true, new FrameIdentity("main", 2, 2, "second"), preserveTextPhase: true); Arrange(effects, 300, 100);
        var translation = Assert.IsType<TransformGroup>(effects.Children.OfType<TextBlock>().First().RenderTransform).Children.OfType<TranslateTransform>().Single();
        Assert.Equal(7, translation.Y, 5);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task Narrative_PresentationSequenceReplaysSameBlockOneShot_AndResetRestartsIdenticalFirstFrame()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var preview = new ScenePreview();
            var story = new StoryFrame("dialogue", "", "Repeated", [])
            {
                Identity = new("main", 1, 1, "same_block"), PresentationId = 1,
                TextEffect = new() { Kind = "dissolve", Loop = false }
            };
            try
            {
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, story, true, false);
                Arrange(preview, 1280, 720); var effects = Find<EffectTextPresenter>(preview, "DialogueEffects");
                effects.RenderAt(TimeSpan.FromSeconds(.4)); Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(.6, glyph.Opacity, 5));
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, story with { Identity = story.Identity with { } }, true, false);
                Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(.6, glyph.Opacity, 5));
                story = story with { PresentationId = 2 };
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, story, true, false);
                Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(1, glyph.Opacity));
                effects.RenderAt(TimeSpan.FromSeconds(.4));
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty,
                    story with { Kind = "text_pause", Identity = new("main", 2, 2, "pause"), RemainingSeconds = 2 }, true, false);
                Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(.6, glyph.Opacity, 5));
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty,
                    story with { Kind = "clear_text", Identity = new("main", 3, 3, "erase"), EraseProgress = .5 }, true, false);
                Arrange(preview, 1280, 720); Assert.Equal(4, effects.GlyphCount);
                Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(.6, glyph.Opacity, 5));
                effects.RenderAt(TimeSpan.FromSeconds(1.1)); Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(0, glyph.Opacity));
                preview.ResetPresentation(); Assert.Equal(1, Find<TextBlock>(preview, "DialogueText").Opacity);
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, story with { PresentationId = 1 }, true, false);
                Arrange(preview, 1280, 720); Assert.Equal(8, effects.GlyphCount);
                Assert.All(effects.Children.OfType<TextBlock>(), glyph => Assert.Equal(1, glyph.Opacity));
            }
            finally { preview.Stop(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task Effect_ScrollViewportMaterializesEndLetters_AndDetachesOnUnload() => Sta(async () =>
    {
        string content = string.Join('\n', Enumerable.Range(0, 300).Select(index => "Line " + index));
        var plain = new TextBlock { Text = content, FontSize = 24, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
        var effects = new EffectTextPresenter(); var grid = new Grid(); grid.Children.Add(plain); grid.Children.Add(effects);
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var window = new Window { Content = scroll, Width = 300, Height = 180, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, WindowStyle = WindowStyle.None };
        try
        {
            effects.Present(plain, new() { Kind = "wave" }, true); window.Show(); await Task.Delay(50);
            Assert.InRange(effects.GlyphCount, 1, 150); Assert.True(effects.IsAnimating);
            Assert.Equal(0, Canvas.GetTop(effects.Children.OfType<TextBlock>().First()));
            scroll.ScrollToEnd(); await Until(() => scroll.VerticalOffset > 100);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.InRange(effects.GlyphCount, 1, 150);
            Assert.True(Canvas.GetTop(effects.Children.OfType<TextBlock>().First()) > 100);
            Assert.EndsWith("Line 299", string.Concat(effects.Children.OfType<TextBlock>().Select(glyph => glyph.Text)));
            Assert.Equal(content, plain.Text); Assert.Equal(content, effects.PlainText);
        }
        finally { window.Close(); }
        Assert.False(effects.IsAnimating);
    });

    [Fact]
    public async Task Scene_TimedBackRestoresWholeFrameBeforeCountdown_AndNeverAcceptsUnskippableAdvance()
    {
        using var fixture = new TempProject();
        const string source = "function main() kne_say(nil,'auto',nil,nil,2,false); text_pause(3,true); d(nil,'last') end";
        fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        await using var scene = new SceneTestSession(project, new(project.MainScenePath, source, 1, null, false, "main", false));
        await scene.RestartAsync(); await scene.TickAsync(.5); Assert.False(scene.CanGoBack);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scene.AdvanceAsync());
        await scene.TickAsync(1.5); Assert.Equal("text_pause", scene.Story.Kind); await scene.TickAsync(.5);
        await scene.BackAsync(); Assert.Equal("auto", scene.Story.Text); Assert.Equal(2, scene.Story.RemainingSeconds); Assert.False(scene.CanGoBack);
        await scene.TickAsync(2); await scene.TickAsync(.5); await scene.AdvanceAsync(); Assert.Equal("last", scene.Story.Text);
        await scene.BackAsync(); Assert.Equal("text_pause", scene.Story.Kind); Assert.Equal(3, scene.Story.RemainingSeconds);
        await scene.BackAsync(); Assert.Equal("auto", scene.Story.Text); Assert.Equal(2, scene.Story.RemainingSeconds);
        await scene.TickAsync(2); await scene.TickAsync(3); await scene.AdvanceAsync(); Assert.True(scene.Completed);
        await scene.BackAsync(); Assert.Equal("last", scene.Story.Text);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(project.Path, "saves"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SceneWindow_TextPauseEnablesOnlySkippableContinue_WhileTimedClockReachesNextDialogue()
    {
        using var fixture = new TempProject();
        const string source = "function main() d(nil,'first'); text_pause(10,true); text_pause(20,false); d(nil,'last') end";
        fixture.Write("game/scenes/main.krl", source); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var window = new SceneTestingWindow(project, new(project.MainScenePath, source, 1, null, false, "main", false), true);
            try
            {
                await window.InitializeAsync(); Field<DispatcherTimer>(window, "sceneClock").Stop();
                var scene = Field<SceneTestSession>(window, "scene"); await scene.AdvanceAsync(); Invoke(window, "Render");
                Assert.Equal("text_pause", scene.Story.Kind); Assert.True(Find<Button>(window, "NextFrameButton").IsEnabled);
                Invoke(window, "Next_Click", window, new RoutedEventArgs());
                await Until(() => scene.Story.RemainingSeconds == 20);
                Assert.False(Find<Button>(window, "NextFrameButton").IsEnabled);
                Invoke(window, "Next_Click", window, new RoutedEventArgs()); await Task.Delay(30);
                Assert.Equal("text_pause", scene.Story.Kind); Assert.Equal(20, scene.Story.RemainingSeconds);
                await scene.TickAsync(20); Invoke(window, "Render");
                Assert.Equal("last", Find<TextBlock>(Find<ScenePreview>(window, "SceneSurface"), "DialogueText").Text);
                Assert.True(Find<Button>(window, "NextFrameButton").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Narrative_GameAndPreviewUseProfileAndRestoreConfiguredNarrator_AndEraseWholeGraphemes()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(() =>
        {
            var window = new GameWindow(project); var preview = new ScenePreview();
            var style = GameplayUiStyle.ForTarget("dialogue") with { FontFamily = "Consolas", FontSize = 29, TextAlignment = "center", TextColor = "#FF55CCAA" };
            var visuals = SceneVisuals.Empty with { UiConfigurations = [new("dialogue", style)] };
            var person = new CharacterProfile { Id = "alice", Name = "Алиса", NameColor = "#FFCC3344", FontFamily = "Georgia", FontSize = 35, TextAlignment = "right" };
            var frame = new StoryFrame("dialogue", person.Name, "A👩‍👩‍👧‍👦e\u0301Б", []) { Character = person, TextEffect = new() { Kind = "wave" } };
            try
            {
                Invoke(window, "ShowGameplay");
                foreach (var story in new[] { frame, frame with { Kind = "clear_text", EraseProgress = .5 }, new StoryFrame("dialogue", "", "narrator", []) })
                {
                    Invoke(window, "ApplyGameplayUi", visuals);
                    typeof(GameWindow).Assembly.GetType("KNE.NarrativePresentation")!.GetMethod("Apply")!.Invoke(null,
                        [Find<TextBlock>(window, "SpeakerText"), Find<TextBlock>(window, "DialogueText"), Find<EffectTextPresenter>(window, "DialogueEffects"), story, project.Engine.Theme, visuals, false]);
                    preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), visuals, story, false, false);
                    foreach (var surface in new FrameworkElement[] { window, preview })
                    {
                        var speaker = Find<TextBlock>(surface, "SpeakerText"); var text = Find<TextBlock>(surface, "DialogueText");
                        Assert.Equal(story.Character?.FontFamily ?? style.FontFamily, text.FontFamily.Source);
                        Assert.Equal(story.Character?.FontSize ?? style.FontSize, text.FontSize);
                        Assert.Equal(story.Character is null ? TextAlignment.Center : TextAlignment.Right, text.TextAlignment);
                        Assert.Equal((Color)ColorConverter.ConvertFromString(story.Character?.NameColor ?? style.TextColor), Assert.IsType<SolidColorBrush>(speaker.Foreground).Color);
                        Assert.Equal(story.Kind == "clear_text" ? "A👩‍👩‍👧‍👦" : story.Text, text.Text);
                        Assert.Equal(story.Speaker.Length == 0 ? Visibility.Collapsed : Visibility.Visible, speaker.Visibility);
                        Assert.False(Find<EffectTextPresenter>(surface, "DialogueEffects").IsAnimating);
                    }
                    if (story.Character is not null)
                    {
                        Capture((FrameworkElement)window.Content, story.Kind == "clear_text" ? "erase-game.png" : "profile-game.png", 1280, 720);
                        Capture(preview, story.Kind == "clear_text" ? "erase-preview.png" : "profile-preview.png", 1280, 720);
                    }
                }
                preview.DisplayScene(project, project.Engine.RuntimeDefaults(project.Title), SceneVisuals.Empty, new("dialogue", "free", "plain", []), false, false);
                Assert.Equal(project.Engine.Theme.FontFamily, Find<TextBlock>(preview, "DialogueText").FontFamily.Source);
                Assert.Equal(project.Engine.Theme.Dialogue.FontSize, Find<TextBlock>(preview, "DialogueText").FontSize);
                Assert.Equal(1, Find<TextBlock>(preview, "DialogueText").Opacity);
                Assert.Equal(Visibility.Collapsed, Find<EffectTextPresenter>(preview, "DialogueEffects").Visibility);
            }
            finally { preview.Stop(); window.Close(); }
            return Task.CompletedTask;
        });
    }

    private static string VisualState(EffectTextPresenter view) => string.Join("|", view.Children.OfType<TextBlock>().Select(glyph =>
        $"{glyph.Text}:{glyph.RenderTransform.Value}:{glyph.Opacity:R}:{glyph.Foreground}"));
    private static T Find<T>(FrameworkElement target, string name) => (T)target.FindName(name);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Invoke(object target, string name, params object[] arguments) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(target, arguments);
    private static void Arrange(FrameworkElement target, double width, double height) { target.Measure(new Size(width, height)); target.Arrange(new Rect(0, 0, width, height)); target.UpdateLayout(); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static RenderTargetBitmap Capture(FrameworkElement target, string fileName, int width, int height)
    {
        Arrange(target, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(target);
        string? destination = Environment.GetEnvironmentVariable("KNE_NARRATIVE_RENDER");
        if (!string.IsNullOrEmpty(destination))
        {
            Directory.CreateDirectory(destination);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(destination, fileName)); encoder.Save(output);
        }
        return bitmap;
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); } catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }); Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
