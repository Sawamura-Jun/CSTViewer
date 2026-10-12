using System.Windows;

namespace CSTViewer;

// Flagsにより、角の操作を「左辺と上辺」などの組み合わせで表す。
[Flags]
internal enum CropHit
{
    None = 0, Inside = 1, Left = 2, Top = 4, Right = 8, Bottom = 16, NewSelection = 32
}

/// <summary>選択範囲を元画像の座標で保持し、ズームやパン後も同じ画素を選ぶ。</summary>
internal sealed class ImageCropSelection
{
    private CropHit _mode;
    private Point _pressPosition;
    private Point _startImagePosition;
    private Rect _startRectangle;
    private bool _hasMoved;

    public Rect ImageRectangle { get; private set; } = Rect.Empty;
    public bool HasSelection => !ImageRectangle.IsEmpty && ImageRectangle.Width > 0 && ImageRectangle.Height > 0;
    public bool IsDragging => _mode != CropHit.None;
    public CropHit Mode => _mode;

    public bool BeginSelection(Point position, ImageViewport viewport)
    {
        Clear();
        Rect bounds = viewport.VisibleImageBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;
        _mode = CropHit.NewSelection;
        _pressPosition = position;
        _startImagePosition = viewport.ViewToImage(Clamp(position, bounds));
        return true;
    }

    public bool BeginAdjustment(Point position, ImageViewport viewport)
    {
        CropHit hit = HitTest(position, viewport);
        if (hit == CropHit.None || IsDragging) return false;
        _mode = hit;
        _pressPosition = position;
        _startImagePosition = viewport.ViewToImage(position);
        _startRectangle = ImageRectangle;
        _hasMoved = false;
        return true;
    }

    public void Update(Point position, ImageViewport viewport)
    {
        if (!IsDragging) return;
        Vector movement = position - _pressPosition;
        _hasMoved |= Math.Max(Math.Abs(movement.X), Math.Abs(movement.Y)) >= ViewerSettings.CropDragStartDistance;
        if (!_hasMoved) return;
        Point current = viewport.ViewToImage(position);
        Rect bounds = viewport.ImageBounds;
        if (_mode == CropHit.NewSelection)
        {
            current = viewport.ViewToImage(Clamp(position, viewport.VisibleImageBounds));
            ImageRectangle = new Rect(_startImagePosition, current);
        }
        else if (_mode == CropHit.Inside)
        {
            Vector delta = current - _startImagePosition;
            ImageRectangle = new Rect(
                Math.Clamp(_startRectangle.X + delta.X, 0, bounds.Width - _startRectangle.Width),
                Math.Clamp(_startRectangle.Y + delta.Y, 0, bounds.Height - _startRectangle.Height),
                _startRectangle.Width, _startRectangle.Height);
        }
        else
        {
            current = Clamp(current, bounds);
            double left = _mode.HasFlag(CropHit.Left) ? current.X : _startRectangle.Left;
            double right = _mode.HasFlag(CropHit.Right) ? current.X : _startRectangle.Right;
            double top = _mode.HasFlag(CropHit.Top) ? current.Y : _startRectangle.Top;
            double bottom = _mode.HasFlag(CropHit.Bottom) ? current.Y : _startRectangle.Bottom;
            ImageRectangle = new Rect(new Point(left, top), new Point(right, bottom));
        }
    }

    public bool End(Point position, ImageViewport viewport)
    {
        if (!IsDragging) return false;
        Update(position, viewport);
        bool save = _mode == CropHit.Inside && !_hasMoved;
        bool newClick = _mode == CropHit.NewSelection && !_hasMoved;
        _mode = CropHit.None;
        Rect view = viewport.ImageToView(ImageRectangle);
        if (newClick || view.IsEmpty || view.Width < ViewerSettings.CropMinimumViewSize
            || view.Height < ViewerSettings.CropMinimumViewSize) Clear();
        return save && HasSelection;
    }

    public void CancelDrag()
    {
        if (!IsDragging) return;
        ImageRectangle = _mode == CropHit.NewSelection ? Rect.Empty : _startRectangle;
        _mode = CropHit.None;
    }

    public void Clear()
    {
        ImageRectangle = Rect.Empty;
        _mode = CropHit.None;
        _hasMoved = false;
    }

    public CropHit HitTest(Point position, ImageViewport viewport)
    {
        if (!HasSelection || !viewport.VisibleImageBounds.Contains(position)) return CropHit.None;
        Rect view = viewport.ImageToView(ImageRectangle);
        double grab = ViewerSettings.CropEdgeGrabWidth / 2;
        var outer = view;
        outer.Inflate(grab, grab);
        if (!outer.Contains(position)) return CropHit.None;
        CropHit hit = CropHit.None;
        if (Math.Abs(position.X - view.Left) <= grab) hit |= CropHit.Left;
        else if (Math.Abs(position.X - view.Right) <= grab) hit |= CropHit.Right;
        if (Math.Abs(position.Y - view.Top) <= grab) hit |= CropHit.Top;
        else if (Math.Abs(position.Y - view.Bottom) <= grab) hit |= CropHit.Bottom;
        return hit == CropHit.None && view.Contains(position) ? CropHit.Inside : hit;
    }

    public Int32Rect PixelRectangle(Size sourceSize)
    {
        if (!HasSelection) return Int32Rect.Empty;
        // 外側の画素まで含めるよう切り下げ・切り上げし、画像の端で範囲を制限する。
        int left = Math.Clamp((int)Math.Floor(ImageRectangle.Left), 0, (int)sourceSize.Width - 1);
        int top = Math.Clamp((int)Math.Floor(ImageRectangle.Top), 0, (int)sourceSize.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(ImageRectangle.Right), left + 1, (int)sourceSize.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(ImageRectangle.Bottom), top + 1, (int)sourceSize.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private static Point Clamp(Point position, Rect bounds) => new(
        Math.Clamp(position.X, bounds.Left, bounds.Right), Math.Clamp(position.Y, bounds.Top, bounds.Bottom));
}
