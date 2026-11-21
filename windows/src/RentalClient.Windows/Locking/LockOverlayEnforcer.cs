using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RentalClient.Windows.Locking;

public static class LockOverlayEnforcer
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    public static void BringToFront(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Topmost = true;
        window.Activate();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        CoverNearestMonitor(window, handle);
        SetForegroundWindow(handle);
    }

    private static void CoverNearestMonitor(Window window, IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var widthPx = info.rcMonitor.Right - info.rcMonitor.Left;
        var heightPx = info.rcMonitor.Bottom - info.rcMonitor.Top;
        if (widthPx <= 0 || heightPx <= 0)
        {
            return;
        }

        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(info.rcMonitor.Left, info.rcMonitor.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(info.rcMonitor.Right, info.rcMonitor.Bottom));

        window.Left = topLeft.X;
        window.Top = topLeft.Y;
        window.Width = Math.Max(1, bottomRight.X - topLeft.X);
        window.Height = Math.Max(1, bottomRight.Y - topLeft.Y);

        SetWindowPos(
            handle,
            HWND_TOPMOST,
            info.rcMonitor.Left,
            info.rcMonitor.Top,
            widthPx,
            heightPx,
            SWP_SHOWWINDOW);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
