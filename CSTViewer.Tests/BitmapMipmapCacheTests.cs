using System.Windows.Media.Imaging;

namespace CSTViewer.Tests;

public class BitmapMipmapCacheTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0.4, 0)]
    [InlineData(0.399, 1)]
    [InlineData(0.2, 1)]
    [InlineData(0.199, 2)]
    [InlineData(0.1, 2)]
    [InlineData(0.099, 3)]
    public void LevelSelection_MatchesPythonThreshold(double scale, int level) =>
        Assert.Equal(level, BitmapMipmapCache.RequiredLevel(scale));

    [Fact]
    public async Task Levels_ReusesBitmapsWithoutMixingDifferentSourceImages()
    {
        var cache = new BitmapMipmapCache(64, 1024 * 1024);
        var source = PreviewLoadingTests.CreateBitmap(256, 128, 31);
        Assert.Same(source, await cache.GetOrCreateAsync(source, 0, CancellationToken.None));
        var half = await cache.GetOrCreateAsync(source, 1, CancellationToken.None);
        var quarter = await cache.GetOrCreateAsync(source, 2, CancellationToken.None);
        Assert.Equal((128, 64), (half.PixelWidth, half.PixelHeight));
        Assert.Equal((64, 32), (quarter.PixelWidth, quarter.PixelHeight));
        Assert.True(quarter.IsFrozen);
        Assert.Same(half, await cache.GetOrCreateAsync(source, 1, CancellationToken.None));
        Assert.Same(quarter, await cache.GetOrCreateAsync(source, 2, CancellationToken.None));
        var otherSource = PreviewLoadingTests.CreateBitmap(256, 128, 179);
        Assert.Null(cache.Get(otherSource, 2));
        var otherQuarter = await cache.GetOrCreateAsync(otherSource, 2, CancellationToken.None);
        Assert.NotSame(quarter, otherQuarter);
        Assert.Equal(31, FirstPixel(quarter));
        Assert.Equal(179, FirstPixel(otherQuarter));
    }

    [Fact]
    public async Task Cache_BoundsMemoryAndBitmapCount()
    {
        var source = PreviewLoadingTests.CreateBitmap(128, 64, 31);
        var byteCache = new BitmapMipmapCache(64, 64 * 32 * 3 + 32 * 16 * 3);
        await byteCache.GetOrCreateAsync(source, 1, CancellationToken.None);
        await byteCache.GetOrCreateAsync(source, 2, CancellationToken.None);
        await byteCache.GetOrCreateAsync(source, 3, CancellationToken.None);
        Assert.Null(byteCache.Get(source, 1));
        Assert.Equal(2, byteCache.Count);
        Assert.Equal((32 * 16 + 16 * 8) * 3, byteCache.ByteSize);
        var countCache = new BitmapMipmapCache(1, 1024 * 1024);
        await countCache.GetOrCreateAsync(source, 1, CancellationToken.None);
        await countCache.GetOrCreateAsync(source, 2, CancellationToken.None);
        Assert.Null(countCache.Get(source, 1));
        Assert.NotNull(countCache.Get(source, 2));
        Assert.Equal(1, countCache.Count);
        var tooSmallCache = new BitmapMipmapCache(64, 1);
        await tooSmallCache.GetOrCreateAsync(source, 1, CancellationToken.None);
        Assert.Equal(0, tooSmallCache.Count);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotPopulateCache()
    {
        var cache = new BitmapMipmapCache(64, 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(
            PreviewLoadingTests.CreateBitmap(128, 64, 31), 1, cancellation.Token));
        Assert.Equal(0, cache.Count);
    }

    private static byte FirstPixel(BitmapSource bitmap)
    {
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels[0];
    }
}
