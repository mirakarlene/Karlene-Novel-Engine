using System.Windows;
using System.Windows.Input;

namespace KNE;

public partial class GameWindow
{
    private string lastScriptInput = "";

    private void RenderScriptHud()
    {
        var visuals = runtime.Visuals;
        bool show = ready && !startupFailed && MenuOverlay.Visibility != Visibility.Visible;
        ScriptHud.Visibility = show && (visuals.Watermark != null || visuals.InputVisible || visuals.GlobalTimer?.Visible == true)
            ? Visibility.Visible : Visibility.Collapsed;
        if (!visuals.InputVisible) lastScriptInput = "";
        if (ScriptHud.Visibility != Visibility.Visible) return;
        InlineTextPresentation.SetFonts(this, project.Path, visuals.Fonts);
        LastInputText.Visibility = visuals.InputVisible ? Visibility.Visible : Visibility.Collapsed;
        LastInputText.Text = lastScriptInput;
        GlobalTimerText.Visibility = visuals.GlobalTimer?.Visible == true ? Visibility.Visible : Visibility.Collapsed;
        RenderGlobalTimer();
        WatermarkText.Visibility = visuals.Watermark == null ? Visibility.Collapsed : Visibility.Visible;
        if (visuals.Watermark is not { } watermark) return;
        WatermarkText.Opacity = watermark.Opacity;
        WatermarkText.HorizontalAlignment = watermark.Position.EndsWith("left", StringComparison.Ordinal) ? HorizontalAlignment.Left :
            watermark.Position.EndsWith("right", StringComparison.Ordinal) ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        WatermarkText.VerticalAlignment = watermark.Position.StartsWith("top", StringComparison.Ordinal) ? VerticalAlignment.Top :
            watermark.Position.StartsWith("bottom", StringComparison.Ordinal) ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        InlineTextPresentation.Apply(WatermarkText, watermark.Text);
    }

    private void RenderGlobalTimer()
    {
        if (ScriptHud.Visibility != Visibility.Visible || runtime.Visuals.GlobalTimer is not { Visible: true } timer) return;
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, ReadPlayTime() - timer.StartSeconds));
        string text = timer.Format == "mm:ss:fff"
            ? FormattableString.Invariant($"{(long)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}:{elapsed.Milliseconds:D3}")
            : FormattableString.Invariant($"{(long)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}");
        if (GlobalTimerText.Text != text) GlobalTimerText.Text = text;
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (DebugOverlay.IsMouseOver) return;
        RecordScriptInput(e.ChangedButton switch
        {
            MouseButton.Left => "ЛКМ", MouseButton.Right => "ПКМ", MouseButton.Middle => "СКМ",
            MouseButton.XButton1 => "Мышь 4", _ => "Мышь 5"
        });
    }

    private void RecordScriptInput(string input)
    {
        if (runtime == null || !ready || systemFrozen || startupFailed || MenuOverlay.Visibility == Visibility.Visible ||
            !runtime.Visuals.InputVisible) return;
        lastScriptInput = input;
        LastInputText.Text = input;
    }
}
