using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>ファイルごとの先読み・キャッシュ・キャンセルを管理する。</summary>
internal sealed class ImageLoadSession
{
    // ファイルを切り替えた直後も含め、重いデコードが重ならないよう全セッションで共有する。
    private static readonly SemaphoreSlim DecodeGate = new(ViewerSettings.ImageDecodeConcurrency);
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
    public PageImageCache Details { get; } = new(ViewerSettings.DetailCachePageLimit, ViewerSettings.DetailCacheByteLimit);
    public BitmapMipmapCache Mipmaps { get; } = new(ViewerSettings.MipmapCacheBitmapLimit, ViewerSettings.MipmapCacheByteLimit);

    public ImageLoadSession(string path, Size previewSize)
    {
        Path = path;
        PreviewSize = previewSize;
        Token = _cancellation.Token;
    }

    public async Task<ImagePage> LoadPageAsync(int index, Size size, ImageLoadMeasurement? measurement)
    {
        measurement?.StartStage("DecodeQueue");
        await DecodeGate.WaitAsync(Token);
        try
        {
            return await ImageLoader.LoadPageAsync(Path, index, size, measurement, Token);
        }
        finally
        {
            DecodeGate.Release();
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

    public async Task<BitmapSource> LoadCropAsync(int index, Int32Rect rectangle)
    {
        await DecodeGate.WaitAsync(Token);
        try { return await ImageLoader.LoadCropAsync(Path, index, rectangle, Token); }
        finally { DecodeGate.Release(); }
    }

    public async Task SaveCropAsync(int index, Int32Rect rectangle, string outputPath)
    {
        await DecodeGate.WaitAsync(Token);
        try { await ImageLoader.SaveCropAsync(Path, index, rectangle, outputPath, Token); }
        finally { DecodeGate.Release(); }
    }

    public async Task<CropPng> LoadCropPngAsync(int index, Int32Rect rectangle)
    {
        await DecodeGate.WaitAsync(Token);
        try { return await ImageLoader.LoadCropPngAsync(Path, index, rectangle, Token); }
        finally { DecodeGate.Release(); }
    }
}
