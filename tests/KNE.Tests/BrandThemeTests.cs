using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class BrandThemeTests
{
    [Fact]
    public async Task Settings_FirstLaunchUsesKne_AndExistingExplicitThemesArePreserved()
    {
        using var fixture = new Fixture();
        Assert.Equal("kne", new UserSettings().Theme);
        Assert.Equal("", new UserSettings().UserName);
        Assert.Equal("kne", (await fixture.Store.LoadAsync()).Theme);
        foreach (string id in new[] { "kne", "light", "dark", "rose", "ocean", "forest", "amber" })
        {
            await fixture.Store.SaveAsync(new UserSettings { Theme = id, UserName = "Алиса" });
            var loaded = await fixture.Store.LoadAsync();
            Assert.Equal(id, loaded.Theme); Assert.Equal("Алиса", loaded.UserName);
        }
        foreach (string json in new[] { "{}", "{\"Theme\":null,\"UserName\":null}", "{\"Theme\":\"missing\"}" })
        {
            await File.WriteAllTextAsync(fixture.Store.SettingsPath, json);
            var loaded = await fixture.Store.LoadAsync();
            Assert.Equal("kne", loaded.Theme); Assert.Equal("", loaded.UserName);
        }
    }

    [Fact]
    public Task Palettes_AreDistinctWindowScopedAndDynamic_AndBrandColorsAndGradientStayAvailable() => WorkspaceTabsTests.Sta(() =>
    {
        string[] ids = ["kne", "light", "dark", "rose", "ocean", "forest", "amber"];
        Assert.Equal(ids, EditorTheme.Themes.Select(option => option.Id));
        Assert.All(EditorTheme.Themes, option => Assert.False(string.IsNullOrWhiteSpace(option.Label)));
        Assert.Equal(ids.Length, EditorTheme.Themes.Select(option => option.Label).Distinct().Count());
        var first = new Window(); var second = new Window();
        var field = new TextBox { Text = "Палитра" }; first.Content = field;
        try
        {
            EditorTheme.Apply(second, "light");
            var isolated = ColorOf(second.Background);
            var surfaces = new HashSet<Color>();
            foreach (string id in ids)
            {
                Assert.Equal(id, EditorTheme.Normalize(id));
                EditorTheme.Apply(first, id); Layout(first);
                Assert.Equal(ColorOf(first.FindResource("Editor.Surface")), ColorOf(field.Background));
                Assert.Equal(ColorOf(first.FindResource("Editor.Text")), ColorOf(field.Foreground));
                Assert.Equal(ColorOf(first.FindResource("Editor.Background")), ColorOf(first.Background));
                Assert.Equal(ColorOf(first.FindResource("Editor.Text")), ColorOf(first.Foreground));
                Assert.Equal((Color)ColorConverter.ConvertFromString("#7C5CFF"), first.FindResource("KNE.AccentColor"));
                Assert.Equal((Color)first.FindResource("KNE.AccentColor"), ColorOf(first.FindResource("KNE.AccentBrush")));
                Assert.Equal((Color)first.FindResource("KNE.PinkColor"), ColorOf(first.FindResource("KNE.PinkBrush")));
                var gradient = Assert.IsType<LinearGradientBrush>(first.FindResource("Editor.WelcomeBackground"));
                Assert.True(gradient.GradientStops.Select(stop => stop.Color).Distinct().Count() > 1);
                Assert.True(surfaces.Add(ColorOf(field.Background)), id + " must have a distinct surface.");
                Assert.Equal(isolated, ColorOf(second.Background));
            }
            foreach (string? value in new[] { null, "", "UNKNOWN" })
            {
                Assert.Equal("kne", EditorTheme.Normalize(value));
                EditorTheme.Apply(first, value!);
                Assert.Equal("dark", first.FindResource("Editor.ThemeName"));
            }
            Assert.Throws<ArgumentNullException>(() => EditorTheme.Apply(null!, "kne"));
        }
        finally { first.Close(); second.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Welcome_UserNameIsTrimmedSavedAndLoaded_AndEmptyRestoresTheDefaultGreeting() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new Fixture();
        var window = CreateWindow(fixture);
        try
        {
            Invoke(window, "Window_Loaded", window, new RoutedEventArgs()); await Idle(window);
            Assert.Equal("Добро пожаловать в KNE", Find<TextBlock>(window, "WelcomeGreeting").Text);
            var logo = Find<WatercolorLogo>(window, "WelcomeLogo");
            Assert.Equal("Karlene Novel Engine", logo.Text);
            Assert.Equal("Karlene Novel Engine", AutomationProperties.GetName(logo));
            Assert.Equal((Color)window.FindResource("KNE.AccentColor"), logo.StartColor);
            Assert.Equal((Color)window.FindResource("KNE.PinkColor"), logo.EndColor);
            var backdrop = Find<Grid>(window, "WelcomeBackdrop");
            Assert.Equal(Visibility.Visible, backdrop.Visibility); Assert.False(backdrop.IsHitTestVisible);
            Assert.Equal(ColorOf(window.FindResource("KNE.AccentBrush")), ColorOf(Find<Button>(window, "CreateProjectButton").Background));
            Assert.Equal("kne", Find<ComboBox>(window, "ThemeInput").SelectedValue);
            Assert.Equal("kne", (await fixture.Store.LoadAsync()).Theme);
            Render(window, "welcome-kne.png");
            foreach (var (input, stored, greeting) in new[]
            {
                ("  Алиса  ", "Алиса", "Добро пожаловать, Алиса"),
                ("   ", "", "Добро пожаловать в KNE")
            })
            {
                Invoke(window, "BtnSettings_Click", new Button(), new RoutedEventArgs());
                Assert.Equal(Visibility.Collapsed, backdrop.Visibility);
                Find<TextBox>(window, "TxtUserName").Text = input;
                Find<ComboBox>(window, "ThemeInput").SelectedValue = "ocean";
                await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
                Assert.Equal(stored, (await fixture.Store.LoadAsync()).UserName);
                Assert.Equal(greeting, Find<TextBlock>(window, "WelcomeGreeting").Text);
                var restored = CreateWindow(fixture);
                try
                {
                    Invoke(restored, "Window_Loaded", restored, new RoutedEventArgs()); await Idle(restored);
                    Assert.Equal(greeting, Find<TextBlock>(restored, "WelcomeGreeting").Text);
                    Assert.Equal(stored, Find<TextBox>(restored, "TxtUserName").Text);
                    Assert.Equal("ocean", Find<ComboBox>(restored, "ThemeInput").SelectedValue);
                }
                finally { restored.Close(); }
            }
            Invoke(window, "BtnSettingsBack_Click", new Button(), new RoutedEventArgs()); await Idle(window);
            Render(window, "welcome-brand.png");
            Invoke(window, "BtnSettings_Click", new Button(), new RoutedEventArgs());
            Render(window, "settings-ocean.png");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Appearance_ClosedComboboxRendersLabelsForObjectsAndItems_AndEditableAndCustomTemplatesStillWork() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new Fixture();
        var project = await ProjectLoader.LoadProjectAsync(fixture.Project);
        var editor = new AppearanceEditor(); var owner = new Window { Content = editor };
        try
        {
            EditorTheme.Apply(owner, "kne"); await editor.OpenAsync(project);
            foreach (string name in new[] { "ScreenRole", "ElementAction", "PaletteChoice", "PatternChoice" })
            {
                var choice = Find<ComboBox>(editor, name);
                choice.SelectedIndex = choice.Items.Count - 1; Layout(owner);
                object item = choice.SelectedItem;
                string expected = (string)item.GetType().GetProperty("Label")!.GetValue(item)!;
                var presenter = Assert.IsType<ContentPresenter>(choice.Template.FindName("SelectedContent", choice));
                Assert.Contains(Descendants(presenter).OfType<TextBlock>(), text => text.Text == expected);
                Assert.DoesNotContain(Descendants(presenter).OfType<TextBlock>(), text => text.Text.Contains("AppearanceChoice"));
                Assert.Equal(item.GetType().GetProperty("Value")!.GetValue(item), choice.SelectedValue);
            }
            var types = Find<ComboBox>(editor, "NewElementType"); types.SelectedIndex = 1; Layout(owner);
            Assert.Contains(Descendants((DependencyObject)types.Template.FindName("SelectedContent", types)).OfType<TextBlock>(), text => text.Text == "Текст");
            var custom = new ComboBox { ItemsSource = new[] { new Choice("x", "Пользовательский") }, DisplayMemberPath = "Label", SelectedIndex = 0 };
            var panel = new StackPanel(); panel.Children.Add(custom); owner.Content = panel; Layout(owner);
            Assert.Contains(Descendants(custom).OfType<TextBlock>(), text => text.Text == "Пользовательский");
            custom.IsEditable = true; custom.Text = "Мой текст"; Layout(owner);
            var editable = Assert.IsType<TextBox>(custom.Template.FindName("PART_EditableTextBox", custom));
            Assert.Equal(Visibility.Visible, editable.Visibility); Assert.Equal("Мой текст", editable.Text);
            var template = new DataTemplate(); var visual = new FrameworkElementFactory(typeof(TextBlock));
            visual.SetValue(TextBlock.TextProperty, "Своя разметка"); template.VisualTree = visual;
            var templated = new ComboBox { ItemsSource = new[] { new Choice("x", "Текст модели") }, ItemTemplate = template, SelectedIndex = 0 };
            panel.Children.Add(templated); Layout(owner);
            Assert.Equal(0, templated.SelectedIndex);
            Assert.Contains(Descendants(templated).OfType<TextBlock>(), text => text.Text == "Своя разметка");
            owner.Content = editor; Layout(owner); Render(owner, "appearance-labels.png");
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task Tabs_LiveThemeSwitchKeepsSelectedAndInactiveHeadersReadable_AndCloseStillClosesOnlyItsFile() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new Fixture();
        string first = Path.Combine(fixture.Project, "first.krl"), second = Path.Combine(fixture.Project, "second.krl");
        await File.WriteAllTextAsync(first, "function first() end"); await File.WriteAllTextAsync(second, "function second() end");
        var window = CreateWindow(fixture);
        try
        {
            await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.Project, false);
            await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", first); await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", second);
            var tabs = Find<TabControl>(window, "DocumentTabs");
            foreach (string theme in new[] { "dark", "rose", "light", "kne" })
            {
                EditorTheme.Apply(window, theme); Layout(window);
                foreach (TabItem tab in tabs.Items)
                {
                    var header = Assert.IsType<StackPanel>(tab.Header);
                    var title = Assert.Single(header.Children.OfType<TextBlock>());
                    Assert.Equal(tab == tabs.Items[0] ? "first.krl" : "second.krl", title.Text);
                    Assert.Equal(ColorOf(window.FindResource("Editor.Text")), ColorOf(title.Foreground));
                    Assert.Equal(ColorOf(window.FindResource("Editor.Text")), ColorOf(tab.Foreground));
                    var surface = Assert.Single(Descendants(tab).OfType<Border>(), border => border.Name == "TabSurface");
                    Assert.Equal(ColorOf(window.FindResource(tab.IsSelected ? "Editor.Selection" : "Editor.Background")), ColorOf(surface.Background));
                }
            }
            Render(window, "tabs-kne.png");
            var firstTab = (TabItem)tabs.Items[0];
            Assert.Single(((StackPanel)firstTab.Header).Children.OfType<Button>()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle(window);
            Assert.Single(tabs.Items); Assert.Equal("second.krl", Assert.Single(((StackPanel)((TabItem)tabs.Items[0]).Header).Children.OfType<TextBlock>()).Text);
            Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Buttons_HoverTemplateAnimatesIntoVioletAndBack_AndPrimaryAccentUsesDynamicResources() => WorkspaceTabsTests.Sta(() =>
    {
        var owner = new Window(); var button = new Button { Content = "Наведение" }; owner.Content = button;
        try
        {
            EditorTheme.Apply(owner, "kne"); Layout(owner);
            var hover = Assert.Single(button.Template.Triggers.OfType<Trigger>(), trigger => trigger.Property == UIElement.IsMouseOverProperty);
            Assert.Equal(true, hover.Value);
            var entering = Assert.Single(Assert.IsType<BeginStoryboard>(Assert.Single(hover.EnterActions.Cast<TriggerAction>())).Storyboard.Children.OfType<ColorAnimation>());
            var leaving = Assert.Single(Assert.IsType<BeginStoryboard>(Assert.Single(hover.ExitActions.Cast<TriggerAction>())).Storyboard.Children.OfType<ColorAnimation>());
            Assert.Equal("HoverSurface", Storyboard.GetTargetName(entering));
            var targetPath = Storyboard.GetTargetProperty(entering);
            Assert.Contains(UIElement.OpacityMaskProperty, targetPath.PathParameters);
            Assert.Contains(SolidColorBrush.ColorProperty, targetPath.PathParameters);
            Assert.InRange(entering.Duration.TimeSpan.TotalMilliseconds, 100, 500);
            Assert.InRange(leaving.Duration.TimeSpan.TotalMilliseconds, 100, 500);
            var surface = Assert.IsType<Border>(button.Template.FindName("HoverSurface", button));
            Assert.Equal((Color)ColorConverter.ConvertFromString("#7C5CFF"), ColorOf(surface.Background));
            var brush = Assert.IsType<SolidColorBrush>(surface.OpacityMask).CloneCurrentValue(); surface.OpacityMask = brush;
            brush.Color = Color.FromArgb(0, 255, 255, 255);
            var enterClock = (AnimationClock)entering.CreateClock(); brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, enterClock);
            enterClock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
            Assert.Equal((byte)0, brush.Color.A);
            enterClock.Controller.SeekAlignedToLastTick(TimeSpan.FromTicks(entering.Duration.TimeSpan.Ticks / 2), TimeSeekOrigin.BeginTime);
            Assert.InRange(brush.Color.A, (byte)1, (byte)254);
            enterClock.Controller.SeekAlignedToLastTick(entering.Duration.TimeSpan, TimeSeekOrigin.BeginTime);
            Assert.Equal(Colors.White, brush.Color);
            var leaveClock = (AnimationClock)leaving.CreateClock(); brush.ApplyAnimationClock(SolidColorBrush.ColorProperty, leaveClock);
            leaveClock.Controller!.SeekAlignedToLastTick(TimeSpan.FromTicks(leaving.Duration.TimeSpan.Ticks / 2), TimeSeekOrigin.BeginTime);
            Assert.InRange(brush.Color.A, (byte)1, (byte)254);
            leaveClock.Controller.SeekAlignedToLastTick(leaving.Duration.TimeSpan, TimeSeekOrigin.BeginTime);
            Assert.Equal((byte)0, brush.Color.A);
            button.Style = (Style)owner.FindResource("KNE.PrimaryButton"); Layout(owner);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#7C5CFF"), ColorOf(button.Background));
            var customAccent = new SolidColorBrush(Colors.MediumPurple); owner.Resources["KNE.AccentBrush"] = customAccent;
            Assert.Equal(Colors.MediumPurple, ColorOf(button.Background));
            Assert.Equal(Colors.White, ColorOf(button.Foreground));
            Assert.Equal(customAccent.Color, ColorOf(button.BorderBrush));
            Assert.Equal(customAccent.Color, ColorOf(Assert.IsType<Border>(button.Template.FindName("HoverSurface", button)).Background));
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void Bubbles_DriftRepelThePointerAndWrap_WithoutGrowingOrLosingFiniteBounds()
    {
        var plain = new BubbleField(); var repelled = new BubbleField(); var size = new Size(900, 600);
        plain.Resize(size); repelled.Resize(size);
        int count = plain.Bubbles.Count; Assert.InRange(count, 10, 60);
        var original = plain.Bubbles[0]; var pointer = new Point(original.X - 10, original.Y);
        double initialY = original.Y;
        plain.Step(.05, size, null); repelled.Step(.05, size, pointer);
        Assert.NotEqual(initialY, original.Y);
        Assert.True(repelled.Bubbles[0].X > original.X);
        Assert.True(repelled.Bubbles[0].Vx > 0);
        var centered = new BubbleField(); centered.Resize(size);
        centered.Step(.05, size, new Point(centered.Bubbles[0].X, centered.Bubbles[0].Y));
        Assert.True(Math.Abs(centered.Bubbles[0].Vx) + Math.Abs(centered.Bubbles[0].Vy) > 0);
        var threshold = new BubbleField(); threshold.Resize(size);
        threshold.Bubbles[0].X = 200; threshold.Bubbles[0].Y = 100; threshold.Bubbles[0].Radius = 20;
        threshold.Step(.05, size, new Point(80, 100)); Assert.Equal(0, threshold.Bubbles[0].Vx);
        threshold.Bubbles[0].X = 200; threshold.Bubbles[0].Y = 100;
        threshold.Step(.05, size, new Point(80.001, 100)); Assert.True(threshold.Bubbles[0].Vx > 0);
        for (int step = 0; step < 5000; step++) repelled.Step(.05, size, step % 2 == 0 ? pointer : null);
        Assert.Equal(count, repelled.Bubbles.Count);
        foreach (var bubble in repelled.Bubbles)
        {
            Assert.True(double.IsFinite(bubble.X) && double.IsFinite(bubble.Y));
            Assert.InRange(bubble.X, -bubble.Radius, size.Width + bubble.Radius);
            Assert.InRange(bubble.Y, -bubble.Radius, size.Height + bubble.Radius);
            Assert.InRange(bubble.Vx, -90, 90); Assert.InRange(bubble.Vy, -90, 90);
        }
        var edge = repelled.Bubbles[0]; edge.Y = -edge.Radius - 1; edge.X = -edge.Radius - 1;
        repelled.Step(0, size, null);
        Assert.Equal(size.Height + edge.Radius, edge.Y); Assert.Equal(size.Width + edge.Radius, edge.X);
        edge.Y = size.Height + edge.Radius + 1; edge.X = size.Width + edge.Radius + 1;
        repelled.Step(0, size, null);
        Assert.Equal(-edge.Radius, edge.Y); Assert.Equal(-edge.Radius, edge.X);
    }

    [Fact]
    public void Bubbles_ResizePreservesRelativePositions_AndInvalidInputsAndLongFramesCannotDestabilizeMotion()
    {
        var field = new BubbleField();
        field.Step(0, new Size(0, 600), null); Assert.Empty(field.Bubbles);
        field.Resize(new Size(double.PositiveInfinity, 600)); Assert.Empty(field.Bubbles);
        var size = new Size(800, 600); field.Resize(size);
        var before = field.Bubbles.Select(bubble => (bubble.X, bubble.Y, bubble.Radius)).ToArray();
        field.Resize(new Size(400, 300));
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].X / 2, field.Bubbles[i].X); Assert.Equal(before[i].Y / 2, field.Bubbles[i].Y);
            Assert.Equal(before[i].Radius, field.Bubbles[i].Radius);
        }
        foreach (double seconds in new[] { -.001, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => field.Step(seconds, size, null));
        var capped = new BubbleField(); var regular = new BubbleField(); var invalidPointer = new BubbleField();
        capped.Step(1000, size, null); regular.Step(.05, size, null); invalidPointer.Step(.05, size, new Point(double.NaN, 0));
        for (int i = 0; i < capped.Bubbles.Count; i++)
        {
            Assert.Equal(regular.Bubbles[i].X, capped.Bubbles[i].X); Assert.Equal(regular.Bubbles[i].Y, capped.Bubbles[i].Y);
            Assert.Equal(regular.Bubbles[i].X, invalidPointer.Bubbles[i].X); Assert.Equal(regular.Bubbles[i].Y, invalidPointer.Bubbles[i].Y);
        }
    }

    [Fact]
    public Task WelcomeAnimations_StopWhenHiddenMinimizedUnloadedAndClosed_AndDecorationsNeverInterceptInput() => WorkspaceTabsTests.Sta(async () =>
    {
        var bubbles = new WelcomeBubbles();
        var owner = new Window { Content = bubbles, Width = 500, Height = 300, ShowActivated = false, Left = -10000, Top = -10000 };
        try
        {
            Assert.False(bubbles.IsHitTestVisible); Assert.False(bubbles.IsAnimating);
            owner.Show(); await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(SystemParameters.ClientAreaAnimation, bubbles.IsAnimating);
            bubbles.Visibility = Visibility.Collapsed; Assert.False(bubbles.IsAnimating);
            bubbles.Visibility = Visibility.Visible;
            Assert.Equal(SystemParameters.ClientAreaAnimation, bubbles.IsAnimating);
            owner.WindowState = WindowState.Minimized; Assert.False(bubbles.IsAnimating);
            owner.WindowState = WindowState.Normal;
            Assert.Equal(SystemParameters.ClientAreaAnimation, bubbles.IsAnimating);
            owner.Content = null; await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.False(bubbles.IsAnimating);
            owner.Content = bubbles; await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(SystemParameters.ClientAreaAnimation, bubbles.IsAnimating);
            owner.Close(); Assert.False(bubbles.IsAnimating);
        }
        finally { owner.Close(); }
    });

    [Fact]
    public Task WelcomeBubbleControl_ForwardsThePointerAndClearsInvalidOrDepartedPositions() => WorkspaceTabsTests.Sta(() =>
    {
        var plain = new WelcomeBubbles(); var active = new WelcomeBubbles(); var invalid = new WelcomeBubbles(); var departed = new WelcomeBubbles();
        foreach (var control in new[] { plain, active, invalid, departed })
        {
            control.Measure(new Size(900, 600)); control.Arrange(new Rect(0, 0, 900, 600)); control.Step(0);
        }
        var pointer = new Point(plain.Field.Bubbles[0].X - 10, plain.Field.Bubbles[0].Y);
        active.SetPointer(pointer); invalid.SetPointer(pointer); invalid.SetPointer(new Point(double.PositiveInfinity, 0));
        departed.SetPointer(pointer); departed.SetPointer(null);
        foreach (var control in new[] { plain, active, invalid, departed }) control.Step(.05);
        Assert.True(active.Field.Bubbles[0].X > plain.Field.Bubbles[0].X);
        Assert.Equal(plain.Field.Bubbles[0].X, invalid.Field.Bubbles[0].X);
        Assert.Equal(plain.Field.Bubbles[0].Y, invalid.Field.Bubbles[0].Y);
        Assert.Equal(plain.Field.Bubbles[0].X, departed.Field.Bubbles[0].X);
        Assert.Equal(plain.Field.Bubbles[0].Y, departed.Field.Bubbles[0].Y);
        return Task.CompletedTask;
    });

    [Fact]
    public Task WatercolorLogo_RendersPurpleToPinkPigment_AndLettersFloatIndependentlyWithinGentleBounds() => WorkspaceTabsTests.Sta(() =>
    {
        var logo = new WatercolorLogo { Width = 1000, Height = 150 };
        Assert.Equal("Karlene Novel Engine", WatercolorLogo.BrandText); Assert.Equal(WatercolorLogo.BrandText, logo.Text);
        Assert.False(logo.IsHitTestVisible);
        byte[] first = LogoPixels(logo);
        var colors = new HashSet<uint>(); long leftR = 0, leftB = 0, rightR = 0, rightB = 0; int colored = 0;
        for (int y = 0; y < 150; y++) for (int x = 0; x < 1000; x++)
        {
            int offset = (y * 1000 + x) * 4; if (first[offset + 3] < 150) continue;
            colored++; colors.Add(BitConverter.ToUInt32(first, offset));
            if (x < 300) { leftB += first[offset]; leftR += first[offset + 2]; }
            if (x > 700) { rightB += first[offset]; rightR += first[offset + 2]; }
        }
        Assert.True(colored > 3000); Assert.True(colors.Count > 100);
        Assert.True(leftB > leftR, "The start of the title must be purple.");
        Assert.True(rightR * leftB > leftR * rightB, "Pigment must transition toward pink at the end of the title.");
        var before = Enumerable.Range(0, logo.Text.Length).Select(logo.OffsetFor).ToArray();
        for (int step = 0; step < 40; step++) logo.Step(.05);
        Assert.NotEqual(before[0], logo.OffsetFor(0)); Assert.NotEqual(logo.OffsetFor(0), logo.OffsetFor(1));
        for (int i = 0; i < logo.Text.Length; i++)
        {
            Assert.InRange(logo.OffsetFor(i).X, -1.4, 1.4); Assert.InRange(logo.OffsetFor(i).Y, -3, 3);
        }
        Assert.False(first.SequenceEqual(LogoPixels(logo)));
        double phase = logo.MotionPhase;
        foreach (double seconds in new[] { -.001, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => logo.Step(seconds));
        Assert.Equal(phase, logo.MotionPhase);
        var capped = new WatercolorLogo(); var regular = new WatercolorLogo(); capped.Step(100); regular.Step(.05);
        Assert.Equal(regular.MotionPhase, capped.MotionPhase);
        logo.StartColor = Colors.Blue; logo.EndColor = Colors.Blue;
        Assert.False(first.SequenceEqual(LogoPixels(logo)));
        return Task.CompletedTask;
    });

    private static byte[] LogoPixels(WatercolorLogo logo)
    {
        logo.Measure(new Size(1000, 150)); logo.Arrange(new Rect(0, 0, 1000, 150)); logo.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1000, 150, 96, 96, PixelFormats.Pbgra32); bitmap.Render(logo);
        var pixels = new byte[1000 * 150 * 4]; bitmap.CopyPixels(pixels, 1000 * 4, 0); return pixels;
    }

    private sealed record Choice(string Value, string Label);
    private static T Find<T>(FrameworkElement view, string name) where T : class => WorkspaceTabsTests.Find<T>(view, name);
    private static Color ColorOf(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static MainWindow CreateWindow(Fixture fixture)
    {
        var window = new MainWindow();
        typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, fixture.Store);
        return window;
    }
    private static async Task Idle(MainWindow window)
    {
        for (int attempt = 0; attempt < 500; attempt++)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!(bool)typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Workspace operation did not finish.");
    }
    private static void Layout(Window window)
    {
        var view = (FrameworkElement)window.Content;
        view.Measure(new Size(1460, 900)); view.Arrange(new Rect(0, 0, 1460, 900)); view.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Render(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_BRAND_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); Layout(window);
        var image = new RenderTargetBitmap(1460, 900, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1460, 900));
        image.Render(background); image.Render((Visual)window.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
    private sealed class Fixture : IDisposable
    {
        private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "kne-brand-theme-tests");
        public string Root { get; } = Path.Combine(Scratch, "ui-" + Guid.NewGuid().ToString("N"));
        public string Project { get; }
        public UserSettingsStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Root); Project = ProjectCreator.CreateProject("Brand", Root); Store = new(Path.Combine(Root, "settings.json")); }
        public void Dispose()
        {
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Scratch) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(target, true);
        }
    }
}
