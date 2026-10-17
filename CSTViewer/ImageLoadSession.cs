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
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; }
    public Size PreviewSize { get; }
    public CancellationToken Token { get; }
    public int PageCount { get; set; }
    public int LoadedPages { get; set; }
    public bool IsPreloading { get; set; } = true;
    public string? Error { get; set; }
    public PageImageCache Previews { get; } = new(ViewerSettings.PreviewCachePageLimit, ViewerSettings.PreviewCacheByteLimit);
    public ImageDetailRegionCache Details { get; } = new(ViewerSettings.DetailCacheRegionLimit, ViewerSettings.DetailCacheByteLimit);
    public BitmapMipmapCache Mipmaps { get; } = new(ViewerSettings.MipmapCacheBitmapLimit, ViewerSettings.MipmapCacheByteLimit);

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
        ImageLoadMeasurement? measurement, CancellationToken cancellationToken, bool useRegion = true)
    {
        // 表示の変更とファイルの切り替えの、どちらでも待機中の要求を中止できるようにする。
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token, cancellationToken);
        measurement?.StartStage("DecodeQueue");
        await DecodeQueue.WaitAsync(cancellation.Token);
        try
        {
            return useRegion
                ? await ImageLoader.LoadDetailRegionAsync(Path, index, request, measurement, cancellation.Token)
                : await ImageLoader.LoadFullDetailAsync(Path, index, request, measurement, cancellation.Token);
        }
        finally { DecodeQueue.Release(); }
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
