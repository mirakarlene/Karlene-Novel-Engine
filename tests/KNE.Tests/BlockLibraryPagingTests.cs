using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class BlockLibraryPagingTests
{
    [Fact]
    public async Task Picker_GroupsSearchAndPluginWarningKeepSameBounds_AndCardsStayFourColumnsByThreeRows()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var view = await View(fixture); var groups = Find<ListBox>(view, "BlockGroups");
            Assert.Equal(new[] { "favorites", "recent" }, groups.Items.Cast<BlockGroup>().Take(2).Select(group => group.Id));
            Assert.Equal("plugins", groups.Items.Cast<BlockGroup>().Last().Id);
            var bounds = new List<(Size View, Point GroupOrigin, Point CardsOrigin)>();
            foreach (var group in groups.Items.Cast<BlockGroup>().ToArray())
            {
                Select(view, group.Id); Layout(view);
                bounds.Add((view.RenderSize, groups.TranslatePoint(new Point(), view), Find<WrapPanel>(view, "BlockCards").TranslatePoint(new Point(), view)));
                Assert.Equal(group.Id == "plugins" ? Visibility.Visible : Visibility.Hidden, Find<TextBlock>(view, "PluginWarning").Visibility);
                Assert.Equal("Внешние файлы, особенно скачанные из интернета, могут иметь неожиданные последствия, работайте с этим на свой страх и риск", Find<TextBlock>(view, "PluginWarning").Text);
                foreach (var card in Cards(view)) { Assert.Equal(236, card.ActualWidth); Assert.Equal(130, card.ActualHeight); }
            }
            Assert.Single(bounds.Distinct());
            Select(view, "visual"); Layout(view); var cards = Cards(view);
            Assert.Equal(12, cards.Length);
            var panel = Find<WrapPanel>(view, "BlockCards");
            Assert.Equal(new[] { 0d, 248, 496, 744 }, cards.Take(4).Select(card => card.TranslatePoint(new Point(), panel).X));
            Assert.Equal(new[] { 0d, 142, 284 }, cards.Where((_, index) => index % 4 == 0).Select(card => card.TranslatePoint(new Point(), panel).Y));
            Find<TextBox>(view, "BlockSearch").Text = "no matching block"; Layout(view);
            Assert.Equal(bounds[0].View, view.RenderSize); Assert.Empty(Cards(view));
            Assert.Equal(bounds[0].CardsOrigin, panel.TranslatePoint(new Point(), view));
        });
    }

    [Fact]
    public async Task Picker_PagingShowsEachEntryExactlyOnce_ResetsOnSearchAndGroup_AndClampsEmptyPages()
    {
        using var fixture = new TempProject(); var definitions = BlockLibrary.Definitions.Take(25).ToArray();
        var store = Store(fixture); await store.SaveAsync(new() { Favorites = definitions.Select(BlockLibraryPreferences.Key).ToList() });
        await Sta(async () =>
        {
            var view = await View(fixture); Select(view, "favorites");
            Assert.Equal("1 / 3", Find<TextBlock>(view, "PageStatus").Text);
            Assert.False(Find<Button>(view, "PreviousPageButton").IsEnabled);
            var seen = new List<string>();
            for (int index = 0; index < 3; index++)
            {
                seen.AddRange(Cards(view).Select(card => (string)Star(card).Tag));
                Assert.Equal(index == 2 ? 1 : 12, Cards(view).Length);
                if (index < 2) Click(view, "NextPageButton");
            }
            Assert.Equal(definitions.Select(BlockLibraryPreferences.Key), seen);
            Assert.False(Find<Button>(view, "NextPageButton").IsEnabled);
            Click(view, "NextPageButton"); Assert.Equal("3 / 3", Find<TextBlock>(view, "PageStatus").Text);
            Find<TextBox>(view, "BlockSearch").Text = definitions[0].Title;
            Assert.StartsWith("1 /", Find<TextBlock>(view, "PageStatus").Text);
            Find<TextBox>(view, "BlockSearch").Text = "missing block";
            Assert.Equal("1 / 1", Find<TextBlock>(view, "PageStatus").Text); Assert.Empty(Cards(view));
            Click(view, "PreviousPageButton"); Assert.Equal("1 / 1", Find<TextBlock>(view, "PageStatus").Text);
            Find<TextBox>(view, "BlockSearch").Clear(); Click(view, "NextPageButton"); Select(view, "terminals");
            Assert.Equal("1 / 1", Find<TextBlock>(view, "PageStatus").Text); Assert.Equal(2, Cards(view).Length);
            Select(view, "favorites"); Assert.Equal("1 / 3", Find<TextBlock>(view, "PageStatus").Text);
        });
    }

    [Fact]
    public async Task Picker_StarsNeverInsertBlocks_PersistAcrossViews_AndKeepConditionGroupsIndependent()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var view = await View(fixture); int insertions = 0; view.BlockSelected += (_, _) => insertions++;
            Assert.Throws<InvalidOperationException>(() => view.ConfigurePreferenceStore(Store(fixture)));
            Select(view, "narrative"); Star(Cards(view).Single(card => Equals(card.Tag, ScriptNodeKind.Condition))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.SavePreferencesAsync();
            Select(view, "logic"); Star(Cards(view).Single(card => Equals(card.Tag, ScriptNodeKind.Condition))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.SavePreferencesAsync(); Assert.Equal(0, insertions);
            var reopened = await View(fixture); Select(reopened, "favorites");
            Assert.Equal(new[] { "narrative/Condition", "logic/Condition" }, Cards(reopened).Select(card => (string)Star(card).Tag));
            Star(Cards(reopened)[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await reopened.SavePreferencesAsync();
            Assert.Equal("logic/Condition", (string)Star(Assert.Single(Cards(reopened))).Tag);
            var third = await View(fixture); Select(third, "favorites"); Assert.Equal("logic/Condition", (string)Star(Assert.Single(Cards(third))).Tag);
        });
    }

    [Theory]
    [InlineData(0, "1 / 1", 0)]
    [InlineData(1, "1 / 1", 1)]
    [InlineData(12, "1 / 1", 12)]
    [InlineData(13, "1 / 2", 12)]
    [InlineData(24, "1 / 2", 12)]
    [InlineData(25, "1 / 3", 12)]
    public async Task Picker_PageCapacityHonorsEmptySingletonAndBothSidesOfTwelveEntryBoundary(int count, string label, int firstPage)
    {
        using var fixture = new TempProject();
        await Store(fixture).SaveAsync(new() { Favorites = BlockLibrary.Definitions.Take(count).Select(BlockLibraryPreferences.Key).ToList() });
        await Sta(async () =>
        {
            var view = await View(fixture); Select(view, "favorites");
            Assert.Equal(label, Find<TextBlock>(view, "PageStatus").Text); Assert.Equal(firstPage, Cards(view).Length);
            Assert.Equal(count > 12, Find<Button>(view, "NextPageButton").IsEnabled);
            Assert.Equal(count == 0 ? Visibility.Visible : Visibility.Collapsed, Find<TextBlock>(view, "EmptyLibraryText").Visibility);
            if (count != 13) return;
            Click(view, "NextPageButton"); var last = Assert.Single(Cards(view));
            Star(last).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await view.SavePreferencesAsync();
            Assert.Equal("1 / 1", Find<TextBlock>(view, "PageStatus").Text); Assert.Equal(12, Cards(view).Length);
            Assert.False(Find<Button>(view, "NextPageButton").IsEnabled); Assert.False(Find<Button>(view, "PreviousPageButton").IsEnabled);
        });
    }

    [Fact]
    public async Task Picker_RecentIsUniqueMostRecentFirst_ExactlyOnePage_AndSurvivesNewSession()
    {
        using var fixture = new TempProject(); var entries = BlockLibrary.Definitions.Take(14).ToArray();
        await Sta(async () =>
        {
            var view = await View(fixture); foreach (var entry in entries) await view.RecordUseAsync(entry);
            await view.RecordUseAsync(entries[5]); Select(view, "recent");
            var expected = entries.Reverse().Where(entry => entry != entries[5]).Prepend(entries[5]).Take(12).Select(BlockLibraryPreferences.Key).ToArray();
            Assert.Equal(expected, Cards(view).Select(card => (string)Star(card).Tag));
            Assert.Equal("1 / 1", Find<TextBlock>(view, "PageStatus").Text); Assert.False(Find<Button>(view, "NextPageButton").IsEnabled);
            var reopened = await View(fixture); Select(reopened, "recent"); Assert.Equal(expected, Cards(reopened).Select(card => (string)Star(card).Tag));
        });
    }

    [Fact]
    public async Task Picker_UnavailableCommandsCanBeStarred_ButDoNotInsert_AndFailuresStayNonmodal()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var view = await View(fixture); view.Open(_ => false); Select(view, "terminals");
            int insertions = 0; view.BlockSelected += (_, _) => insertions++;
            var start = Cards(view).Single(card => Equals(card.Tag, ScriptNodeKind.Start));
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, insertions);
            Star(start).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await view.SavePreferencesAsync();
            Select(view, "favorites"); Assert.Equal(ScriptNodeKind.Start, Assert.Single(Cards(view)).Tag); Assert.Equal(0, insertions);
            string directoryAsFile = Path.Combine(fixture.Root, "cannot-write.json"); Directory.CreateDirectory(directoryAsFile);
            var failed = new BlockLibraryView(new(directoryAsFile)); await failed.LoadPreferencesAsync(); failed.Open(_ => true);
            await failed.RecordUseAsync(BlockLibrary.Definitions[0]);
            Assert.Contains("Не удалось сохранить", Find<TextBlock>(failed, "PreferenceStatus").Text);
            Select(failed, "recent"); Assert.Single(Cards(failed));
        });
    }

    [Fact]
    public async Task PreferenceStore_NormalizesObsoleteDuplicateAndOversizedLists_AndLeavesEditorSettingsUntouched()
    {
        using var fixture = new TempProject(); var store = Store(fixture);
        string editorSettings = Path.Combine(fixture.Root, "settings.json"); await File.WriteAllTextAsync(editorSettings, "untouched");
        var entries = BlockLibrary.Definitions.Take(14).Select(BlockLibraryPreferences.Key).ToArray();
        await File.WriteAllTextAsync(store.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new BlockLibraryPreferences
        { Favorites = ["removed/Unknown", entries[0], entries[0], entries[1]], Recent = entries.Concat(entries).Prepend("removed/Unknown").ToList() }));
        var loaded = await store.LoadAsync(); Assert.Equal(entries.Take(2), loaded.Favorites); Assert.Equal(entries.Take(12), loaded.Recent);
        await store.SaveAsync(loaded); Assert.Equal("untouched", await File.ReadAllTextAsync(editorSettings));
        Assert.Empty(Directory.GetFiles(fixture.Root, ".block-library-*.tmp"));
        await File.WriteAllTextAsync(store.SettingsPath, "{ malformed"); Assert.Empty((await store.LoadAsync()).Favorites);
        await File.WriteAllTextAsync(store.SettingsPath, "{\"Favorites\":null,\"Recent\":null}"); Assert.Empty((await store.LoadAsync()).Recent);
        await File.WriteAllTextAsync(store.SettingsPath, new string(' ', 1024 * 1024 + 1)); Assert.Empty((await store.LoadAsync()).Favorites);
        File.Delete(store.SettingsPath); Assert.Empty((await store.LoadAsync()).Recent);
    }

    [Fact]
    public async Task Picker_SmallMainWindowScrollsWholeFixedCatalog_WithoutShrinkingCards()
    {
        await Sta(() =>
        {
            var window = new MainWindow();
            try
            {
                Find<Border>(window, "BlockLibraryOverlay").Visibility = Visibility.Visible;
                var view = Find<BlockLibraryView>(window, "LibraryView"); view.Open(_ => true); Select(view, "visual");
                var content = (FrameworkElement)window.Content; content.Measure(new Size(1100, 700)); content.Arrange(new Rect(0, 0, 1100, 700)); content.UpdateLayout();
                var scroller = Find<ScrollViewer>(window, "BlockLibraryScroller");
                Assert.True(scroller.ScrollableWidth > 0); Assert.True(scroller.ScrollableHeight > 0);
                Assert.Equal(1202, view.ActualWidth); Assert.All(Cards(view), card => Assert.Equal(236, card.ActualWidth));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Picker_ShutdownFlushDoesNotLoadUnusedCatalog_AndPublishesImmediateStarAndRecentEdits()
    {
        using var fixture = new TempProject(); var store = Store(fixture);
        await Sta(async () =>
        {
            var unused = new BlockLibraryView(store);
            Assert.False(unused.HasPendingPreferenceSave); await unused.FlushPendingPreferencesAsync();
            Assert.False(File.Exists(store.SettingsPath));
            // A flush on an unused view leaves its store configurable, proving no lazy load was started.
            unused.ConfigurePreferenceStore(store);
            await store.SaveAsync(new() { Favorites = ["logic/Condition"] });
            var view = new BlockLibraryView(store); view.Open(_ => true);
            Select(view, "narrative"); Star(Cards(view).Single(card => Equals(card.Tag, ScriptNodeKind.Dialogue))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var use = view.RecordUseAsync(BlockLibrary.Definitions.Single(entry => entry.Kind == ScriptNodeKind.Random));
            await view.FlushPendingPreferencesAsync(); await use;
            Assert.False(view.HasPendingPreferenceSave);
            var persisted = await store.LoadAsync();
            Assert.Equal(new[] { "logic/Condition", "narrative/Dialogue" }, persisted.Favorites);
            Assert.Equal(new[] { "logic/Random" }, persisted.Recent);
        });
    }

    private static BlockLibraryPreferenceStore Store(TempProject fixture) => new(Path.Combine(fixture.Root, "block-library.json"));
    private static async Task<BlockLibraryView> View(TempProject fixture)
    {
        var view = new BlockLibraryView(Store(fixture)); await view.LoadPreferencesAsync(); view.Open(_ => true); return view;
    }
    private static T Find<T>(FrameworkElement view, string name) => (T)view.FindName(name);
    private static void Select(BlockLibraryView view, string id)
    {
        var groups = Find<ListBox>(view, "BlockGroups"); groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == id);
    }
    private static Button[] Cards(BlockLibraryView view) => Find<WrapPanel>(view, "BlockCards").Children.Cast<Button>().ToArray();
    private static Button Star(Button card) => ((Grid)card.Content).Children.OfType<Button>().Single();
    private static void Click(FrameworkElement view, string name) => Find<Button>(view, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Layout(FrameworkElement view) { view.Measure(new Size(1202, 652)); view.Arrange(new Rect(0, 0, 1202, 652)); view.UpdateLayout(); }
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
