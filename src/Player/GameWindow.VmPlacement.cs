using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KNE;

public partial class GameWindow
{
    private Rect? vmInitialArea;

    internal void ShowVmTest(Window editor)
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
        vmInitialArea = area;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyVmBounds(area, Width, Height);
        Show();
    }

    private void ApplyVmBounds(Rect workArea, double width, double height)
    {
        Rect bounds = CenterVmBounds(workArea, width, height);
        MinWidth = Math.Min(MinWidth, bounds.Width);
        MinHeight = Math.Min(MinHeight, bounds.Height);
        Width = bounds.Width; Height = bounds.Height;
        Left = bounds.Left; Top = bounds.Top;
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
