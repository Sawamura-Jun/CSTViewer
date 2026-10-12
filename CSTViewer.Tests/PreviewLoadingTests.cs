using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer.Tests;

public class PreviewLoadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTiff_ReadsClassicAndBigTiff(bool bigTiff)
    {
        string path = TemporaryPath();
        try
        {
            using (var raw = VipsImage.NewFromMemory(Enumerable.Repeat((byte)137, 80 * 60).ToArray(),
                       80, 60, 1, Enums.BandFormat.Uchar))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Bw))
                image.Tiffsave(path, bigtiff: bigTiff);
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                reader.ReadUInt16();
                Assert.Equal(bigTiff ? 43 : 42, reader.ReadUInt16());
            }
            var page = await ImageLoader.LoadPageAsync(path, 0, new Size(512, 512));
            Assert.Equal((80, 60), (page.SourceWidth, page.SourceHeight));
            Assert.Equal(137, FirstPixel(page.Bitmap));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TiffOrientation_RotatesPixelsAndSourceDimensions()
    {
        string path = TemporaryPath();
        try
        {
            var metadata = new BitmapMetadata("tiff");
            // TIFF Orientation=6は、表示時に時計回り90度の回転を指定する。
            metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
            var encoder = new TiffBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(80, 60, 137), null, metadata, null));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var page = await ImageLoader.LoadPageAsync(path, 0, new Size(512, 512));
            Assert.Equal((60, 80), (page.SourceWidth, page.SourceHeight));
            Assert.Equal((60, 80), (page.Bitmap.PixelWidth, page.Bitmap.PixelHeight));
            Assert.Equal(137, FirstPixel(page.Bitmap));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Gray16Tiff_UsesSameEightBitCastAsPython()
    {
        string path = TemporaryPath();
        try
        {
            ushort[] pixels = [128, 256, 4095];
            using (var raw = VipsImage.NewFromMemory(pixels, 3, 1, 1, Enums.BandFormat.Ushort))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Grey16))
                image.Tiffsave(path);
            var page = await ImageLoader.LoadPageAsync(path, 0, new Size(512, 512));
            Assert.Equal(PixelFormats.Gray8, page.Bitmap.Format);
            var actual = new byte[3];
            page.Bitmap.CopyPixels(actual, 3, 0);
            Assert.Equal(new byte[] { 128, 255, 255 }, actual);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Preview_ReadsEveryPageWithIndependentDimensionsAndPixels()
    {
        string path = TemporaryPath();
        try
        {
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(2000, 1000, 31)));
            encoder.Frames.Add(BitmapFrame.Create(CreateBitmap(600, 1200, 179)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var session = new ImageLoadSession(path, new Size(512, 512));
            try
            {
                var first = await session.LoadPageAsync(0, session.PreviewSize, null);
                Assert.Equal(2, first.PageCount);
                Assert.Equal((2000, 1000), (first.SourceWidth, first.SourceHeight));
                Assert.Equal((512, 256), (first.Bitmap.PixelWidth, first.Bitmap.PixelHeight));
                Assert.Equal(31, FirstPixel(first.Bitmap));
                var second = await session.LoadPageAsync(1, session.PreviewSize, null);
                Assert.Equal((600, 1200), (second.SourceWidth, second.SourceHeight));
                Assert.Equal((256, 512), (second.Bitmap.PixelWidth, second.Bitmap.PixelHeight));
                Assert.Equal(179, FirstPixel(second.Bitmap));
                Assert.True(second.Bitmap.IsFrozen);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { session.Cancel(); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledSession_DoesNotDecodeAnotherPage()
    {
        var session = new ImageLoadSession("not-needed.tif", new Size(512, 512));
        session.Cancel();
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LoadPageAsync(0, session.PreviewSize, null));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    public async Task AlphaTiff_ConvertsToBgraWithoutLosingAlpha(int bands)
    {
        string path = TemporaryPath();
        try
        {
            byte[] pixels = bands == 4 ? [200, 100, 50, 128] : [90, 128];
            using (var raw = VipsImage.NewFromMemory(pixels, 1, 1, bands, Enums.BandFormat.Uchar))
            using (var image = raw.Copy(interpretation: bands == 4 ? Enums.Interpretation.Srgb : Enums.Interpretation.Bw))
                image.Tiffsave(path);
            var page = await ImageLoader.LoadPageAsync(path, 0, new Size(512, 512));
            Assert.Equal(PixelFormats.Bgra32, page.Bitmap.Format);
            var actual = new byte[4];
            page.Bitmap.CopyPixels(actual, 4, 0);
            Assert.Equal(bands == 4 ? new byte[] { 50, 100, 200, 128 } : [90, 90, 90, 128], actual);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MonochromePreview_ReducesCcittBeforeMaterializingBitmap()
    {
        string path = TemporaryPath();
        try
        {
            var bitmap = BitmapSource.Create(2048, 1024, 96, 96, PixelFormats.BlackWhite, null,
                Enumerable.Repeat((byte)0xAA, 256 * 1024).ToArray(), 256);
            bitmap.Freeze();
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Ccitt4 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var measurement = new ImageLoadMeasurement(path);
            var page = await ImageLoader.LoadPageAsync(path, 0, new Size(512, 512), measurement);
            measurement.FinishStage();
            Assert.Equal((512, 256), (page.Bitmap.PixelWidth, page.Bitmap.PixelHeight));
            Assert.Equal(PixelFormats.Gray8, page.Bitmap.Format);
            Assert.Equal(512 * 256, page.ByteSize);
            Assert.Equal(2048, measurement.SourceWidth);
            Assert.Equal(512, measurement.PixelWidth);
            Assert.Equal("NetVips", measurement.Backend);
            Assert.Contains("BitmapCreate", measurement.StageMilliseconds.Keys);
            Assert.InRange(FirstPixel(page.Bitmap), 120, 135);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsedAndHonoursByteLimit()
    {
        var bitmap = CreateBitmap(10, 10, 0);
        var cache = new PageImageCache(2, 600);
        cache.Add(new ImagePage(0, 3, 10, 10, bitmap));
        cache.Add(new ImagePage(1, 3, 10, 10, bitmap));
        Assert.NotNull(cache.Get(0));
        cache.Add(new ImagePage(2, 3, 10, 10, bitmap));
        Assert.Null(cache.Get(1));
        Assert.NotNull(cache.Get(0));
        Assert.Equal(600, cache.ByteSize);
        cache.Add(new ImagePage(0, 3, 20, 20, CreateBitmap(20, 20, 0)));
        Assert.Null(cache.Get(0));
        Assert.Equal(300, cache.ByteSize);
        Assert.Equal(1, cache.Count);
        var byteBoundCache = new PageImageCache(64, 300);
        byteBoundCache.Add(new ImagePage(0, 3, 10, 10, bitmap));
        byteBoundCache.Add(new ImagePage(1, 3, 10, 10, bitmap));
        Assert.Null(byteBoundCache.Get(0));
        Assert.Equal(1, byteBoundCache.Count);
    }

    [Fact]
    public void DetailTarget_PreservesAspectRatioAndAllowsFullSourceResolution()
    {
        var page = new ImagePage(0, 5, 28086, 19866, CreateBitmap(10, 10, 0));
        Size moderate = page.DetailDecodeSize(0.04);
        Assert.InRange(moderate.Width, 2021, 2023);
        Size full = page.DetailDecodeSize(1);
        // 大判画像でも、元の解像度を画素数の上限で切り詰めない。
        Assert.Equal(new Size(28086, 19866), full);
        var small = new ImagePage(0, 1, 80, 60, CreateBitmap(10, 10, 0));
        Assert.Equal(new Size(80, 60), small.DetailDecodeSize(32));
    }

    internal static BitmapSource CreateBitmap(int width, int height, byte value)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat(value, width * height * 3).ToArray(), width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplayResize_MaterializesFrozenPixelsAndPreservesAlpha(bool alpha)
    {
        PixelFormat format = alpha ? PixelFormats.Bgra32 : PixelFormats.Rgb24;
        int bands = alpha ? 4 : 3;
        var source = BitmapSource.Create(200, 100, 96, 96, format, null,
            Enumerable.Repeat((byte)137, 200 * 100 * bands).ToArray(), 200 * bands);
        source.Freeze();
        var resized = await BitmapResizer.ResizeAsync(source, new Size(50, 50), CancellationToken.None);
        Assert.Equal((50, 25), (resized.PixelWidth, resized.PixelHeight));
        Assert.True(resized.IsFrozen);
        Assert.Equal(format, resized.Format);
        var pixels = new byte[50 * 25 * bands];
        resized.CopyPixels(pixels, 50 * bands, 0);
        Assert.All(pixels, value => Assert.Equal(137, value));
        Assert.Equal((200, 100), (source.PixelWidth, source.PixelHeight));
    }

    [Fact]
    public async Task DisplayResize_CancelledSessionDoesNotCreateBitmap()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BitmapResizer.ResizeAsync(
            CreateBitmap(200, 100, 137), new Size(50, 50), cancellation.Token));
    }

    private static int FirstPixel(BitmapSource bitmap)
    {
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels[0];
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"CSTViewer-preview-{Guid.NewGuid()}.tif");
}
