using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CSTViewer.Tests;

public class ImageDetailRegionTests
{
    [Fact]
    public void FullImageRequest_CoversEntirePageAtEveryPanPosition()
    {
        var viewport = new ImageViewport();
        viewport.Resize(new Size(800, 600));
        viewport.SetImage(new Size(8000, 6000));
        viewport.Zoom(new Point(400, 300), 120 * 10);
        var request = ImageDetailRequest.CreateFullImage(viewport, 0.73);
        Assert.Equal(new Int32Rect(0, 0, 8000, 6000), request.Rectangle);
        Assert.Equal(1, request.DecodeScale);
        viewport.Pan(new Vector(-100000, -100000));
        Assert.Equal(request, ImageDetailRequest.CreateFullImage(viewport, 0.73));
    }

    [Fact]
    public async Task FullDetail_DecodesEntirePageAtRequestedScaleAndReleasesFile()
    {
        string path = TemporaryPath();
        try
        {
            Save(path, PreviewLoadingTests.CreateBitmap(512, 256, 71));
            var measurement = new ImageLoadMeasurement(path);
            var detail = await ImageLoader.LoadFullDetailAsync(path, 0,
                new ImageDetailRequest(new Int32Rect(0, 0, 512, 256), 0.5), measurement);
            Assert.Equal(new Int32Rect(0, 0, 512, 256), detail.Rectangle);
            Assert.Equal((256, 128), (detail.Bitmap.PixelWidth, detail.Bitmap.PixelHeight));
            Assert.True(detail.Bitmap.IsFrozen);
            Assert.Equal(512, measurement.RegionWidth);
            var pixels = new byte[256 * 128 * 3];
            detail.Bitmap.CopyPixels(pixels, 256 * 3, 0);
            Assert.All(pixels, value => Assert.Equal((byte)71, value));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Request_ContainsVisiblePixelsAndMarginWithoutExceedingImageEdges()
    {
        var viewport = new ImageViewport();
        viewport.Resize(new Size(800, 600));
        viewport.SetImage(new Size(8000, 6000));
        viewport.Zoom(new Point(400, 300), 120 * 10);
        AssertRequest(viewport);
        viewport.Pan(new Vector(100000, 100000));
        var topLeft = AssertRequest(viewport);
        Assert.Equal(0, topLeft.Rectangle.X);
        Assert.Equal(0, topLeft.Rectangle.Y);
        viewport.Pan(new Vector(-100000, -100000));
        var bottomRight = AssertRequest(viewport);
        Assert.Equal(8000, bottomRight.Rectangle.X + bottomRight.Rectangle.Width);
        Assert.Equal(6000, bottomRight.Rectangle.Y + bottomRight.Rectangle.Height);
    }

    private static ImageDetailRequest AssertRequest(ImageViewport viewport)
    {
        var request = ImageDetailRequest.Create(viewport, viewport.Scale);
        Rect bounds = new(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height);
        Assert.True(bounds.Contains(viewport.VisibleSourceBounds));
        Assert.True(viewport.ImageBounds.Contains(bounds));
        Assert.True(bounds.Width <= Math.Ceiling(viewport.VisibleSourceBounds.Width
            * (1 + 2 * ViewerSettings.DetailRegionMarginRatio)) + 2);
        Assert.True(bounds.Height <= Math.Ceiling(viewport.VisibleSourceBounds.Height
            * (1 + 2 * ViewerSettings.DetailRegionMarginRatio)) + 2);
        Assert.True(bounds.Width * bounds.Height < viewport.ImageBounds.Width * viewport.ImageBounds.Height / 4);
        return request;
    }

    [Fact]
    public void Cache_ReusesOnlyCoveredPagesAndSufficientResolutionWithinMemoryLimit()
    {
        var bitmap = PreviewLoadingTests.CreateBitmap(100, 50, 71);
        var region = new ImageDetailRegion(0, new Int32Rect(200, 100, 200, 100), bitmap);
        var cache = new ImageDetailRegionCache(2, region.ByteSize * 2);
        cache.Add(region);
        Assert.Same(region, cache.Get(0, new Rect(220, 110, 100, 60), 0.25));
        Assert.Null(cache.Get(1, new Rect(220, 110, 100, 60), 0.25));
        Assert.Null(cache.Get(0, new Rect(220, 110, 100, 60), 1));
        Assert.Null(cache.Get(0, new Rect(390, 110, 100, 60), 0.25));
        var second = region with { PageIndex = 1, Rectangle = new Int32Rect(500, 100, 200, 100) };
        cache.Add(second);
        Assert.Same(region, cache.Get(0, region.Bounds, 0.25));
        cache.Add(region with { PageIndex = 2 });
        Assert.Null(cache.Get(1, second.Bounds, 0.25));
        Assert.Equal(2, cache.Count);
        Assert.Equal(region.ByteSize * 2, cache.ByteSize);
        var tooSmall = new ImageDetailRegionCache(2, region.ByteSize - 1);
        tooSmall.Add(region);
        Assert.Equal(0, tooSmall.Count);
    }

    [Fact]
    public void PageCache_ReplacesRegionWithFullImageAndKeepsFourRecentPages()
    {
        var cache = new ImageDetailRegionCache(4);
        var bitmap = PreviewLoadingTests.CreateBitmap(100, 50, 71);
        var full = new ImageDetailRegion(0, new Int32Rect(0, 0, 100, 50), bitmap);
        var region = new ImageDetailRegion(0, new Int32Rect(10, 10, 20, 20),
            PreviewLoadingTests.CreateBitmap(20, 20, 71));
        cache.Add(region);
        cache.Add(full);
        cache.Add(region);
        Assert.Equal(1, cache.Count);
        Assert.Same(full, cache.Get(0, full.Bounds, 1));
        for (int index = 1; index < 4; index++) cache.Add(full with { PageIndex = index });
        Assert.Same(full, cache.Get(0, full.Bounds, 1));
        cache.Add(full with { PageIndex = 4 });
        Assert.Equal(4, cache.Count);
        Assert.Null(cache.Get(1, full.Bounds, 1));
        Assert.Same(full, cache.Get(0, full.Bounds, 1));
    }

    [Fact]
    public async Task SessionCache_RetainsAllDetailedPagesAndReusesThemWithoutReadingFile()
    {
        var session = new ImageLoadSession("not-needed.tif", new Size(64, 64)) { PageCount = 12 };
        var bitmap = PreviewLoadingTests.CreateBitmap(128, 64, 71);
        var regions = Enumerable.Range(0, session.PageCount)
            .Select(index => new ImageDetailRegion(index, new Int32Rect(0, 0, 128, 64), bitmap)).ToArray();
        try
        {
            foreach (var region in regions) session.Details.Add(region);
            Assert.Equal(session.PageCount, session.Details.Count);
            // 5ページ目以降を保存しても、先頭を含む全ページをファイルの再読み込みなしで使える。
            foreach (var region in regions)
            {
                var page = new ImagePage(region.PageIndex, session.PageCount, 128, 64, bitmap);
                Assert.Same(region, await session.LoadFullPageAsync(page, 1, null));
            }
            Assert.Equal(session.PageCount, session.Details.Count);
        }
        finally { session.Cancel(); }
    }

    [Fact]
    public async Task CompletedFullPage_SatisfiesRegionRequestWithoutOpeningFileAgain()
    {
        var session = new ImageLoadSession("not-needed.tif", new Size(64, 64));
        var bitmap = PreviewLoadingTests.CreateBitmap(128, 64, 71);
        var page = new ImagePage(0, 1, 128, 64, bitmap);
        var full = new ImageDetailRegion(0, new Int32Rect(0, 0, 128, 64), bitmap);
        try
        {
            session.Details.Add(full);
            var reused = await session.LoadDetailRegionAsync(0,
                new ImageDetailRequest(new Int32Rect(10, 10, 40, 30), 1), null, CancellationToken.None);
            Assert.Same(full, reused);
            Assert.Same(full, await session.LoadFullPageAsync(page, 1, null));
            session.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LoadFullPageAsync(page, 1, null));
        }
        finally { session.Cancel(); }
    }

    [Fact]
    public async Task FullPage_CachesDisplayResolutionAndReusesItWithoutReadingFile()
    {
        string path = TemporaryPath();
        var session = new ImageLoadSession(path, new Size(64, 64));
        try
        {
            Save(path, PreviewLoadingTests.CreateBitmap(1024, 512, 71));
            var page = new ImagePage(0, 1, 1024, 512, PreviewLoadingTests.CreateBitmap(64, 32, 71));
            var full = await session.LoadFullPageAsync(page, 0.1, null);
            Assert.Equal(new Int32Rect(0, 0, 1024, 512), full.Rectangle);
            Assert.InRange(full.Bitmap.PixelWidth, 183, 185);
            Assert.InRange(full.Bitmap.PixelHeight, 91, 93);
            Assert.Same(full, session.Details.Get(0, full.Bounds, 0.1));
            Assert.Null(session.Details.Get(0, full.Bounds, 0.2));
            // ファイルを排他ロックしても、同じ倍率と縮小後の倍率には保存済みの②だけで対応できる。
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Same(full, await session.LoadFullPageAsync(page, 0.1, null));
            Assert.Same(full, await session.LoadFullPageAsync(page, 0.08, null));
            Assert.Same(full, await session.LoadDetailRegionAsync(0,
                new ImageDetailRequest(new Int32Rect(100, 100, 100, 50), 0.18), null, CancellationToken.None));
        }
        finally { session.Cancel(); File.Delete(path); }
    }

    [Fact]
    public async Task FullPage_HigherResolutionWaitsForRunningRequestThenUpdatesCache()
    {
        string path = TemporaryPath();
        var session = new ImageLoadSession(path, new Size(64, 64));
        var gate = (SemaphoreSlim)typeof(ImageLoadSession).GetField("DecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        bool held = false;
        Task<ImageDetailRegion>? initial = null, upgraded = null;
        try
        {
            Save(path, PreviewLoadingTests.CreateBitmap(1024, 512, 71));
            var page = new ImagePage(0, 1, 1024, 512, PreviewLoadingTests.CreateBitmap(64, 32, 71));
            await gate.WaitAsync();
            held = true;
            initial = session.LoadFullPageAsync(page, 0.1, null);
            Assert.Same(initial, session.LoadFullPageAsync(page, 0.1, null));
            upgraded = session.LoadFullPageAsync(page, 0.3, null);
            Assert.NotSame(initial, upgraded);
            Assert.False(upgraded.IsCompleted);
            gate.Release();
            held = false;
            var low = await initial.WaitAsync(TimeSpan.FromSeconds(8));
            var high = await upgraded.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(high.Bitmap.PixelWidth > low.Bitmap.PixelWidth);
            Assert.True(high.Covers(0, high.Bounds, 0.3));
            Assert.Same(high, session.Details.Get(0, high.Bounds, 0.3));
            Assert.Same(high, await session.LoadFullPageAsync(page, 0.2, null));
            var original = await session.LoadFullPageAsync(page, 1, null);
            Assert.Equal((1024, 512), (original.Bitmap.PixelWidth, original.Bitmap.PixelHeight));
        }
        finally
        {
            if (held) gate.Release();
            session.Cancel();
            foreach (var pending in new[] { initial, upgraded }.OfType<Task<ImageDetailRegion>>())
                try { await pending.WaitAsync(TimeSpan.FromSeconds(8)); } catch (OperationCanceledException) { }
            File.Delete(path);
        }
    }

    [Fact]
    public void Cache_RetainsScaledFullPageWhenLateRegionOnlyDiffersByPixelRounding()
    {
        var cache = new ImageDetailRegionCache(4);
        var full = new ImageDetailRegion(0, new Int32Rect(0, 0, 1000, 500), PreviewLoadingTests.CreateBitmap(360, 180, 71));
        var roundedRegion = new ImageDetailRegion(0, new Int32Rect(100, 100, 100, 50), PreviewLoadingTests.CreateBitmap(37, 19, 71));
        cache.Add(full);
        cache.Add(roundedRegion);
        Assert.Equal(1, cache.Count);
        Assert.Same(full, cache.Get(0, full.Bounds, 0.2));
        // 丸め差を超える拡大要求には、新しく読んだ領域画像を保存する。
        var sharperRegion = roundedRegion with { Bitmap = PreviewLoadingTests.CreateBitmap(72, 36, 71) };
        cache.Add(sharperRegion);
        Assert.Same(sharperRegion, cache.Get(0, sharperRegion.Bounds, 0.4));
    }

    [Theory]
    [InlineData(TiffCompressOption.Lzw)]
    [InlineData(TiffCompressOption.Zip)]
    public async Task RegionDecode_ReadsRequestedCoordinatesAndBoundsBitmapSize(TiffCompressOption compression)
    {
        string path = TemporaryPath();
        try
        {
            var pixels = new byte[512 * 256];
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 512; x++) pixels[y * 512 + x] = (byte)((x + y * 3) % 256);
            Save(path, BitmapSource.Create(512, 256, 96, 96, PixelFormats.Gray8, null, pixels, 512), compression);
            var rectangle = new Int32Rect(103, 57, 168, 90);
            var measurement = new ImageLoadMeasurement(path);
            var region = await ImageLoader.LoadDetailRegionAsync(path, 0, new(rectangle, 1), measurement);
            Assert.Equal(rectangle, region.Rectangle);
            Assert.Equal((168, 90), (region.Bitmap.PixelWidth, region.Bitmap.PixelHeight));
            Assert.True(region.Bitmap.IsFrozen);
            var actual = new byte[168 * 90];
            region.Bitmap.CopyPixels(actual, 168, 0);
            for (int y = 0; y < 90; y++)
                for (int x = 0; x < 168; x++) Assert.Equal(pixels[(y + 57) * 512 + x + 103], actual[y * 168 + x]);
            Assert.Equal(512, measurement.SourceWidth);
            Assert.Equal(103, measurement.RegionX);
            Assert.Equal(168, measurement.RegionWidth);
            var reduced = await ImageLoader.LoadDetailRegionAsync(path, 0, new(rectangle, 0.5));
            Assert.Equal((84, 45), (reduced.Bitmap.PixelWidth, reduced.Bitmap.PixelHeight));
            Assert.Equal(region.ByteSize / 4, reduced.ByteSize);
            // ネイティブ画像を破棄した後は、元ファイルを排他的に開ける。
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RegionDecode_StrongReductionKeepsSinglePixelLines()
    {
        string path = TemporaryPath();
        try
        {
            const int width = 1024, height = 64, spacing = 23;
            const double scale = 0.243;
            var pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
            for (int x = 20; x < width - 20; x += spacing)
                for (int y = 0; y < height; y++) pixels[y * width + x] = 0;
            Save(path, BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width));

            var region = await ImageLoader.LoadDetailRegionAsync(path, 0, new(new Int32Rect(0, 0, width, height), scale));
            var row = new byte[region.Bitmap.PixelWidth];
            region.Bitmap.CopyPixels(new Int32Rect(0, region.Bitmap.PixelHeight / 2, row.Length, 1), row, row.Length, 0);
            // 元の画素位置が異なる細線も、縮小後の近傍に残ることを確認する。
            for (int x = 20; x < width - 20; x += spacing)
            {
                int center = (int)(x * scale);
                Assert.Contains(row[(center - 1)..(center + 2)], value => value < 255);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RegionDecode_UsesSelectedPageAndRotatedCoordinates()
    {
        string path = TemporaryPath();
        try
        {
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(40, 40, 0)));
            const int width = 400, height = 600;
            var pixels = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) pixels[y * width + x] = (byte)((x + y * 3) % 256);
            var metadata = new BitmapMetadata("tiff");
            metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96,
                PixelFormats.Gray8, null, pixels, width), null, metadata, null));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var rectangle = new Int32Rect(5, 25, 300, 200);
            var region = await ImageLoader.LoadDetailRegionAsync(path, 1, new(rectangle, 1));
            Assert.Equal(1, region.PageIndex);
            var actual = new byte[rectangle.Width * rectangle.Height];
            region.Bitmap.CopyPixels(actual, rectangle.Width, 0);
            for (int y = 0; y < rectangle.Height; y++)
                for (int x = 0; x < rectangle.Width; x++)
                    Assert.Equal(pixels[(height - 1 - rectangle.X - x) * width + rectangle.Y + y],
                        actual[y * rectangle.Width + x]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledOrInvalidRegion_DoesNotLoadPixelsOrBlockDecodeGate()
    {
        string path = TemporaryPath();
        var session = new ImageLoadSession(path, new Size(512, 512));
        try
        {
            var request = new ImageDetailRequest(new Int32Rect(0, 0, 10, 10), 1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.LoadDetailRegionAsync(
                0, request, null, new CancellationToken(true)));
            Save(path, PreviewLoadingTests.CreateBitmap(20, 20, 71), TiffCompressOption.Lzw);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageLoader.LoadDetailRegionAsync(
                path, 0, new(new Int32Rect(15, 15, 10, 10), 1)));
            Assert.NotNull(await session.LoadDetailRegionAsync(0, request, null, CancellationToken.None));
        }
        finally { session.Cancel(); File.Delete(path); }
    }

    internal static void Save(string path, BitmapSource bitmap, TiffCompressOption compression = TiffCompressOption.Zip)
    {
        var encoder = new TiffBitmapEncoder { Compression = compression };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"CSTViewer-region-{Guid.NewGuid():N}.tif");
}
