using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>元画像の座標と表示用Bitmapの解像度を分けて保持する。</summary>
internal sealed record ImagePage(int Index, int PageCount, int SourceWidth, int SourceHeight, BitmapSource Bitmap)
{
    public long ByteSize => ((Bitmap.PixelWidth * (long)Bitmap.Format.BitsPerPixel + 7) / 8) * Bitmap.PixelHeight;

    public Size DetailDecodeSize(double displayScale)
    {
        double scale = Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor);
        // 画素数で制限せず、拡大に応じて元画像の解像度まで読み込む。
        return new Size(Math.Max(1, Math.Floor(SourceWidth * scale)), Math.Max(1, Math.Floor(SourceHeight * scale)));
    }
}
