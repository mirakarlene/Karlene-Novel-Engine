using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KNE;

// The hook belongs to the WPF dispatcher thread and only examines Escape for the active game window.
internal sealed class TestPauseShortcut : SafeHandleZeroOrMinusOneIsInvalid
{
    private const int LowLevelKeyboardHook = 13;
    private const int Escape = 0x1B;
    private const int Control = 0x11;
    private const int Shift = 0x10;
    private const int Alt = 0x12;
    private const int KeyDown = 0x0100;
    private const int KeyUp = 0x0101;
    private readonly HookProcedure callback;
    private bool caughtKey;

    internal TestPauseShortcut(nint window, Action resume) : base(ownsHandle: true)
    {
        callback = (code, message, data) =>
        {
            if (code == 0 && GetForegroundWindow() == window && Marshal.ReadInt32(data) == Escape)
            {
                if (message == KeyDown && GetAsyncKeyState(Control) < 0 && GetAsyncKeyState(Shift) < 0 && GetAsyncKeyState(Alt) >= 0)
                {
                    caughtKey = true;
                    return 1;
                }
                if (message == KeyUp && caughtKey)
                {
                    caughtKey = false;
                    resume();
                    return 1;
                }
            }
            return CallNextHookEx(0, code, message, data);
        };
        SetHandle(SetWindowsHookExW(LowLevelKeyboardHook, callback, GetModuleHandleW(0), 0));
        if (IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    protected override bool ReleaseHandle()
    {
        bool released = UnhookWindowsHookEx(handle) != 0;
        GC.KeepAlive(callback);
        return released;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProcedure(int code, nuint message, nint data);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowsHookExW(int hook, HookProcedure procedure, nint module, uint thread);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetModuleHandleW(nint moduleName);
}
