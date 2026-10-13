using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    private readonly SpreadWindowPlacement _spreadWindowPlacement = new();
    private HwndSource? _spreadWindowSource;
    private bool _applyingSpreadWindowBounds;
    private bool _spreadCaptionDrag;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _spreadWindowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        // タイトルバーの操作はWPFのマウスイベントに届かないため、Win32通知を受け取る。
        _spreadWindowSource?.AddHook(SpreadWindowHook);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (!_applyingSpreadWindowBounds && WindowState == WindowState.Normal)
            _spreadWindowPlacement.SizeChanged(sizeInfo.NewSize);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!_applyingSpreadWindowBounds && WindowState == WindowState.Maximized) _spreadWindowPlacement.Cancel();
    }

    internal IntPtr SpreadWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcLeftButtonDown when wParam.ToInt64() == HitCaption:
                _spreadCaptionDrag = _spreadWindowPlacement.IsWaitingToRestore;
                if (_spreadCaptionDrag && GetSpreadWindowRect(hwnd, out var bounds))
                {
                    // lParamの上下16ビットは符号付き座標。左側・上側のモニターも扱う。
                    long packed = lParam.ToInt64();
                    _spreadWindowPlacement.BeginDrag(bounds.ToRect(), new Point((short)packed, (short)(packed >> 16)),
                        VisualTreeHelper.GetDpi(this));
                }
                break;
            case WmMoving when _spreadCaptionDrag && lParam != IntPtr.Zero:
                if (!GetSpreadCursorPos(out var pointer)) break;
                var proposed = Marshal.PtrToStructure<SpreadNativeRect>(lParam);
                bool firstRestore = _spreadWindowPlacement.IsWaitingToRestore;
                if (!_spreadWindowPlacement.TryMove(proposed.ToRect(), new Point(pointer.X, pointer.Y),
                    VisualTreeHelper.GetDpi(this), out var restored)) break;
                _applyingSpreadWindowBounds = true;
                try
                {
                    if (firstRestore) RestoreSpreadMinimumConstraints();
                    var rectangle = new SpreadNativeRect(restored);
                    // 移動ループにも復元寸法を返し、次の通知で左右配置の寸法へ戻されることを防ぐ。
                    Marshal.StructureToPtr(rectangle, lParam, false);
                    SetSpreadWindowPos(hwnd, IntPtr.Zero, rectangle.Left, rectangle.Top,
                        rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, SwpNoZOrder | SwpNoActivate);
                    handled = true;
                }
                finally { _applyingSpreadWindowBounds = false; }
                return new IntPtr(1);
            case WmSizing:
                // 枠のリサイズはユーザー指定を優先し、以前のサイズへ強制的に戻さない。
                _spreadWindowPlacement.Cancel();
                _spreadCaptionDrag = false;
                break;
            case WmExitSizeMove:
                _spreadWindowPlacement.EndDrag();
                _spreadCaptionDrag = false;
                break;
        }
        return IntPtr.Zero;
    }

    private void RemoveSpreadWindowHook()
    {
        _spreadWindowSource?.RemoveHook(SpreadWindowHook);
        _spreadWindowSource = null;
    }

    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmMoving = 0x0216;
    private const int WmSizing = 0x0214;
    private const int WmExitSizeMove = 0x0232;
    private const int HitCaption = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpreadNativeRect
    {
        public int Left, Top, Right, Bottom;
        public SpreadNativeRect(Rect bounds)
        {
            Left = (int)bounds.Left;
            Top = (int)bounds.Top;
            Right = (int)bounds.Right;
            Bottom = (int)bounds.Bottom;
        }
        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpreadNativePoint { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSpreadWindowRect(IntPtr hwnd, out SpreadNativeRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSpreadCursorPos(out SpreadNativePoint point);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSpreadWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
