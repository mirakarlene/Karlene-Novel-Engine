using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace KNE;

/// <summary>A bounded welcome-only animation clock, detached when the visual leaves its window.</summary>
public abstract class WelcomeAnimation : FrameworkElement
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Window? owner;
    private long lastTick;
    internal bool IsAnimating => timer.IsEnabled;
    protected WelcomeAnimation()
    {
        IsHitTestVisible = false;
        timer.Tick += (_, _) => { long now = Stopwatch.GetTimestamp(); Step(Math.Min(.05, Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds)); lastTick = now; };
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this); if (owner != null) owner.StateChanged += OwnerStateChanged;
            SystemParameters.StaticPropertyChanged += PreferencesChanged;
            RefreshAnimation();
        };
        Unloaded += (_, _) =>
        {
            timer.Stop(); if (owner != null) owner.StateChanged -= OwnerStateChanged; owner = null;
            SystemParameters.StaticPropertyChanged -= PreferencesChanged;
        };
        IsVisibleChanged += (_, _) => RefreshAnimation(); SizeChanged += (_, _) => RefreshAnimation();
    }
    private void OwnerStateChanged(object? sender, EventArgs e) => RefreshAnimation();
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(RefreshAnimation); }
    private void RefreshAnimation()
    {
        if (IsLoaded && IsVisible && ActualWidth > 0 && ActualHeight > 0 && owner?.WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation)
        { if (!timer.IsEnabled) { lastTick = Stopwatch.GetTimestamp(); timer.Start(); } }
        else timer.Stop();
    }
    internal void Step(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Advance(Math.Min(.05, seconds)); InvalidateVisual();
    }
    protected abstract void Advance(double seconds);
}
