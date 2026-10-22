using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace KNE;

/// <summary>A disabled choice keeps its original index and exposes a hover explanation.</summary>
public sealed class ChoiceOptionButton : Button
{
    private readonly ChoiceOptionState option;
    private readonly TextBlock label;
    internal ChoiceOptionButton(ChoiceOptionState option, int index)
    {
        this.option = option;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Tag = index; IsEnabled = option.IsEnabled;
        label = new TextBlock { Text = option.Text, TextWrapping = TextWrapping.Wrap };
        Content = label;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Padding = new Thickness(12, 8, 12, 8); Margin = new Thickness(0, 0, 0, 8);
        if (!option.IsEnabled && option.Hint.Length > 0)
        {
            ToolTip = new ToolTip { Content = new TextBlock { Text = option.Hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
                Placement = option.HintPlacement == "overlay" ? PlacementMode.Center : PlacementMode.Right,
                PlacementTarget = this, StaysOpen = true };
            ToolTipService.SetShowOnDisabled(this, true);
            ToolTipService.SetInitialShowDelay(this, 150);
        }
        ApplyEffect(0, false);
    }

    /// <summary>Whether this answer can advance the story.</summary>
    public bool IsAvailable => option.IsEnabled;
    internal bool Animated => !option.IsEnabled && option.Effect is "glitch" or "hide" or "scramble";
    internal bool IsAnimating { get; set; }
    protected override void OnClick() { if (IsAvailable && IsEnabled) base.OnClick(); }

    internal void ApplyEffect(double seconds, bool animate)
    {
        IsAnimating = animate && Animated;
        label.Text = option.Text; label.TextDecorations = null;
        label.Opacity = 1; label.RenderTransform = Transform.Identity;
        if (option.IsEnabled) return;
        switch (option.Effect)
        {
            case "dim": label.Opacity = .45; break;
            case "strike": label.TextDecorations = TextDecorations.Strikethrough; break;
            case "glitch":
                if (animate) label.RenderTransform = new TranslateTransform(Math.Sin(seconds * 57) * 3, Math.Cos(seconds * 43));
                label.Opacity = animate ? .65 + .3 * Math.Abs(Math.Sin(seconds * 31)) : .7;
                break;
            case "hide": label.Text = Conceal(option.Text, animate && Math.Sin(seconds * 2.5) > .65, animate ? seconds : 0, false); break;
            case "scramble": label.Text = Conceal(option.Text, animate && Math.Sin(seconds * 2) > .4, animate ? seconds : 0, true); break;
        }
    }

    private static string Conceal(string text, bool readable, double seconds, bool scramble)
    {
        if (readable) return text;
        const string alphabet = "#%?@$&*ЖФΨΩ";
        var result = new System.Text.StringBuilder(); var elements = StringInfo.GetTextElementEnumerator(text); int index = 0;
        while (elements.MoveNext())
        {
            string element = (string)elements.Current;
            result.Append(string.IsNullOrWhiteSpace(element) ? element : scramble ? alphabet[(index * 7 + (int)(seconds * 12)) % alphabet.Length] : "•"); index++;
        }
        return result.ToString();
    }
}

/// <summary>Displays authoritative game time; this control never makes a choice or owns a countdown.</summary>
public sealed class ChoiceTimerPresenter : Border
{
    internal ProgressBar Bar { get; } = new() { Minimum = 0, Maximum = 1, Height = 8, IsHitTestVisible = false };
    internal TextBlock Number { get; } = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
    /// <summary>Creates an unobtrusive timer anchored to the top edge of its scene.</summary>
    public ChoiceTimerPresenter()
    {
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        VerticalAlignment = VerticalAlignment.Top; HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(24, 16, 24, 0); Padding = new Thickness(10, 6, 10, 6);
        Background = new SolidColorBrush(Color.FromArgb(210, 25, 17, 36)); CornerRadius = new CornerRadius(8);
        IsHitTestVisible = false; Visibility = Visibility.Collapsed;
        var grid = new Grid(); grid.Children.Add(Bar); grid.Children.Add(Number); Child = grid;
    }
    internal void Display(ChoiceTimerState? timer, Brush accent)
    {
        Visibility = timer is null ? Visibility.Collapsed : Visibility.Visible;
        if (timer is null) return;
        Bar.Foreground = Number.Foreground = accent;
        Bar.Value = Math.Clamp(timer.RemainingSeconds / timer.Seconds, 0, 1);
        Bar.Visibility = timer.Display == "bar" ? Visibility.Visible : Visibility.Collapsed;
        Number.Visibility = timer.Display == "numeric" ? Visibility.Visible : Visibility.Collapsed;
        Number.Text = Math.Max(0, timer.RemainingSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " с";
    }
}

/// <summary>Shared choice lifecycle for the player, scene test and frozen frame preview.</summary>
internal sealed class ChoicePresentation
{
    private readonly StackPanel panel;
    private readonly ChoiceTimerPresenter timer;
    private readonly Action<Button> configure;
    private readonly RoutedEventHandler clicked;
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private StoryFrame? rendered;
    private bool animate;
    private double phase;
    internal ChoicePresentation(StackPanel panel, ChoiceTimerPresenter timer, Action<Button> configure, RoutedEventHandler clicked)
    {
        this.panel = panel; this.timer = timer; this.configure = configure; this.clicked = clicked;
        animation.Tick += (_, _) => { phase += animation.Interval.TotalSeconds; AdvanceAnimation(phase); };
        panel.IsVisibleChanged += (_, _) => UpdateClock();
        panel.Unloaded += (_, _) => animation.Stop();
        panel.Loaded += (_, _) => UpdateClock();
    }

    internal void Display(StoryFrame story, bool animate, Brush accent)
    {
        this.animate = animate;
        bool same = rendered is { } before && before.PresentationId == story.PresentationId && before.Kind == story.Kind &&
            before.Options.SequenceEqual(story.Options) && EqualOptions(before.ChoiceOptions, story.ChoiceOptions);
        if (!same)
        {
            panel.Children.Clear(); phase = 0;
            if (story.Kind == "choice")
                for (int index = 0; index < story.Options.Count; index++)
                {
                    var state = story.ChoiceOptions is { } states && index < states.Count ? states[index] : new ChoiceOptionState(story.Options[index], true);
                    var button = new ChoiceOptionButton(state, index + 1); configure(button);
                    button.Click += (sender, args) => { if (button.IsAvailable && button.IsEnabled && panel.IsEnabled && panel.IsHitTestVisible) clicked(sender, args); else args.Handled = true; };
                    panel.Children.Add(button);
                }
        }
        rendered = story;
        panel.Visibility = story.Kind == "choice" ? Visibility.Visible : Visibility.Collapsed;
        timer.Display(story.Kind == "choice" ? story.ChoiceTimer : null, accent);
        AdvanceAnimation(phase); UpdateClock();
    }
    private static bool EqualOptions(IReadOnlyList<ChoiceOptionState>? left, IReadOnlyList<ChoiceOptionState>? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.SequenceEqual(right);
    internal void AdvanceAnimation(double seconds)
    {
        foreach (var option in panel.Children.OfType<ChoiceOptionButton>()) option.ApplyEffect(seconds, animate);
    }
    private void UpdateClock()
    {
        if (animate && panel.IsLoaded && panel.IsVisible && panel.Children.OfType<ChoiceOptionButton>().Any(button => button.Animated)) animation.Start();
        else animation.Stop();
    }
    internal bool IsAnimating => animation.IsEnabled;
    internal void Stop()
    {
        animate = false; animation.Stop();
        foreach (var button in panel.Children.OfType<ChoiceOptionButton>()) button.IsAnimating = false;
    }
    internal void Reset() { Stop(); rendered = null; panel.Children.Clear(); timer.Visibility = Visibility.Collapsed; }
}
