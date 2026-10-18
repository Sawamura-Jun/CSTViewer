using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>ファイルごとの先読み・キャッシュ・キャンセルを管理する。</summary>
internal sealed class ImageLoadSession
{
    // ファイルを切り替えた直後も含め、重いデコードが重ならないよう全セッションで共有する。
    private static readonly SemaphoreSlim DecodeGate = new(ViewerSettings.ImageDecodeConcurrency);
    private static readonly ImageDecodeQueue DecodeQueue = new(DecodeGate);
    private readonly CancellationTokenSource _cancellation = new();
    private bool _cancelled;
    private sealed class FullPageWork(CancellationToken token, ImageDetailRequest request)
    {
        public ImageDetailRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public bool Started { get; set; }
        public Task<ImageDetailRegion> Task { get; set; } = null!;
    }
    private readonly Dictionary<int, FullPageWork> _fullPages = new();
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; }
    public Size PreviewSize { get; }
    public CancellationToken Token { get; }
    public int PageCount { get; set; }
    public int LoadedPages { get; set; }
    public bool IsPreloading { get; set; } = true;
    public string? Error { get; set; }
    // ① プレビュー画像: 初期表示・ページ切り替え・ズーム操作中に使う軽いページ全体画像。
    public PageImageCache Previews { get; } = new(ViewerSettings.PreviewCachePageLimit);
    // ② 表示用高精細画像を保存する。先行する領域画像は、同じ必要解像度のページ全体画像で置き換える。
    public ImageDetailRegionCache Details { get; } = new(ViewerSettings.DetailCachePageLimit);
    public BitmapMipmapCache Mipmaps { get; } = new(ViewerSettings.MipmapCachePageLimit);

    public ImageLoadSession(string path, Size previewSize)
    {
        Path = path;
        PreviewSize = previewSize;
        Token = _cancellation.Token;
    }

    public async Task<ImagePage> LoadPageAsync(int index, Size size, ImageLoadMeasurement? measurement, bool isPreload = false)
    {
        measurement?.StartStage("DecodeQueue");
        await DecodeQueue.WaitAsync(Token, isPreload);
        try
        {
            return await ImageLoader.LoadPageAsync(Path, index, size, measurement, Token);
        }
        finally
        {
            DecodeQueue.Release();
        }
    }

    public void Cancel()
    {
        if (_cancelled) return;
        _cancelled = true;
        // 実行中のネイティブ処理は完了を待ち、その結果と残りページを破棄する。
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    public async Task<ImageDetailRegion> LoadDetailRegionAsync(int index, ImageDetailRequest request,
        ImageLoadMeasurement? measurement, CancellationToken cancellationToken, bool useRegion = true,
        Action? onDecodeStarted = null)
    {
        // 表示の変更とファイルの切り替えの、どちらでも待機中の要求を中止できるようにする。
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        measurement?.StartStage("DecodeQueue");
        await DecodeQueue.WaitAsync(cancellation.Token);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            // 全体画像の完了を待っていた場合は、同じ画素をもう一度デコードしない。
            var bounds = new Rect(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height);
            if (Details.Get(index, bounds, request.DecodeScale / ViewerSettings.DetailOversamplingFactor) is { } cached)
                return cached;
            // デコード枠を得た後に通知し、実行中と順番待ちの要求を区別する。
            onDecodeStarted?.Invoke();
            return useRegion
                ? await ImageLoader.LoadDetailRegionAsync(Path, index, request, measurement, cancellation.Token)
                : await ImageLoader.LoadFullDetailAsync(Path, index, request, measurement, cancellation.Token);
        }
        finally { DecodeQueue.Release(); }
    }

    public bool IsFullPageLoading(int index) => _fullPages.ContainsKey(index);

    public void CancelQueuedFullPages()
    {
        foreach (var work in _fullPages.Values.ToArray())
            if (!work.Started) work.Cancellation.Cancel();
    }

    public void CancelFullPages()
    {
        foreach (var work in _fullPages.Values.ToArray()) work.Cancellation.Cancel();
    }

    public Task<ImageDetailRegion> LoadFullPageAsync(ImagePage page, double displayScale, ImageLoadMeasurement? measurement)
    {
        Token.ThrowIfCancellationRequested();
        if (!double.IsFinite(displayScale) || displayScale <= 0) throw new ArgumentOutOfRangeException(nameof(displayScale));
        var bounds = new Rect(0, 0, page.SourceWidth, page.SourceHeight);
        if (Details.Get(page.Index, bounds, displayScale) is { } cached) return Task.FromResult(cached);
        var request = new ImageDetailRequest(new Int32Rect(0, 0, page.SourceWidth, page.SourceHeight),
            Math.Min(1, displayScale * ViewerSettings.DetailOversamplingFactor));
        if (_fullPages.TryGetValue(page.Index, out var pending))
            return pending.Request.DecodeScale >= request.DecodeScale ? pending.Task
                : LoadHigherResolutionFullPageAsync(page, displayScale, measurement, pending.Task);
        var work = new FullPageWork(Token, request);
        _fullPages.Add(page.Index, work);
        work.Task = LoadFullPageCoreAsync(page, measurement, work);
        return work.Task;
    }

    private async Task<ImageDetailRegion> LoadHigherResolutionFullPageAsync(ImagePage page, double displayScale,
        ImageLoadMeasurement? measurement, Task<ImageDetailRegion> pending)
    {
        // 既に実行中の低倍率画像を待ち、必要なら拡大後の解像度で更新する。複数要求も再確認時に共有する。
        try { await pending; }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested) { }
        return await LoadFullPageAsync(page, displayScale, measurement);
    }

    private async Task<ImageDetailRegion> LoadFullPageCoreAsync(ImagePage page, ImageLoadMeasurement? measurement, FullPageWork work)
    {
        bool acquired = false;
        try
        {
            measurement?.StartStage("DecodeQueue");
            await DecodeQueue.WaitAsync(work.Cancellation.Token, ImageDecodePriority.FullPage);
            acquired = true;
            work.Cancellation.Token.ThrowIfCancellationRequested();
            var request = work.Request;
            var bounds = new Rect(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height);
            if (Details.Get(page.Index, bounds, request.DecodeScale / ViewerSettings.DetailOversamplingFactor) is { } cached)
                return cached;
            work.Started = true;
            // ②のページ全体画像も、表示倍率の1.8倍（元解像度が上限）で生成して保存する。
            var region = await ImageLoader.LoadFullDetailAsync(Path, page.Index, request, measurement, work.Cancellation.Token);
            work.Cancellation.Token.ThrowIfCancellationRequested();
            // 待機中の表示領域要求へデコード枠を渡す前に、完成画像をキャッシュへ登録する。
            Details.Add(region);
            return region;
        }
        finally
        {
            if (acquired) DecodeQueue.Release();
            _fullPages.Remove(page.Index);
            work.Cancellation.Dispose();
        }
    }

    public async Task<BitmapSource> LoadCropAsync(int index, Int32Rect rectangle)
    {
        await DecodeQueue.WaitAsync(Token);
        try { return await ImageLoader.LoadCropAsync(Path, index, rectangle, Token); }
        finally { DecodeQueue.Release(); }
    }

    public async Task SaveCropAsync(int index, Int32Rect rectangle, string outputPath)
    {
        await DecodeQueue.WaitAsync(Token);
        try { await ImageLoader.SaveCropAsync(Path, index, rectangle, outputPath, Token); }
        finally { DecodeQueue.Release(); }
    }

    public async Task<CropPng> LoadCropPngAsync(int index, Int32Rect rectangle)
    {
        await DecodeQueue.WaitAsync(Token);
        try { return await ImageLoader.LoadCropPngAsync(Path, index, rectangle, Token); }
        finally { DecodeQueue.Release(); }
    }
}
