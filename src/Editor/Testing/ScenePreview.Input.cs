namespace KNE;

public partial class ScenePreview
{
    public void FocusPlayerInput() => PlayerInput.FocusAnswer();
    private async void PlayerInput_Submitted(object? sender, TextInputSubmission submission)
    {
        if (standalone) SceneInputRequested?.Invoke(this, submission);
        else { await RunAsync(reset: false, input: submission); if (preview?.NeedsInput == true) PlayerInput.FocusAnswer(); }
    }
}
