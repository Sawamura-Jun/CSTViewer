namespace CSTViewer.Tests;

public class ImageDecodeQueueTests
{
    [Fact]
    public async Task Priority_VisibleRegionThenPreviewThenFullPage()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var queue = new ImageDecodeQueue(gate);
        Task full = queue.WaitAsync(CancellationToken.None, ImageDecodePriority.FullPage);
        Task preview = queue.WaitAsync(CancellationToken.None, ImageDecodePriority.Preview);
        Task visible = queue.WaitAsync(CancellationToken.None, ImageDecodePriority.Visible);
        gate.Release();
        await visible.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(full.IsCompleted);
        Assert.False(preview.IsCompleted);
        queue.Release();
        await preview.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(full.IsCompleted);
        queue.Release();
        await full.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
    }

    [Fact]
    public async Task RunningPreview_NextPreviewOvertakesQueuedFullPage()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var queue = new ImageDecodeQueue(gate);
        await queue.WaitAsync(CancellationToken.None, ImageDecodePriority.Preview);
        Task full = queue.WaitAsync(CancellationToken.None, ImageDecodePriority.FullPage);
        Task nextPreview = queue.WaitAsync(CancellationToken.None, ImageDecodePriority.Preview);
        Assert.False(full.IsCompleted);
        Assert.False(nextPreview.IsCompleted);
        queue.Release();
        await nextPreview.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(full.IsCompleted);
        queue.Release();
        await full.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task Foreground_OvertakesPreloadsAndKeepsArrivalOrderWithinEachPriority()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var queue = new ImageDecodeQueue(gate);
        Task preloadFirst = queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task preloadSecond = queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task foregroundFirst = queue.WaitAsync(CancellationToken.None);
        Task foregroundSecond = queue.WaitAsync(CancellationToken.None);

        // 実行中の1ページが終わって枠が空いた状況を再現する。
        gate.Release();
        await foregroundFirst.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(foregroundSecond.IsCompleted);
        Assert.False(preloadFirst.IsCompleted);
        Assert.False(preloadSecond.IsCompleted);
        queue.Release();
        await foregroundSecond.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(preloadFirst.IsCompleted);
        queue.Release();
        await preloadFirst.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(preloadSecond.IsCompleted);
        queue.Release();
        await preloadSecond.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWaiter_IsRemovedWithoutConsumingDecodeSlot(bool cancelPreload)
    {
        using var gate = new SemaphoreSlim(0, 1);
        var queue = new ImageDecodeQueue(gate);
        using var cancellation = new CancellationTokenSource();
        Task cancelled = queue.WaitAsync(cancellation.Token, isPreload: cancelPreload);
        Task next = queue.WaitAsync(CancellationToken.None, isPreload: !cancelPreload);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(next.IsCompleted);
        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.CurrentCount);
        queue.Release();

        // キャンセルが重なった後も、新しい要求が同じ枠を正常に使える。
        await queue.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task RunningPreload_CompletesBeforeForegroundAndQueuedPreloadRunsLast()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var queue = new ImageDecodeQueue(gate);
        await queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task queuedPreload = queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task foreground = queue.WaitAsync(CancellationToken.None);
        Assert.False(foreground.IsCompleted);
        Assert.False(queuedPreload.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);
        queue.Release();
        await foreground.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queuedPreload.IsCompleted);
        queue.Release();
        await queuedPreload.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task MultipleDecodeSlots_RespectConcurrencyLimitAndForegroundPriority()
    {
        using var gate = new SemaphoreSlim(0, 2);
        var queue = new ImageDecodeQueue(gate);
        Task preloadFirst = queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task preloadSecond = queue.WaitAsync(CancellationToken.None, isPreload: true);
        Task foreground = queue.WaitAsync(CancellationToken.None);
        gate.Release(2);
        await Task.WhenAll(foreground, preloadFirst).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(preloadSecond.IsCompleted);
        Assert.Equal(0, gate.CurrentCount);
        queue.Release();
        await preloadSecond.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Release();
        queue.Release();
        Assert.Equal(2, gate.CurrentCount);
    }
}
