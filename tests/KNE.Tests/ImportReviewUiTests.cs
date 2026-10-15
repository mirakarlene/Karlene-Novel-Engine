using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class ImportReviewUiTests
{
    [Fact]
    public async Task Review_ShowsOriginalBesideEditableColoredKrl_WithExperimentalWarning_AndPreservesEdits()
    {
        await Sta(() =>
        {
            const string original = "label start:\n    \"Привет\"";
            var result = ScriptImportConverter.FromRenPy(original);
            var window = new ImportReviewWindow(result, "test.rpy");
            try
            {
                var left = (TextBox)window.FindName("OriginalEditor");
                var right = (KrlCodeEditor)window.FindName("AdaptedEditor");
                Assert.True(left.IsReadOnly); Assert.Equal(original, left.Text);
                Assert.False(right.IsReadOnly); Assert.Equal(result.Code, right.Text);
                Assert.Equal(0, Grid.GetColumn(left)); Assert.Equal(2, Grid.GetColumn(right));
                Assert.Contains("Экспериментальная", ((TextBlock)window.FindName("ExperimentalNote")).Text);
                Assert.Contains("ошибки", ((TextBlock)window.FindName("ExperimentalNote")).Text);
                Assert.Contains("Python", ((TextBox)window.FindName("WarningsText")).Text);
                Assert.NotEmpty(right.Tokens);
                right.Text += "\n-- review edit";
                Assert.EndsWith("-- review edit", window.EditedCode);
                Assert.Equal(original, left.Text);
                Assert.DoesNotContain("-- review edit", result.Code);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Review_SaveRequiresNonemptyEditedCode_AndCancelDoesNotApproveImport()
    {
        await Sta(() =>
        {
            var result = ScriptImportConverter.FromRenPy("label start:\n    pass");
            var save = new ImportReviewWindow(result, "source.rpy");
            save.Loaded += (_, _) =>
            {
                var editor = (KrlCodeEditor)save.FindName("AdaptedEditor");
                var button = (Button)save.FindName("SaveImportButton");
                editor.Text = " ";
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Null(save.DialogResult);
                Assert.Contains("Добавьте код", ((TextBox)save.FindName("WarningsText")).Text);
                editor.Text = "function reviewed() end";
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            Assert.True(save.ShowDialog());
            Assert.Equal("function reviewed() end", save.EditedCode);
            var cancel = new ImportReviewWindow(result, "source.rpy");
            Assert.True(((Button)cancel.FindName("CancelImportButton")).IsCancel);
            cancel.Loaded += (_, _) => cancel.Close();
            Assert.False(cancel.ShowDialog());
            return Task.CompletedTask;
        });
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
