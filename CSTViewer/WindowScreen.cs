using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CSTViewer;

internal static class WindowScreen
{
    public static IntPtr MonitorAtCenter(Window window, bool nearestIfOutside = true)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetWindowRect(handle, out var bounds)) return MonitorFromWindow(handle, MonitorDefaultToNearest);
        // モニターごとのDPIが違っても、選択判定には物理ピクセルの中心を使う。
        return MonitorFromPoint(new NativePoint((bounds.Left + bounds.Right) / 2,
            (bounds.Top + bounds.Bottom) / 2), nearestIfOutside ? MonitorDefaultToNearest : 0);
    }

    public static Point Center(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetWindowRect(handle, out var bounds)) return new Point(window.Left + window.ActualWidth / 2,
            window.Top + window.ActualHeight / 2);
        var transform = HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice;
        return transform.Transform(new Point((bounds.Left + bounds.Right) / 2.0, (bounds.Top + bounds.Bottom) / 2.0));
    }

    public static Size ClientSize(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetClientRect(handle, out var rectangle)) return new Size(window.Width, window.Height);
        var transform = HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice;
        var size = transform.Transform(new Vector(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top));
        return new Size(size.X, size.Y);
    }

    public static Rect WorkArea(Window window)
    {
        return FromPixels(window, WorkAreaPixels(window));
    }

    public static Rect FromPixels(Window window, Rect bounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        bounds.Transform(HwndSource.FromHwnd(handle).CompositionTarget.TransformFromDevice);
        return bounds;
    }

    public static Rect WorkAreaPixels(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        // 同期モードの対象選択と同じ、ウィンドウの中心があるモニターを使う。
        var monitor = MonitorAtCenter(window);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info)) return info.Work.ToRect();
        var fallback = SystemParameters.WorkArea;
        fallback.Transform(HwndSource.FromHwnd(handle).CompositionTarget.TransformToDevice);
        return fallback;
    }

    public static Rect OuterBoundsForVisibleFrame(Window window, Rect visibleBounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        // DWMの表示枠は透明なリサイズ領域を含まない。両方とも物理ピクセルで比較する。
        if (!GetWindowRect(handle, out var outer)
            || DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) < 0
            || frame.Right <= frame.Left || frame.Bottom <= frame.Top) return visibleBounds;
        return WindowSizing.OuterBoundsForVisibleFrame(visibleBounds, outer.ToRect(), frame.ToRect());
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint DwmExtendedFrameBounds = 9;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out NativeRect rectangle, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
