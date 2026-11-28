using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KNE;

public partial class GameWindow
{
    private Rect? vmInitialArea;
    private bool initialClientSizeApplied;
    private Size? initialViewportSize;

    private void GameClient_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateEmbeddedConsoleHeight();
        if (initialViewportSize is { } size &&
            (Math.Abs(size.Width - e.NewSize.Width) >= 1 || Math.Abs(size.Height - e.NewSize.Height) >= 1))
            StartupViewportBackground.Visibility = Visibility.Collapsed;
        UpdateStartupBackgroundBounds(e.NewSize);
    }

    private void UpdateStartupBackgroundBounds(Size clientSize)
    {
        if (StartupViewportBackground == null) return;
        double scale = Math.Min(clientSize.Width / GameStage.Width, clientSize.Height / GameStage.Height);
        var margin = new Thickness();
        if (StartupViewportBackground.Visibility == Visibility.Visible && scale > 0)
        {
            // Use the same crop in the scene and in its surrounding area until the first resize.
            double horizontal = Math.Min(0, (GameStage.Width - clientSize.Width / scale) / 2);
            double vertical = Math.Min(0, (GameStage.Height - clientSize.Height / scale) / 2);
            margin = new Thickness(horizontal, vertical, horizontal, vertical);
        }
        DefaultBackground.Margin = DefaultBackgroundImage.Margin = BackgroundImage.Margin = margin;
    }

    internal void ShowVmTest(Window editor)
    {
        var area = WorkAreaFor(editor);
        vmInitialArea = area;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyVmBounds(area, Width, Height);
        Show();
    }

    private static Rect WorkAreaFor(Window editor)
    {
        var source = PresentationSource.FromVisual(editor);
        nint monitor = MonitorFromWindow(new WindowInteropHelper(editor).Handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        var area = SystemParameters.WorkArea;
        if (monitor != 0 && GetMonitorInfo(monitor, ref info) != 0 && source?.CompositionTarget is { } target)
        {
            var scale = target.TransformFromDevice;
            area = new Rect(scale.Transform(new Point(info.Work.Left, info.Work.Top)),
                scale.Transform(new Point(info.Work.Right, info.Work.Bottom)));
        }

        return area;
    }

    private void ApplyVmBounds(Rect workArea, double width, double height)
    {
        Rect bounds = CenterVmBounds(workArea, width, height);
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width; Height = bounds.Height;
        Left = bounds.Left; Top = bounds.Top;
    }

    private void ApplyInitialClientSize()
    {
        var settings = project.Engine.Window;
        if (!IsLoaded || WindowState != WindowState.Normal || fullscreen) return;

        var client = (FrameworkElement)Content;
        UpdateLayout();
        if (client.ActualWidth <= 0 || client.ActualHeight <= 0) return;
        double chromeWidth = Math.Max(0, ActualWidth - client.ActualWidth);
        double chromeHeight = Math.Max(0, ActualHeight - client.ActualHeight);
        Rect workArea = vmInitialArea ?? WorkAreaFor(this);
        double scale = Math.Min(1, Math.Min((workArea.Width - chromeWidth) / settings.Width,
            (workArea.Height - chromeHeight) / settings.Height));
        if (scale <= 0) return;

        double width = settings.Width * scale + chromeWidth;
        double height = settings.Height * scale + chromeHeight;
        if (vmInitialArea != null) ApplyVmBounds(workArea, width, height);
        else
        {
            MinWidth = Math.Min(MinWidth, width);
            MinHeight = Math.Min(MinHeight, height);
            Width = width;
            Height = height;
        }
        initialClientSizeApplied = true;
    }

    internal static Rect CenterVmBounds(Rect area, double width, double height)
    {
        width = Math.Min(width, area.Width); height = Math.Min(height, area.Height);
        return new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { internal uint Size; internal NativeRect Monitor, Work; internal uint Flags; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern int GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
