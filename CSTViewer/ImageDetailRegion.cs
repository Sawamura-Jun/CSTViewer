using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>表示範囲と周辺の余白を、元画像の画素座標で読み込む要求。</summary>
internal readonly record struct ImageDetailRequest(Int32Rect Rectangle, double DecodeScale)
{
    public static ImageDetailRequest CreateFullImage(ImageViewport viewport, double displayScale)
    {
        Rect bounds = viewport.ImageBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return default;
        return new ImageDetailRequest(new Int32Rect(0, 0, (int)bounds.Width, (int)bounds.Height),
            Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor));
    }

    public static ImageDetailRequest Create(ImageViewport viewport, double displayScale)
    {
        Rect visible = viewport.VisibleSourceBounds;
        if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return default;
        visible.Inflate(visible.Width * ViewerSettings.DetailRegionMarginRatio,
            visible.Height * ViewerSettings.DetailRegionMarginRatio);
        visible.Intersect(viewport.ImageBounds);
        // 外側へ丸めて、画面の端に必要な画素を取りこぼさない。
        int left = (int)Math.Floor(visible.Left), top = (int)Math.Floor(visible.Top);
        int right = (int)Math.Ceiling(visible.Right), bottom = (int)Math.Ceiling(visible.Bottom);
        return new ImageDetailRequest(new Int32Rect(left, top, right - left, bottom - top),
            Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor));
    }
}

/// <summary>ページ全体のプレビューに重ねる、位置情報付きの高精細画像。</summary>
internal sealed record ImageDetailRegion(int PageIndex, Int32Rect Rectangle, BitmapSource Bitmap)
{
    public Rect Bounds => new(Rectangle.X, Rectangle.Y, Rectangle.Width, Rectangle.Height);
    public long ByteSize => ((Bitmap.PixelWidth * (long)Bitmap.Format.BitsPerPixel + 7) / 8) * Bitmap.PixelHeight;

    // 再利用にもデコード時と同じ余裕を要求し、拡大途中で補間用の画素が不足するのを防ぐ。
    public bool Covers(int pageIndex, Rect visible, double displayScale) => PageIndex == pageIndex
        && !visible.IsEmpty && Bounds.Contains(visible)
        && Bitmap.PixelWidth >= Math.Ceiling(Rectangle.Width * Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor)) - 1
        && Bitmap.PixelHeight >= Math.Ceiling(Rectangle.Height * Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor)) - 1;
}
