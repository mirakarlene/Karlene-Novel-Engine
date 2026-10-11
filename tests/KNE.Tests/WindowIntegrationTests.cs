using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

[Trait("Category", "Integration")]
public sealed class WindowIntegrationTests
{
    [Fact]
    public async Task MainWindow_FileTableDisallowsPathEditingAndRowChanges()
    {
        await OnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                var grid = (DataGrid)window.FindName("FilesDataGrid");
                Assert.True(grid.IsReadOnly);
                Assert.False(grid.CanUserAddRows);
                Assert.False(grid.CanUserDeleteRows);
                return Task.CompletedTask;
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task MainWindow_ExternalChangeAndSearch_UpdateTreeAndTableTogether()
    {
        using var fixture = new TempProject();
        await OnStaAsync(async () =>
        {
            var window = new MainWindow();
            var grid = (DataGrid)window.FindName("FilesDataGrid");
            var itemsProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            var external = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var filtered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool searching = false;
            EventHandler changed = (_, _) =>
            {
                var files = grid.ItemsSource?.Cast<ProjectFile>().ToArray() ?? [];
                if (searching && files.Length == 1 && files[0].FileName == "forest.krl") filtered.TrySetResult();
                else if (!searching && files.Any(f => f.FileName == "forest.krl")) external.TrySetResult();
            };
            itemsProperty.AddValueChanged(grid, changed);
            try
            {
                var open = typeof(MainWindow).GetMethod("OpenProjectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                await (Task)open.Invoke(window, new object[] { fixture.ProjectPath, false })!;
                Assert.DoesNotContain(grid.ItemsSource.Cast<ProjectFile>(), f => f.FileName == "forest.krl");
                fixture.Write("game/scenes/forest.krl", "function main() d(nil, 'Forest') end");
                await external.Task.WaitAsync(TimeSpan.FromSeconds(10));
                searching = true;
                ((TextBox)window.FindName("FileSearch")).Text = "SCENES/FOREST";
                await filtered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var file = Assert.Single(grid.ItemsSource.Cast<ProjectFile>());
                Assert.Equal("forest.krl", file.FileName);
                var tree = (TreeView)window.FindName("ProjectFilesTree");
                var game = Assert.Single(tree.ItemsSource.Cast<ProjectTreeNode>());
                Assert.Equal("game", game.Name);
                var scenes = Assert.Single(game.Children);
                Assert.Equal("scenes", scenes.Name);
                Assert.Same(file, Assert.Single(scenes.Children).File);
            }
            finally { itemsProperty.RemoveValueChanged(grid, changed); window.Close(); }
        });
    }

    [Fact]
    public async Task GameWindow_LoadedEvent_RunsMainAndRendersDialogue()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        await OnStaAsync(async () =>
        {
            var window = new GameWindow(project, new GameSaveStore(project.Path, System.IO.Path.Combine(fixture.Root, "saves")));
            try
            {
                var input = (TextBox)window.FindName("CommandInput");
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                input.IsEnabledChanged += (_, _) => { if (input.IsEnabled) ready.TrySetResult(); };
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("Новелла с пробелами", window.Title);
                Assert.Equal("Ожидание продолжения", ((TextBlock)window.FindName("StatusText")).Text);
                Assert.Equal("Добро пожаловать! Это базовая сцена.", ((TextBlock)window.FindName("DialogueText")).Text);
                Assert.True(((Button)window.FindName("NextButton")).IsEnabled);
                Assert.Contains("Добро пожаловать! Это базовая сцена.", ((TextBlock)window.FindName("ConsoleOutput")).Text);
                Assert.True(input.IsEnabled);
                Assert.False(((Button)window.FindName("StopButton")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    private static Task OnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task GameWindow_RendersImageAndChoice_AndLoadsManualSave()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", """
            function main()
                background("game/backgrounds/test.png")
                show_character("game/backgrounds/test.png", "left")
                d("Алиса", "Начало")
                state.answer = choice("Куда?", {"Домой", "В парк"})
                hide_character()
                background(nil)
                d(nil, "Выбор " .. state.answer)
                d(nil, "Конец")
            end
            """);
        await OnStaAsync(async () =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(fixture.ProjectPath, "game", "backgrounds", "test.png"))) encoder.Save(file);
            var project = await fixture.LoadAsync();
            var saves = new GameSaveStore(project.Path, System.IO.Path.Combine(fixture.Root, "saves"));
            var window = new GameWindow(project, saves);
            var input = (TextBox)window.FindName("CommandInput");
            async Task ActAsync(Action action)
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DependencyPropertyChangedEventHandler handler = (_, e) => { if ((bool)e.NewValue) ready.TrySetResult(); };
                input.IsEnabledChanged += handler;
                try { action(); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { input.IsEnabledChanged -= handler; }
            }
            void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                await ActAsync(() => window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)));
                Assert.Equal(1, ((BitmapSource)((Image)window.FindName("BackgroundImage")).Source).PixelWidth);
                var character = (Image)window.FindName("CharacterImage");
                Assert.NotNull(character.Source);
                Assert.Equal(HorizontalAlignment.Left, character.HorizontalAlignment);
                await ActAsync(() => Click("NextButton"));
                var choices = ((StackPanel)window.FindName("ChoicePanel")).Children.OfType<Button>().ToArray();
                Assert.Equal(new[] { "Домой", "В парк" }, choices.Select(button => button.Content));
                await ActAsync(() => choices[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                Assert.Equal("Выбор 2", ((TextBlock)window.FindName("DialogueText")).Text);
                Assert.Null(character.Source);
                Assert.Null(((Image)window.FindName("BackgroundImage")).Source);
                await ActAsync(() => Click("SaveButton"));
                Assert.Equal(2, (await saves.LoadAsync("quick")).State.GetProperty("answer").GetInt32());
                await ActAsync(() => Click("NextButton"));
                Assert.Equal("Конец", ((TextBlock)window.FindName("DialogueText")).Text);
                await ActAsync(() => Click("LoadButton"));
                Assert.Equal("Выбор 2", ((TextBlock)window.FindName("DialogueText")).Text);
                Assert.True(((Button)window.FindName("NextButton")).IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task GameWindow_BrokenBackground_DoesNotPreventValidCharacterOrDialogue()
    {
        using var fixture = new TempProject();
        fixture.Write("game/backgrounds/broken.png", "not an image");
        fixture.Write("game/scenes/main.krl", """
            function main()
                background("game/backgrounds/broken.png")
                show_character("game/characters/valid.png", "right")
                d(nil, "Сцена работает")
            end
            """);
        await OnStaAsync(async () =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
            using (var file = System.IO.File.Create(System.IO.Path.Combine(fixture.ProjectPath, "game", "characters", "valid.png"))) encoder.Save(file);
            var project = await fixture.LoadAsync();
            var window = new GameWindow(project, new GameSaveStore(project.Path, System.IO.Path.Combine(fixture.Root, "saves")));
            var log = (TextBlock)window.FindName("ConsoleOutput");
            var loggedError = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var textProperty = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            EventHandler changed = (_, _) => { if (log.Text.Contains("Ошибка изображения:", StringComparison.Ordinal)) loggedError.TrySetResult(); };
            textProperty.AddValueChanged(log, changed);
            try
            {
                var input = (TextBox)window.FindName("CommandInput");
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                input.IsEnabledChanged += (_, e) => { if ((bool)e.NewValue) ready.TrySetResult(); };
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(((Image)window.FindName("BackgroundImage")).Source);
                var character = (Image)window.FindName("CharacterImage");
                Assert.NotNull(character.Source);
                Assert.Equal(HorizontalAlignment.Right, character.HorizontalAlignment);
                Assert.Equal("Сцена работает", ((TextBlock)window.FindName("DialogueText")).Text);
                await loggedError.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains("Ошибка изображения:", log.Text);
            }
            finally { textProperty.RemoveValueChanged(log, changed); window.Close(); }
        });
    }
}
