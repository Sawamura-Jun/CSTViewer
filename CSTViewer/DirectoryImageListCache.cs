using System.IO;

namespace CSTViewer;

/// <summary>表示中のフォルダーの一覧と、取得中の処理を再利用する。</summary>
internal sealed class DirectoryImageListCache : IDisposable
{
    private readonly Func<string, CancellationToken, DirectoryImageList> _load;
    private string? _directory;
    private Task<DirectoryImageList>? _images;
    private CancellationTokenSource? _cancellation;

    public bool IsLoading => _images is { IsCompleted: false };

    public DirectoryImageListCache(Func<string, CancellationToken, DirectoryImageList>? load = null)
    {
        _load = load ?? DirectoryImageList.Create;
    }

    public Task<DirectoryImageList> GetAsync(string currentPath, bool refresh = false)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(currentPath))!;
        if (!string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase) || refresh)
        {
            Clear();
            _directory = directory;
        }
        if (!ViewerSettings.SupportedImageExtensions.Contains(Path.GetExtension(currentPath)))
            return Task.FromResult(DirectoryImageList.Empty);
        if (_images is null)
        {
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            // 画像セッションとは独立させ、同一フォルダーの画像変更では取得を中断しない。
            _images = Task.Run(() => _load(currentPath, token), token);
        }
        return _images;
    }

    private void Clear()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _images = null;
        _directory = null;
    }

    public void Dispose() => Clear();
}
