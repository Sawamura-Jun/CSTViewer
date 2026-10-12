using System.IO;

namespace CSTViewer.Tests;

public class DirectoryImageListCacheTests
{
    [Fact]
    public async Task SameDirectory_ReusesEnumerationUntilExplicitRefresh()
    {
        int calls = 0;
        using var cache = new DirectoryImageListCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return DirectoryImageList.Empty;
        });
        string directory = Path.Combine(Path.GetTempPath(), $"CSTViewer-cache-{Guid.NewGuid()}");
        await cache.GetAsync(Path.Combine(directory, "01.tif"));
        await cache.GetAsync(Path.Combine(directory.ToUpperInvariant(), "02.TIFF"));
        await cache.GetAsync(Path.Combine(directory, "image.png"));
        await cache.GetAsync(Path.Combine(directory, "03.tif"));
        Assert.Equal(1, calls);
        await cache.GetAsync(Path.Combine(directory, "03.tif"), refresh: true);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FolderChange_InvalidatesPreviousDirectoryEvenWhenOpeningPng()
    {
        int calls = 0;
        using var cache = new DirectoryImageListCache((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return DirectoryImageList.Empty;
        });
        string directory = Path.Combine(Path.GetTempPath(), $"CSTViewer-cache-{Guid.NewGuid()}");
        string first = Path.Combine(directory, "first", "01.tif");
        string other = Path.Combine(directory, "other", "01.tif");
        await cache.GetAsync(first);
        await cache.GetAsync(other);
        Assert.Equal(2, calls);
        await cache.GetAsync(Path.ChangeExtension(first, ".png"));
        Assert.Equal(2, calls);
        await cache.GetAsync(first);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task PendingEnumeration_IsSharedAndCancelledOnFolderChange()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var cache = new DirectoryImageListCache((_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult(token);
                // 遅いネットワーク列挙を再現し、フォルダー変更時のキャンセルを検証する。
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                token.ThrowIfCancellationRequested();
            }
            return DirectoryImageList.Empty;
        });
        string directory = Path.Combine(Path.GetTempPath(), $"CSTViewer-cache-{Guid.NewGuid()}");
        Task<DirectoryImageList> first = cache.GetAsync(Path.Combine(directory, "01.tif"));
        try
        {
            CancellationToken token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<DirectoryImageList> second = cache.GetAsync(Path.Combine(directory, "02.tif"));
            Assert.Same(first, second);
            Assert.Equal(1, calls);
            await cache.GetAsync(Path.Combine(directory, "other", "01.tif"));
            Assert.True(token.IsCancellationRequested);
            Assert.Equal(2, calls);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        finally { release.Set(); }
    }
}
