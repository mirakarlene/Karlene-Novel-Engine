using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

public sealed record TextInputSubmission(string Text, bool Cancel = false);

/// <summary>A persistent editor for a suspended input frame. Ticks never replace the player's draft.</summary>
public sealed class TextInputPresenter : StackPanel
{
    public TextBox Answer { get; } = new() { Name = "PlayerAnswer", FontSize = 20, Padding = new Thickness(10, 7, 10, 7) };
    public PasswordBox Password { get; } = new() { Name = "PlayerPassword", FontSize = 20, Padding = new Thickness(10, 7, 10, 7) };
    public Button SubmitButton { get; } = new() { Name = "SubmitButton", Content = "Подтвердить", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(0, 0, 8, 0) };
    public Button CancelButton { get; } = new() { Name = "CancelButton", Content = "Назад", Padding = new Thickness(16, 7, 16, 7) };
    public TextBlock Placeholder { get; } = new() { Foreground = Brushes.Gray, Margin = new Thickness(12, 9, 12, 0), IsHitTestVisible = false };
    public TextBlock Feedback { get; } = new() { Foreground = Brushes.LightCoral, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
    private readonly TextBlock prompt = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock maskHint = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0) };
    private TextInputSettings? settings;
    private long presentation;
    private FrameIdentity? identity;
    private bool changing;
    public event EventHandler<TextInputSubmission>? Submitted;

    public TextInputPresenter()
    {
        Visibility = Visibility.Collapsed; Margin = new Thickness(0, 12, 0, 0);
        Children.Add(prompt);
        var field = new Grid(); field.Children.Add(Answer); field.Children.Add(Password); field.Children.Add(Placeholder); Children.Add(field);
        Children.Add(maskHint); Children.Add(Feedback);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(SubmitButton); buttons.Children.Add(CancelButton); Children.Add(buttons);
        Answer.TextChanged += (_, _) => NormalizeDraft(); Password.PasswordChanged += (_, _) => NormalizeDraft();
        Answer.IsKeyboardFocusWithinChanged += (_, _) => UpdatePlaceholder(); Password.IsKeyboardFocusWithinChanged += (_, _) => UpdatePlaceholder();
        Answer.GotFocus += (_, _) => Placeholder.Visibility = Visibility.Collapsed;
        Password.GotFocus += (_, _) => Placeholder.Visibility = Visibility.Collapsed;
        Answer.LostFocus += (_, _) => UpdatePlaceholder(); Password.LostFocus += (_, _) => UpdatePlaceholder();
        SubmitButton.Click += (_, e) => { Submit(); e.Handled = true; };
        CancelButton.Click += (_, e) => { Cancel(); e.Handled = true; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        Loaded += (_, _) => FocusAnswer();
    }

    public string Draft => settings?.IsPassword == true ? Password.Password : Answer.Text;
    public bool CanCancel => settings is { BranchOnResult: true, HasCancelOutput: true };

    public void Display(StoryFrame frame, bool interactive = true)
    {
        if (frame.Kind != "input" || frame.Input is null) { Reset(); return; }
        bool fresh = settings is null || presentation != frame.PresentationId || identity != frame.Identity || settings != frame.Input;
        settings = frame.Input; presentation = frame.PresentationId; identity = frame.Identity;
        Visibility = Visibility.Visible; IsEnabled = interactive;
        prompt.Text = settings.Prompt; Placeholder.Text = settings.Placeholder; Feedback.Text = frame.InputFeedback;
        maskHint.Text = settings.InputMask.Length == 0 ? "" : "Формат: " + settings.InputMask;
        maskHint.Visibility = settings.InputMask.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Answer.Visibility = settings.IsPassword ? Visibility.Collapsed : Visibility.Visible;
        Password.Visibility = settings.IsPassword ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = CanCancel ? Visibility.Visible : Visibility.Collapsed;
        if (fresh)
        {
            changing = true; Answer.Clear(); Password.Clear(); changing = false;
            if (interactive && settings.AutoFocus) Dispatcher.BeginInvoke(new Action(FocusAnswer), System.Windows.Threading.DispatcherPriority.Input);
        }
        UpdatePlaceholder();
    }

    public void Reset()
    {
        settings = null; changing = true; Answer.Clear(); Password.Clear(); changing = false;
        Visibility = Visibility.Collapsed; Feedback.Text = "";
    }

    public void Submit()
    {
        if (settings is not null && IsEnabled) Submitted?.Invoke(this, new(Draft));
    }
    public void Cancel()
    {
        if (CanCancel && IsEnabled) Submitted?.Invoke(this, new("", true));
    }
    public void FocusAnswer()
    {
        if (settings?.AutoFocus != true || !IsEnabled || !IsVisible) return;
        if (settings.IsPassword) Password.Focus(); else Answer.Focus();
    }
    public void ApplyAppearance(TextBlock dialogue)
    {
        prompt.Foreground = dialogue.Foreground;
        prompt.FontFamily = Answer.FontFamily = Password.FontFamily = dialogue.FontFamily;
        prompt.FontSize = Answer.FontSize = Password.FontSize = dialogue.FontSize;
    }
    private void UpdatePlaceholder() => Placeholder.Visibility = Draft.Length == 0 && !Answer.IsKeyboardFocusWithin && !Password.IsKeyboardFocusWithin && !Answer.IsFocused && !Password.IsFocused ? Visibility.Visible : Visibility.Collapsed;
    private void NormalizeDraft()
    {
        if (changing || settings is null) return;
        string current = Draft;
        string formatted = settings.InputMask.Length == 0 ? current : FormatMask(current, settings.InputMask);
        int caretBefore = Answer.CaretIndex;
        int[] formattedStarts = StringInfo.ParseCombiningCharacters(formatted);
        int keep = Math.Min(formattedStarts.Length, settings.MaximumLength);
        while (keep > 0 && (keep < formattedStarts.Length ? formattedStarts[keep] : formatted.Length) > 8192) keep--;
        int length = keep < formattedStarts.Length ? formattedStarts[keep] : formatted.Length;
        if (length < formatted.Length) formatted = formatted[..length];
        if (formatted != current)
        {
            changing = true;
            if (settings.IsPassword) Password.Password = formatted;
            else { Answer.Text = formatted; Answer.CaretIndex = Math.Min(formatted.Length, caretBefore + Math.Max(0, formatted.Length - current.Length)); }
            changing = false;
        }
        UpdatePlaceholder();
    }

    private static string FormatMask(string value, string mask)
    {
        var characters = new List<string>(); var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) characters.Add(enumerator.GetTextElement());
        var result = new System.Text.StringBuilder(); int index = 0; bool escaped = false;
        var elements = StringInfo.GetTextElementEnumerator(mask);
        while (elements.MoveNext())
        {
            string token = elements.GetTextElement();
            if (!escaped && token == "\\") { escaped = true; continue; }
            bool slot = !escaped && token is "0" or "L" or "A" or "&"; escaped = false;
            if (index >= characters.Count) break;
            if (!slot)
            {
                result.Append(token); if (characters[index] == token) index++; continue;
            }
            while (index < characters.Count)
            {
                string character = characters[index++];
                bool asciiLetter = character.Length == 1 && char.IsAsciiLetter(character[0]);
                bool digit = character.Length == 1 && char.IsAsciiDigit(character[0]);
                if (token == "&" || token == "L" && asciiLetter || token == "0" && digit || token == "A" && (asciiLetter || digit))
                { result.Append(character); break; }
            }
        }
        return result.ToString();
    }
}
