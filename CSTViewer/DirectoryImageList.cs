using System.IO;

namespace CSTViewer;

/// <summary>同じフォルダーにあるTIFFを、ファイル名順に移動するための一覧。</summary>
internal sealed class DirectoryImageList
{
    public static DirectoryImageList Empty { get; } = new([], new(StringComparer.OrdinalIgnoreCase), -1);
    private readonly string[] _paths;
    private readonly Dictionary<string, int> _indexes;
    private readonly int _currentIndex;

    private DirectoryImageList(string[] paths, Dictionary<string, int> indexes, int currentIndex)
    {
        _paths = paths;
        _indexes = indexes;
        _currentIndex = currentIndex;
    }

    public static DirectoryImageList Create(string currentPath, CancellationToken cancellationToken = default)
    {
        // Python版と同じく、ファイル移動の対象はTIFFだけにする。
        if (!ImageLoader.IsTiff(currentPath)) return Empty;
        try
        {
            string fullPath = Path.GetFullPath(currentPath);
            string directory = Path.GetDirectoryName(fullPath)!;
            var paths = new List<string>();
            // EnumerateFilesはサブフォルダーを除外し、ファイルごとの存在確認を省ける。
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ImageLoader.IsTiff(path)) paths.Add(path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Python版の名前順に合わせ、数値順ではなく小文字にした名前を比較する。
            string[] sorted = paths.OrderBy(path => Path.GetFileName(path).ToLowerInvariant(),
                StringComparer.Ordinal).ToArray();
            var indexes = new Dictionary<string, int>(sorted.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sorted.Length; index++) indexes.TryAdd(sorted[index], index);
            return new DirectoryImageList(sorted, indexes, -1).ForPath(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // フォルダーを列挙できなくても、開いた画像の表示・ページ操作は続ける。
            return Empty;
        }
    }

    public DirectoryImageList ForPath(string currentPath)
    {
        // 同じ配列と索引を共有し、大量ファイルでも列挙・ソート・全件検索を繰り返さない。
        int index = _indexes.TryGetValue(Path.GetFullPath(currentPath), out int found) ? found : -1;
        return new DirectoryImageList(_paths, _indexes, index);
    }

    public string? GetNeighbor(int step)
    {
        if (_currentIndex < 0 || step is not (-1 or 1)) return null;
        int index = _currentIndex + step;
        return index >= 0 && index < _paths.Length ? _paths[index] : null;
    }
}
