using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KNE;

/// <summary>Keeps break actions reachable when an owned modal dialog has disabled the main window natively.</summary>
internal sealed class BreakWindowSurface
{
    private readonly Window window;
    private readonly string title;
    private readonly object? content;
    private readonly UIElement? surface;
    private readonly bool enabled;
    private readonly Grid? wrapper;
    private readonly TextBlock countdown = new() { FontSize = 22, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    internal BreakWindowSurface(Window window, Action minimize, Action finish)
    {
        this.window = window; title = window.Title; content = window.Content; surface = content as UIElement; enabled = surface?.IsEnabled ?? true;
        if (!window.IsEnabled) return;
        window.Content = null;
        if (surface != null) surface.IsEnabled = false;
        wrapper = new Grid(); wrapper.Children.Add(surface ?? new ContentPresenter { Content = content, IsEnabled = false });
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(12) };
        body.Children.Add(new TextBlock { Text = "Пора сделать перерыв", FontSize = 24, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) }); body.Children.Add(countdown);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 20, 0, 0) };
        var hide = new Button { Content = "Свернуть окно", Padding = new(12, 8, 12, 8), Margin = new(0, 0, 8, 8) }; hide.Click += (_, _) => minimize(); buttons.Children.Add(hide);
        var end = new Button { Content = "Снять досрочно", Padding = new(12, 8, 12, 8), Margin = new(0, 0, 0, 8) }; end.Click += (_, _) => finish(); buttons.Children.Add(end); body.Children.Add(buttons);
        var overlay = new Border { Background = window.TryFindResource("Editor.Background") as Brush ?? window.Background ?? new SolidColorBrush(Color.FromRgb(23, 18, 34)), Child = body }; KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle); wrapper.Children.Add(overlay); window.Content = wrapper;
        if (window.IsActive) end.Focus();
    }
    internal void Update(string text) { window.Title = text + " · " + title; countdown.Text = text; }
    internal void Restore()
    {
        if (wrapper != null && ReferenceEquals(window.Content, wrapper))
        {
            window.Content = null; wrapper.Children.Clear(); if (surface != null) surface.IsEnabled = enabled; window.Content = content;
        }
        window.Title = title;
    }
}
