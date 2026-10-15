using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ScenePreviewUiTests
{
    [Fact]
    public async Task Preview_CoalescesSelections_RendersLatestUnsavedSegment_AndStepResumesWorker()
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync();
        await Sta(async () =>
        {
            var view = new ScenePreview();
            try
            {
                var latest = new EditorSelection(project.MainScenePath, "function main()\n d(nil,'Первый')\n d(nil,'Последний')\nend", 2, null, false, "main");
                view.SelectionProvider = () => latest; view.Open(project);
                var status = Find<TextBlock>(view, "PreviewStatus");
                var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                EventHandler changed = (_, _) =>
                {
                    if (status.Text.Contains(":3 ·")) reached.TrySetResult();
                };
                descriptor.AddValueChanged(status, changed);
                try
                {
                    view.RequestSelection(); latest = latest with { Line = 3 }; view.RequestSelection();
                    await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal("Последний", Find<TextBlock>(view, "DialogueText").Text);
                    Assert.True(Find<Button>(view, "StepButton").IsEnabled);
                    Assert.DoesNotContain("Последний", File.ReadAllText(project.MainScenePath));
                }
                finally { descriptor.RemoveValueChanged(status, changed); }
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var step = Find<Button>(view, "StepButton");
                var dialogue = Find<TextBlock>(view, "DialogueText");
                EventHandler completed = (_, _) => { if (dialogue.Text == "История завершена.") stopped.TrySetResult(); };
                descriptor.AddValueChanged(dialogue, completed);
                try
                {
                    step.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal("История завершена.", dialogue.Text); Assert.False(step.IsEnabled);
                }
                finally { descriptor.RemoveValueChanged(dialogue, completed); }
            }
            finally { view.Stop(); }
        });
    }

    [Fact]
    public async Task Workspace_PreservesFileSidebar_AndGivesFullHeightToEditorWithoutEmbeddedPreview()
    {
        using var fixture = new TempProject();
        await Sta(async () =>
        {
            var window = new MainWindow();
            try
            {
                Assert.True(window.Width >= 1400); Assert.True(window.Height >= 900);
                await InvokeAsync(window, "OpenProjectAsync", fixture.ProjectPath, false);
                await InvokeAsync(window, "OpenScriptAsync", Path.Combine(fixture.ProjectPath, "game/scenes/main.krl"));
                var tree = Find<TreeView>(window, "ProjectFilesTree");
                var workspace = Find<Grid>(window, "EditorWorkspace");
                var editor = Find<ScriptEditor>(window, "SceneEditor");
                Assert.Null(window.FindName("EmbeddedPreview"));
                Assert.NotEmpty(tree.Items); Assert.Equal(0, Grid.GetColumn(tree)); Assert.Equal(2, Grid.GetColumn(workspace));
                Assert.Equal(Visibility.Visible, workspace.Visibility); Assert.Equal(Visibility.Collapsed, Find<DataGrid>(window, "FilesDataGrid").Visibility);
                Assert.Same(workspace, editor.Parent); Assert.Single(workspace.Children.Cast<UIElement>());
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1460, 900)); content.Arrange(new Rect(0, 0, 1460, 900)); content.UpdateLayout();
                Assert.True(tree.ActualWidth >= 200); Assert.True(editor.ActualHeight >= 500);
                Capture(content, "editor-workspace.png");
                typeof(MainWindow).GetMethod("BtnFileList_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, new RoutedEventArgs()]);
                Assert.Equal(Visibility.Collapsed, workspace.Visibility); Assert.Equal(Visibility.Visible, Find<DataGrid>(window, "FilesDataGrid").Visibility);
                Assert.NotEmpty(tree.Items);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Preview_RendersThemeResourcesCharacterAndDialogue_AnimationRequiresSelectedBlockFlag()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/theme.json", "{\"character_width\":320,\"dialogue\":{\"height\":220,\"margin\":20,\"font_size\":28}}");
        string image = fixture.Write("actor.png", "");
        File.WriteAllBytes(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQAAAABJRU5ErkJggg=="));
        var project = await fixture.LoadAsync();
        await using var worker = new EditorPreview(project, _ => { });
        await worker.SeekAsync(new(project.MainScenePath, "function main()\n show_character('actor.png','right')\n d('Алиса','Тема и персонаж')\nend", 3, null, false, "main"));
        await Sta(() =>
        {
            var view = new ScenePreview();
            try
            {
                view.Open(project);
                typeof(ScenePreview).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [worker, false]);
                Assert.Equal(320, Find<Image>(view, "CharacterImage").Width);
                Assert.NotNull(Find<Image>(view, "CharacterImage").Source);
                Assert.Equal(HorizontalAlignment.Right, Find<Image>(view, "CharacterImage").HorizontalAlignment);
                Assert.Equal("Алиса", Find<TextBlock>(view, "SpeakerText").Text);
                Assert.Equal("Тема и персонаж", Find<TextBlock>(view, "DialogueText").Text);
                Assert.Equal(28, Find<TextBlock>(view, "DialogueText").FontSize);
                Assert.Equal(220, Find<Border>(view, "DialoguePanel").Height);
                Assert.Equal(new Thickness(20), Find<Border>(view, "DialoguePanel").Margin);
                var backdrop = Find<AnimatedBackground>(view, "AnimatedBackdrop");
                Assert.False(backdrop.HasAnimatedProperties);
                typeof(ScenePreview).GetMethod("RenderSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [worker, true]);
                Assert.True(backdrop.HasAnimatedProperties);
                view.Measure(new Size(1100, 360)); view.Arrange(new Rect(0, 0, 1100, 360)); view.UpdateLayout();
                Capture(view, "scene-preview.png");
                view.Stop(); Assert.False(backdrop.HasAnimatedProperties);
            }
            finally { view.Stop(); }
            return Task.CompletedTask;
        });
    }

    private static T Find<T>(FrameworkElement owner, string name) => (T)owner.FindName(name);
    private static Task InvokeAsync(object target, string name, params object[] arguments) =>
        (Task)target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
    private static void Capture(FrameworkElement view, string file)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER");
        if (directory == null) return;
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
    }
    private static Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
