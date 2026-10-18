namespace KNE;

public partial class GameWindow
{
    private async void PlayerInput_Submitted(object? sender, TextInputSubmission submission)
    {
        if (runtime.Story.Kind != "input" || execution != null || MenuOverlay.Visibility == System.Windows.Visibility.Visible) return;
        if (await DispatchTriggerAsync(new("button", Button: submission.Cancel ? "input_cancel" : "input_submit"))) return;
        await ExecuteAsync(() => runtime.SubmitInputAsync(submission.Text, submission.Cancel, execution!.Token), operation: "Ввод ответа игрока");
        if (runtime.Story.Kind == "input") PlayerInput.FocusAnswer();
    }
}
