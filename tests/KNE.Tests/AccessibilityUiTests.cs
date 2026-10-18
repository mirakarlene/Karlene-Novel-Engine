using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class AccessibilityUiTests
{
    [Fact]
    public Task Settings_ProjectsAndDesignUseSeparateGroupsAndSixPreviewCards_AndLiveSelectionsSurviveSaveReload() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new AccessibilitySettingsTests.SettingsFixture();
        string project = ProjectCreator.CreateProject("Options", fixture.Root);
        var historyTime = new DateTimeOffset(2026, 10, 18, 5, 6, 7, TimeSpan.Zero);
        await fixture.Store.SaveAsync(new UserSettings { RecentProjects = [project], ProjectLastOpened = new() { [project] = historyTime } });
        var window = Create(fixture.Store);
        try
        {
            Invoke(window, "Window_Loaded", window, new RoutedEventArgs()); await Idle(window);
            Invoke(window, "BtnSettings_Click", new Button(), new RoutedEventArgs()); Layout(window);
            var projects = Find<Border>(window, "ProjectsSettingsGroup"); var design = Find<Border>(window, "DesignSettingsGroup");
            var settings = Find<FrameworkElement>(window, "SettingsPanel");
            Assert.Contains(Find<TextBox>(window, "TxtEditorPath"), AccessibilityPresentationTests.Descendants(projects));
            Assert.Contains(Find<TextBox>(window, "TxtProjectsStorage"), AccessibilityPresentationTests.Descendants(projects));
            Assert.DoesNotContain(Find<TextBox>(window, "TxtUserName"), AccessibilityPresentationTests.Descendants(projects));
            Assert.Contains(Find<TextBox>(window, "TxtUserName"), AccessibilityPresentationTests.Descendants(design));
            Assert.Contains(Find<ComboBox>(window, "ThemeInput"), AccessibilityPresentationTests.Descendants(design));
            Assert.True(projects.TranslatePoint(new Point(), settings).X < design.TranslatePoint(new Point(), settings).X);
            Assert.Equal(projects.TranslatePoint(new Point(), settings).Y, design.TranslatePoint(new Point(), settings).Y, 1);
            var cards = Find<ItemsControl>(window, "AccessibilityCards");
            var radios = AccessibilityPresentationTests.Descendants(cards).OfType<RadioButton>().ToArray();
            Assert.Equal(6, radios.Length);
            string Mode(RadioButton radio) => Assert.Single(AccessibilityPresentationTests.Descendants(radio).OfType<AccessibilityPreview>()).Mode;
            Assert.Equal(BlockAccessibility.Modes.Select(mode => mode.Id), radios.Select(Mode));
            Assert.Single(radios, radio => radio.IsChecked == true && Mode(radio) == "normal");
            Assert.DoesNotContain(AccessibilityPresentationTests.Descendants(cards), element => element is ComboBox);
            var objects = Find<ComboBox>(window, "WelcomeObjectInput");
            Assert.Equal(WelcomeBubbles.Objects.Select(option => option.Id), objects.Items.Cast<WelcomeObjectOption>().Select(option => option.Id));
            Assert.Equal("bubbles", objects.SelectedValue);
            foreach (string mode in new[] { "protanopia", "high_contrast", "normal", "tritanopia" })
            {
                radios.Single(radio => Mode(radio) == mode).IsChecked = true;
                Assert.Equal(mode, BlockAccessibility.GetMode(window));
                Assert.Single(radios, radio => radio.IsChecked == true);
            }
            Find<TextBox>(window, "TxtUserName").Text = "  Карлена  ";
            Find<ComboBox>(window, "ThemeInput").SelectedValue = "aurora";
            Assert.Equal("tritanopia", BlockAccessibility.GetMode(window));
            objects.SelectedValue = "stars";
            Assert.Equal("stars", Find<WelcomeBubbles>(window, "WelcomeBubbleField").ObjectKind);
            await WorkspaceTabsTests.Invoke(window, "SaveSettingsAsync");
            var stored = await fixture.Store.LoadAsync();
            Assert.Equal("tritanopia", stored.AccessibilityMode); Assert.Equal("stars", stored.WelcomeObjects); Assert.Equal("aurora", stored.Theme);
            Assert.Equal("Карлена", stored.UserName); Assert.Equal([project], stored.RecentProjects); Assert.Equal(historyTime, stored.ProjectLastOpened[project]);
            Render(window, "settings-accessibility.png");
            var restored = Create(fixture.Store);
            try
            {
                Invoke(restored, "Window_Loaded", restored, new RoutedEventArgs()); await Idle(restored);
                Assert.Equal("tritanopia", BlockAccessibility.GetMode(restored));
                Assert.Equal("stars", Find<WelcomeBubbles>(restored, "WelcomeBubbleField").ObjectKind);
                Assert.Equal("aurora", Find<ComboBox>(restored, "ThemeInput").SelectedValue);
                Assert.Equal("Добро пожаловать, Карлена", Find<TextBlock>(restored, "WelcomeGreeting").Text);
                Invoke(restored, "BtnSettings_Click", new Button(), new RoutedEventArgs()); Layout(restored);
                var restoredRadios = AccessibilityPresentationTests.Descendants(Find<ItemsControl>(restored, "AccessibilityCards")).OfType<RadioButton>().ToArray();
                Assert.Single(restoredRadios, radio => radio.IsChecked == true && Mode(radio) == "tritanopia");
                Assert.Equal("stars", Find<ComboBox>(restored, "WelcomeObjectInput").SelectedValue);
            }
            finally { restored.Close(); }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Settings_MinimumWindowKeepsBothGroupsAndAllSixCardsWithinHorizontalViewport_AndFooterReachableWhileScrolling() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new AccessibilitySettingsTests.SettingsFixture();
        var window = Create(fixture.Store); var size = new Size(1100, 700);
        try
        {
            Invoke(window, "Window_Loaded", window, new RoutedEventArgs()); await Idle(window);
            Invoke(window, "BtnSettings_Click", new Button(), new RoutedEventArgs()); Layout(window, size);
            var root = (FrameworkElement)window.Content;
            var settings = Find<FrameworkElement>(window, "SettingsPanel");
            var projects = Find<Border>(window, "ProjectsSettingsGroup"); var design = Find<Border>(window, "DesignSettingsGroup");
            var cards = Find<ItemsControl>(window, "AccessibilityCards");
            var scroller = Assert.Single(AccessibilityPresentationTests.Descendants(settings).OfType<ScrollViewer>(), scroll =>
                AccessibilityPresentationTests.Descendants(scroll).Contains(projects) && AccessibilityPresentationTests.Descendants(scroll).Contains(design));
            Rect Bounds(FrameworkElement element, Visual relative) => element.TransformToVisual(relative).TransformBounds(new Rect(element.RenderSize));
            void FitsHorizontally(FrameworkElement element)
            {
                var bounds = Bounds(element, scroller);
                Assert.True(element.ActualWidth > 0, element.GetType().Name + " must be laid out.");
                Assert.InRange(bounds.Left, -1, scroller.ViewportWidth + 1);
                Assert.InRange(bounds.Right, 0, scroller.ViewportWidth + 1);
            }
            FitsHorizontally(projects); FitsHorizontally(design);
            Assert.True(Bounds(projects, scroller).Right < Bounds(design, scroller).Left);
            var radios = AccessibilityPresentationTests.Descendants(cards).OfType<RadioButton>().ToArray(); Assert.Equal(6, radios.Length);
            foreach (var radio in radios)
            {
                FitsHorizontally(radio);
                var preview = Assert.Single(AccessibilityPresentationTests.Descendants(radio).OfType<AccessibilityPreview>());
                Assert.InRange(preview.ActualWidth, 100, radio.ActualWidth);
                var previewBounds = Bounds(preview, radio);
                Assert.InRange(previewBounds.Left, 0, radio.ActualWidth);
                Assert.InRange(previewBounds.Right, 0, radio.ActualWidth);
            }
            var footerButtons = AccessibilityPresentationTests.Descendants(settings).OfType<Button>().Where(button => Equals(button.Content, "Сохранить") || Equals(button.Content, "Назад")).ToArray();
            Assert.Equal(2, footerButtons.Length);
            var footerBefore = footerButtons.Select(button => Bounds(button, root)).ToArray();
            foreach (var button in footerButtons)
            {
                Assert.DoesNotContain(button, AccessibilityPresentationTests.Descendants(scroller));
                var bounds = Bounds(button, root); Assert.True(button.IsEnabled); Assert.True(bounds.Width > 0 && bounds.Height > 0);
                Assert.InRange(bounds.Left, 0, size.Width); Assert.InRange(bounds.Right, 0, size.Width);
                Assert.InRange(bounds.Top, 0, size.Height); Assert.InRange(bounds.Bottom, 0, size.Height);
            }
            scroller.ScrollToBottom(); root.UpdateLayout();
            var lastCard = Bounds(radios[^1], scroller);
            Assert.InRange(lastCard.Top, 0, scroller.ViewportHeight + 1);
            Assert.InRange(lastCard.Bottom, 0, scroller.ViewportHeight + 1);
            Assert.Equal(footerBefore, footerButtons.Select(button => Bounds(button, root)));
            Render(window, "settings-accessibility-minimum.png", size);
        }
        finally { window.Close(); }
    });

    private static MainWindow Create(UserSettingsStore store)
    {
        var window = new MainWindow(); typeof(MainWindow).GetField("settingsStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, store); return window;
    }
    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static void Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static async Task Idle(MainWindow window)
    {
        for (int i = 0; i < 500; i++)
        {
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (!(bool)typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Settings did not finish loading.");
    }
    private static void Layout(MainWindow owner, Size? requested = null)
    { var view = (FrameworkElement)owner.Content; var size = requested ?? new Size(1460, 1000); view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout(); }
    private static void Render(MainWindow owner, string name, Size? requested = null)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_ACCESS_RENDER"); if (directory == null) return;
        Directory.CreateDirectory(directory); var size = requested ?? new Size(1460, 1000); Layout(owner, size);
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render((Visual)owner.Content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
