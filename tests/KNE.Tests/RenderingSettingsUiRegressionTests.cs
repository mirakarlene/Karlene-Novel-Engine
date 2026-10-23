using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class RenderingSettingsUiRegressionTests
{
    [Fact]
    public async Task FileCount_IsVisibleOnlyInTable_AndSubsystemOpeningOrReloadCannotLeakItIntoEditor()
    {
        using var fixture = new TempProject(); string parent = fixture.Write("parent.knegraph", VisualScript.Create().Serialize());
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await Pump(window);
                var status = Find<TextBlock>(window, "FileCountStatus"); var table = Find<DataGrid>(window, "FilesDataGrid");
                Assert.Equal(Visibility.Visible, status.Visibility); Assert.StartsWith("Файлов: ", status.Text); Assert.Equal(Visibility.Visible, Find<Grid>(window, "FileBrowserPanel").Visibility);
                await WorkspaceTabsTests.Invoke(window, "OpenScriptAsync", parent); await Pump(window);
                var tabs = Find<TabControl>(window, "DocumentTabs"); var editor = (ScriptEditor)((TabItem)tabs.Items[0]).Tag;
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                editor.AddBlock(ScriptNodeKind.Subsystem); string id = editor.Document!.Graph!.Nodes.Last().Id;
                editor.SetSubsystemPath(id, "game/scenes/nested/child.knegraph"); editor.OpenSubsystem(id);
                for (int i = 0; i < 500 && (tabs.Items.Count != 2 || Busy(window)); i++) { await Pump(window); await Task.Delay(10); }
                Assert.Equal(2, tabs.Items.Count); Assert.False(Busy(window));
                Assert.EndsWith("child.knegraph", ((ScriptEditor)((TabItem)tabs.SelectedItem).Tag).Document!.FilePath);
                Assert.True(File.Exists(Path.Combine(fixture.ProjectPath, "game", "scenes", "nested", "child.knegraph")));
                Assert.Equal(Visibility.Collapsed, status.Visibility); Assert.Equal(Visibility.Collapsed, table.Visibility);
                await WorkspaceTabsTests.Invoke(window, "OpenProjectAsync", fixture.ProjectPath, false); await Pump(window);
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                foreach (TabItem tab in tabs.Items.Cast<TabItem>().ToArray())
                {
                    var open = (ScriptEditor)tab.Tag; open.ConfirmSave = _ => MessageBoxResult.No;
                    await WorkspaceTabsTests.Invoke(window, "CloseEditorTabAsync", open);
                }
                await Pump(window); Assert.Equal(Visibility.Visible, status.Visibility); Assert.Equal(Visibility.Visible, Find<Grid>(window, "FileBrowserPanel").Visibility);
                Assert.Equal("Файлов: " + (await fixture.LoadAsync()).Files.Count, status.Text);
                typeof(MainWindow).GetMethod("BtnSettings_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Button(), new RoutedEventArgs()]);
                await Pump(window); Assert.Equal(Visibility.Collapsed, status.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task UnavailableStart_NeverAnimatesHoverAndResetsExistingReveal_WhileFavoriteRemainsUsable()
    {
        using var fixture = new TempProject(); var store = new BlockLibraryPreferenceStore(Path.Combine(fixture.Root, "preferences.json"));
        await WorkspaceTabsTests.Sta(async () =>
        {
            var view = new BlockLibraryView(store); await view.LoadPreferencesAsync(); view.Open(kind => kind != ScriptNodeKind.Start);
            var groups = Find<ListBox>(view, "BlockGroups"); groups.SelectedItem = groups.Items.Cast<BlockGroup>().Single(group => group.Id == "terminals");
            var card = Find<WrapPanel>(view, "BlockCards").Children.Cast<Button>().Single(button => Equals(button.Tag, ScriptNodeKind.Start));
            Assert.False(CategoryHover.GetAvailable(card)); Assert.True(card.IsEnabled); Assert.False(card.Focusable);
            int inserted = 0; view.BlockSelected += (_, _) => inserted++;
            card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
            await view.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Assert.False(card.HasAnimatedProperties); Assert.Equal(0, CategoryHover.GetProgress(card));
            card.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Assert.False(card.HasAnimatedProperties); Assert.Equal(0, CategoryHover.GetProgress(card));
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(0, inserted);
            var star = ((Grid)card.Content).Children.OfType<Button>().Single(); Assert.True(star.IsEnabled);
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await view.SavePreferencesAsync();
            Assert.Contains("terminals/Start", (await store.LoadAsync()).Favorites); Assert.Equal(0, inserted);
            // Availability can change while a card is already highlighted: stale fill must disappear immediately.
            CategoryHover.SetAvailable(card, true);
            card.BeginAnimation(CategoryHover.ProgressProperty, new DoubleAnimation(.8, 1, TimeSpan.FromSeconds(10)));
            Assert.True(card.HasAnimatedProperties);
            CategoryHover.SetAvailable(card, false);
            Assert.Equal(0, CategoryHover.GetProgress(card)); Assert.False(card.HasAnimatedProperties);
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) where T : class => WorkspaceTabsTests.Find<T>(owner, name);
    private static bool Busy(MainWindow window) => (bool)typeof(MainWindow).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static async Task Pump(MainWindow window) => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}
