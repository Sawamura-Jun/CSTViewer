using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using VipsEnums = NetVips.Enums;
using VipsImage = NetVips.Image;

namespace CSTViewer;

internal static class DrawingLineRemoval
{
    public static BitmapFrame[] PrepareFrames(string sourcePath, int pageCount, CancellationToken token,
        IProgress<int>? preparedPages = null)
    {
        ImageLoader.InitializeVips();
        var frames = new BitmapFrame[pageCount];
        // ページごとの読み込み中に元ファイルが書き換わらないよう、読み取りハンドルを保持する。
        using var input = File.OpenRead(sourcePath);
        for (int index = 0; index < pageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            // 表示と同じ向きに画素を並べ替えてから処理し、一時TIFFには向きタグを移さない。
            using var source = VipsImage.Tiffload(sourcePath, page: index, n: 1, autorotate: true,
                access: VipsEnums.Access.Sequential, failOn: VipsEnums.FailOn.Error, revalidate: true);
            int sourcePageCount = source.GetTypeOf("n-pages") != 0 ? (int)source.Get("n-pages") : 1;
            if (sourcePageCount != pageCount)
                throw new InvalidDataException("The TIFF page count changed. Reopen the image and try again.");
            frames[index] = RemoveLines(source, token);
            preparedPages?.Report(index + 1);
        }
        return frames;
    }

    public static void WriteTiff(Stream output, ReadOnlySpan<BitmapFrame> frames, CancellationToken token)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Ccitt4 };
        foreach (BitmapFrame frame in frames)
        {
            token.ThrowIfCancellationRequested();
            encoder.Frames.Add(frame);
        }
        token.ThrowIfCancellationRequested();
        encoder.Save(output);
    }

    private static BitmapFrame RemoveLines(VipsImage source, CancellationToken token)
    {
        using var gray = source.Colourspace(VipsEnums.Interpretation.Bw);
        using var opaque = gray.HasAlpha() ? gray.Flatten(background: [255]) : gray.Copy();
        int width = opaque.Width;
        int height = opaque.Height;
        double dpiX = source.Xres * 25.4;
        double dpiY = source.Yres * 25.4;
        double scale = Math.Clamp(Math.Min(dpiX, dpiY) / OcrSearchSettings.LineRemovalReferenceDpi, 0.25, 4);
        int stride = (width + 7) / 8;

        // libvipsの1バンド画像をOpenCVから直接参照し、大判図面の画素配列を複製しない。
        nint buffer = opaque.WriteToMemory(out ulong length);
        try
        {
            if (length != (ulong)width * (ulong)height || opaque.Bands != 1 || opaque.Format != VipsEnums.BandFormat.Uchar)
                throw new InvalidDataException("Could not convert the TIFF page to 8-bit grayscale.");
            token.ThrowIfCancellationRequested();
            using var ink = Mat.FromPixelData(height, width, MatType.CV_8UC1, buffer, width);
            Cv2.Threshold(ink, ink, OcrSearchSettings.LineRemovalInkThreshold, 1, ThresholdTypes.BinaryInv);
            byte[] pixels = DrawingTextMask.Create(ink, stride, scale, token);
            var bitmap = BitmapSource.Create(width, height, dpiX, dpiY, PixelFormats.BlackWhite, null, pixels, stride);
            return BitmapFrame.Create(bitmap);
        }
        finally
        {
            NetVips.NetVips.Free(buffer);
        }
    }
}
