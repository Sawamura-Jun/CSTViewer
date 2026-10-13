using System.Windows;

namespace CSTViewer;

/// <summary>画像の同期とは独立して、左右配置前のサイズとドラッグ中の復元位置を保持する。</summary>
internal sealed class SpreadWindowPlacement
{
    private Size? _restoreSize;
    private Size _arrangedSize;
    private Rect? _dragStartBounds;
    private Point _dragStartPointer;
    private double _anchorRatioX;
    private double _anchorY;
    private Size? _dragRestoreSize;

    public bool IsWaitingToRestore => _restoreSize is not null;

    public void Capture(Size normalSize, Size arrangedSize)
    {
        // 再配置やSyncの切り替えで、最初に保存した通常サイズを上書きしない。
        _restoreSize ??= normalSize;
        _arrangedSize = arrangedSize;
        EndDrag();
    }

    public void SizeChanged(Size size)
    {
        if (_restoreSize is not null && (Math.Abs(size.Width - _arrangedSize.Width) > ViewerSettings.SpreadSizeTolerance
            || Math.Abs(size.Height - _arrangedSize.Height) > ViewerSettings.SpreadSizeTolerance)) Cancel();
    }

    public void BeginDrag(Rect screenBounds, Point screenPointer, DpiScale dpi)
    {
        EndDrag();
        if (_restoreSize is null) return;
        _dragStartBounds = screenBounds;
        _dragStartPointer = screenPointer;
        _anchorRatioX = Math.Clamp((screenPointer.X - screenBounds.Left) / screenBounds.Width, 0, 1);
        // 横はつかんだ位置の割合、縦はタイトルバー上の距離をDIPで保つ。
        _anchorY = Math.Max(0, screenPointer.Y - screenBounds.Top) / dpi.DpiScaleY;
    }

    public bool TryMove(Rect proposedBounds, Point screenPointer, DpiScale dpi, out Rect restoredBounds)
    {
        restoredBounds = proposedBounds;
        if (_dragStartBounds is not { } start) return false;
        if (_dragRestoreSize is null)
        {
            double distanceX = Math.Max(Math.Abs(proposedBounds.Left - start.Left), Math.Abs(screenPointer.X - _dragStartPointer.X));
            double distanceY = Math.Max(Math.Abs(proposedBounds.Top - start.Top), Math.Abs(screenPointer.Y - _dragStartPointer.Y));
            if (distanceX < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX
                && distanceY < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return false;
            _dragRestoreSize = _restoreSize;
            _restoreSize = null;
        }
        if (_dragRestoreSize is not { } size) return false;

        // Win32の移動矩形は物理ピクセル。移動先のDPIが変わっても通常サイズはDIPで維持する。
        double width = Math.Round(size.Width * dpi.DpiScaleX);
        double height = Math.Round(size.Height * dpi.DpiScaleY);
        restoredBounds = new Rect(Math.Round(screenPointer.X - width * _anchorRatioX),
            Math.Round(screenPointer.Y - _anchorY * dpi.DpiScaleY), width, height);
        return true;
    }

    public void EndDrag()
    {
        _dragStartBounds = null;
        _dragRestoreSize = null;
    }

    public void Cancel()
    {
        _restoreSize = null;
        EndDrag();
    }
}
