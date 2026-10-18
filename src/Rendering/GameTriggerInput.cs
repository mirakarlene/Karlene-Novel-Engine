using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

/// <summary>Translates WPF input into scale-independent, replayable game events.</summary>
internal sealed class GameTriggerInput
{
    private static readonly DependencyProperty TriggerButtonIdProperty = DependencyProperty.RegisterAttached("TriggerButtonId", typeof(string), typeof(GameTriggerInput));
    internal static void SetButtonId(FrameworkElement view, string id) => view.SetValue(TriggerButtonIdProperty, id);
    private readonly HashSet<Key> held = [];
    internal TriggerSignal? KeyDown(KeyEventArgs args)
    {
        Key key = args.Key == Key.System ? args.SystemKey : args.Key;
        held.Add(key);
        if (args.IsRepeat || IsEditing(args.OriginalSource as DependencyObject)) return null;
        var keys = held.Select(key => key.ToString()).ToList();
        foreach (var modifier in new[] { (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win") })
            if (Keyboard.Modifiers.HasFlag(modifier.Item1)) keys.Add(modifier.Item2);
        try { return new("key", TriggerSettings.NormalizeKeys(string.Join('+', keys))); }
        catch (ArgumentException) { return null; }
    }
    internal void KeyUp(KeyEventArgs args) => held.Remove(args.Key == Key.System ? args.SystemKey : args.Key);
    internal void Reset() => held.Clear();
    internal static bool IsEditing(DependencyObject? source)
    {
        for (var current = source; current != null; current = Parent(current))
            if (current is TextBoxBase or PasswordBox || current is ComboBox { IsEditable: true }) return true;
        return false;
    }
    internal static Button? FindButton(DependencyObject? source)
    {
        for (var current = source; current != null; current = Parent(current)) if (current is Button button) return button;
        return null;
    }
    private static DependencyObject? Parent(DependencyObject current) => current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
    internal static TriggerSignal Hotspot(Point position, Size size) => new("hotspot", X: size.Width > 0 ? position.X * 1280 / size.Width : position.X,
        Y: size.Height > 0 ? position.Y * 720 / size.Height : position.Y);
    internal static TriggerSignal PointerSignal(StoryFrame story, Button? button, ScreenLayouts screens, Point position, Size size)
    {
        string? id = ButtonId(button, screens);
        var click = id == null ? null : new TriggerSignal("button", Button: id);
        return click != null && story.Triggers.Any(trigger => trigger.Matches(click)) ? click : Hotspot(position, size);
    }
    internal static string? ButtonId(Button? button, ScreenLayouts screens)
    {
        if (button == null) return null;
        if (button.GetValue(TriggerButtonIdProperty) is string registered) return registered;
        if (button.Tag is string id && id.StartsWith("screen:", StringComparison.Ordinal)) return id;
        if (button.Tag is string target && target != "dialogue" && GameplayUiStyle.IsTarget(target)) return target;
        if (button.Tag is ScreenElement element)
        {
            var screen = screens.Screens.FirstOrDefault(screen => screen.Elements.Contains(element));
            if (screen != null) return "screen:" + screen.Name + "/" + element.Id;
        }
        if (button.Tag is int option) return "choice:" + option;
        return button.Name switch
        {
            "NextButton" => "next", "SaveButton" => "save", "LoadButton" => "load", "LoadAutoButton" => "auto_save", "PauseButton" => "pause",
            "SubmitButton" => "input_submit", "CancelButton" => "input_cancel", _ => null
        };
    }
}
