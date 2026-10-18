using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

/// <summary>ページごとの1/2、1/4などの縮小画像を、最近使ったページから保持する。</summary>
internal sealed class BitmapMipmapCache(int pageLimit, long byteLimit = long.MaxValue)
{
    private sealed record Entry(int PageIndex, WeakReference<BitmapSource> Source, int Level, BitmapSource Bitmap, long Bytes);
    private readonly LinkedList<Entry> _entries = new();
    private readonly LinkedList<int> _pages = new();
    public long ByteSize { get; private set; }
    public int Count => _entries.Count;
    public int PageCount => _pages.Count;

    public static int RequiredLevel(double scale)
    {
        int level = 0;
        double effectiveScale = Math.Max(scale, 1e-6);
        while (effectiveScale < ViewerSettings.MipmapMinimumDisplayScale)
        {
            level++;
            effectiveScale *= 2;
        }
        return level;
    }

    public BitmapSource? Get(BitmapSource source, int level, int pageIndex = 0)
    {
        if (level == 0)
        {
            lock (_entries)
            {
                if (_pages.Contains(pageIndex)) TouchPage(pageIndex);
            }
            return source;
        }
        lock (_entries)
        {
            RemoveExpiredEntries();
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.PageIndex != pageIndex || node.Value.Level != level || !Matches(node.Value, source)) continue;
                _entries.Remove(node);
                _entries.AddFirst(node);
                TouchPage(pageIndex);
                return node.Value.Bitmap;
            }
            return null;
        }
    }

    public async Task<BitmapSource> GetOrCreateAsync(BitmapSource source, int level, CancellationToken token, int pageIndex = 0)
    {
        token.ThrowIfCancellationRequested();
        if (Get(source, level, pageIndex) is { } cached) return cached;
        BitmapSource resizeSource = source;
        int nearestLevel = 0;
        lock (_entries)
        {
            // 既存の1/2画像から1/4画像を作るなど、最も近い段階を再利用する。
            LinkedListNode<Entry>? nearest = null;
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                var entry = node.Value;
                if (entry.PageIndex != pageIndex || entry.Level <= nearestLevel || entry.Level >= level || !Matches(entry, source)) continue;
                nearestLevel = entry.Level;
                resizeSource = entry.Bitmap;
                nearest = node;
            }
            if (nearest is not null) { _entries.Remove(nearest); _entries.AddFirst(nearest); }
        }
        int divisor = 1 << level;
        var size = new Size(Math.Max(1, source.PixelWidth / divisor), Math.Max(1, source.PixelHeight / divisor));
        var bitmap = await BitmapResizer.ResizeAsync(resizeSource, size, token);
        token.ThrowIfCancellationRequested();
        long bytes = ((bitmap.PixelWidth * (long)bitmap.Format.BitsPerPixel + 7) / 8) * bitmap.PixelHeight;
        lock (_entries)
        {
            // 弱参照により、詳細キャッシュから外れた大きな元画像を保持し続けない。
            RemoveExpiredEntries();
            if (bytes <= byteLimit)
            {
                for (var node = _entries.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.PageIndex == pageIndex && node.Value.Level == level && Matches(node.Value, source)) Remove(node);
                    node = next;
                }
                _entries.AddFirst(new Entry(pageIndex, new WeakReference<BitmapSource>(source), level, bitmap, bytes));
                ByteSize += bytes;
                TouchPage(pageIndex);
                while (_pages.Count > pageLimit) RemovePage(_pages.Last!.Value);
                while (ByteSize > byteLimit) Remove(_entries.Last!);
            }
        }
        return bitmap;
    }

    private static bool Matches(Entry entry, BitmapSource source) =>
        entry.Source.TryGetTarget(out var original) && ReferenceEquals(original, source);

    private void RemoveExpiredEntries()
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (!node.Value.Source.TryGetTarget(out _)) Remove(node);
            node = next;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        int pageIndex = node.Value.PageIndex;
        ByteSize -= node.Value.Bytes;
        _entries.Remove(node);
        if (!_entries.Any(entry => entry.PageIndex == pageIndex)) _pages.Remove(pageIndex);
    }

    private void TouchPage(int pageIndex)
    {
        _pages.Remove(pageIndex);
        _pages.AddFirst(pageIndex);
    }

    private void RemovePage(int pageIndex)
    {
        for (var node = _entries.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.PageIndex == pageIndex) Remove(node);
            node = next;
        }
    }
}
