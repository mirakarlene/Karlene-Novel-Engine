using System.Windows;

namespace KNE;

/// <summary>A visible-only welcome animation driven by the configured frame cap and composition policy.</summary>
public abstract class WelcomeAnimation : FrameworkElement
{
    private readonly GraphicsFrameClock clock;
    internal bool IsAnimating => clock.Running;
    protected WelcomeAnimation()
    {
        IsHitTestVisible = false;
        clock = new(this, Step); SizeChanged += (_, _) => clock.Refresh();
    }
    internal void Step(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        Advance(Math.Min(.05, seconds)); InvalidateVisual();
    }
    protected abstract void Advance(double seconds);
}
