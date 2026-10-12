using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>読み込み済みの画像から、小さい表示用Bitmapをバックグラウンドで作る。</summary>
internal static class BitmapResizer
{
    public static Task<BitmapSource> ResizeAsync(BitmapSource source, Size size, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            // Freeze済みのBitmapだけをワーカーへ渡し、UIスレッドを止めずに縮小する。
            double scale = Math.Min(1, Math.Min(size.Width / source.PixelWidth, size.Height / source.PixelHeight));
            if (scale >= 1) return source;
            var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            int stride = checked((int)((transformed.PixelWidth * (long)transformed.Format.BitsPerPixel + 7) / 8));
            var pixels = new byte[checked(stride * transformed.PixelHeight)];
            // TransformedBitmapは遅延評価されるため、画素を実体化して毎回の再縮小を避ける。
            transformed.CopyPixels(pixels, stride, 0);
            token.ThrowIfCancellationRequested();
            var bitmap = BitmapSource.Create(transformed.PixelWidth, transformed.PixelHeight,
                source.DpiX, source.DpiY, transformed.Format, transformed.Palette, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }, token);
}
