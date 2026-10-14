using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectCreationDialogTests
{
    [Fact]
    public async Task Chooser_HorizontalCards_ShowCodeAndConnectedBlocks_AndSwitchExclusivePreference()
    {
        await Sta(() =>
        {
            var dialog = new ProjectCreationDialog();
            try
            {
                var code = (RadioButton)dialog.FindName("CodeModeCard");
                var visual = (RadioButton)dialog.FindName("VisualModeCard");
                var content = (FrameworkElement)dialog.Content;
                content.Measure(new Size(860, 520)); content.Arrange(new Rect(0, 0, 860, 520)); content.UpdateLayout();
                Assert.Equal(ProjectAuthoringMode.Code, dialog.AuthoringMode);
                Assert.True(code.IsChecked); Assert.False(visual.IsChecked);
                Assert.Equal(Grid.GetRow(code), Grid.GetRow(visual));
                Assert.True(Grid.GetColumn(code) < Grid.GetColumn(visual));
                Point left = code.TranslatePoint(new Point(), content), right = visual.TranslatePoint(new Point(), content);
                Assert.Equal(left.Y, right.Y); Assert.True(left.X + code.ActualWidth < right.X);
                var example = (TextBlock)dialog.FindName("CodeExample");
                Assert.Contains("function main()", new TextRange(example.ContentStart, example.ContentEnd).Text);
                var graph = (Canvas)dialog.FindName("GraphExample");
                Assert.Equal(3, graph.Children.OfType<Border>().Count());
                Assert.Single(graph.Children.OfType<System.Windows.Shapes.Path>());
                Assert.Equal(4, graph.Children.OfType<System.Windows.Shapes.Ellipse>().Count());
                visual.IsChecked = true;
                Assert.Equal(ProjectAuthoringMode.Visual, dialog.AuthoringMode);
                Assert.False(code.IsChecked); Assert.True(visual.IsChecked);
                code.IsChecked = true;
                Assert.Equal(ProjectAuthoringMode.Code, dialog.AuthoringMode);
                Assert.False(visual.IsChecked);
                Capture(content);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public async Task VisualProject_MainEditor_StartsInGraphModeWithEditableBlocks()
    {
        using var fixture = new TempProject();
        string projectPath = ProjectCreator.CreateProject("Визуальный старт", fixture.Root, ProjectAuthoringMode.Visual);
        await Sta(async () =>
        {
            var editor = new ScriptEditor();
            await editor.OpenAsync(projectPath, Path.Combine(projectPath, "game", "scenes", "main.knegraph"));
            Assert.True(((RadioButton)editor.FindName("GraphMode")).IsChecked);
            Assert.Equal(Visibility.Visible, ((Grid)editor.FindName("GraphPanel")).Visibility);
            Assert.Equal(4, ((Canvas)editor.FindName("GraphCanvas")).Children.OfType<Border>().Count());
            Assert.Equal("main", ((TextBox)editor.FindName("FunctionNameInput")).Text);
            Assert.False(editor.Document!.IsDirty);
        });
    }

    private static void Capture(FrameworkElement content)
    {
        string? path = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER"); if (path == null) return;
        Directory.CreateDirectory(path);
        var bitmap = new RenderTargetBitmap(860, 520, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(247, 245, 252)), null, new Rect(0, 0, 860, 520));
        bitmap.Render(backdrop); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(path, "project-authoring-chooser.png")); encoder.Save(file);
    }

    private static Task Sta(Action action) => Sta(() => { action(); return Task.CompletedTask; });
    private static Task Sta(Func<Task> action)
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
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }
}
