using System.Windows;

namespace CSTViewer;

/// <summary>② 表示用高精細画像をページごとに保存し、同じ必要解像度のページ全体画像で先行領域を置き換える。</summary>
internal sealed class ImageDetailRegionCache(int pageLimit, long byteLimit = long.MaxValue)
{
    private readonly LinkedList<ImageDetailRegion> _regions = new();
    public int Count => _regions.Count;
    public long ByteSize { get; private set; }

    public ImageDetailRegion? Get(int pageIndex, Rect visible, double displayScale)
    {
        for (var node = _regions.First; node is not null; node = node.Next)
        {
            if (!node.Value.Covers(pageIndex, visible, displayScale)) continue;
            _regions.Remove(node);
            _regions.AddFirst(node);
            return node.Value;
        }
        return null;
    }

    public ImageDetailRegion? GetForDisplay(int pageIndex, Rect visible)
    {
        for (var node = _regions.First; node is not null; node = node.Next)
        {
            // 各ページの②は1件だけ保持する。解像度が不足していても暫定表示の候補として返す。
            if (!node.Value.ContainsView(pageIndex, visible)) continue;
            _regions.Remove(node);
            _regions.AddFirst(node);
            return node.Value;
        }
        return null;
    }

    public void Add(ImageDetailRegion region)
    {
        if (region.ByteSize > byteLimit) return;
        for (var node = _regions.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.PageIndex == region.PageIndex)
            {
                // 全体画像の完成直後に古い領域読み込みが戻っても、解像度と範囲を後退させない。
                var previous = node.Value;
                // 小さい領域での1画素の丸め差だけで、完成済みのページ全体画像を追い出さない。
                double toleranceX = 1d / Math.Min(previous.Rectangle.Width, region.Rectangle.Width);
                double toleranceY = 1d / Math.Min(previous.Rectangle.Height, region.Rectangle.Height);
                if (previous.Bounds.Contains(region.Bounds)
                    && previous.Bitmap.PixelWidth / (double)previous.Rectangle.Width + toleranceX >= region.Bitmap.PixelWidth / (double)region.Rectangle.Width
                    && previous.Bitmap.PixelHeight / (double)previous.Rectangle.Height + toleranceY >= region.Bitmap.PixelHeight / (double)region.Rectangle.Height)
                {
                    _regions.Remove(node);
                    _regions.AddFirst(node);
                    return;
                }
                Remove(node);
            }
            node = next;
        }
        _regions.AddFirst(region);
        ByteSize += region.ByteSize;
        while ((pageLimit > 0 && _regions.Count > pageLimit) || ByteSize > byteLimit) Remove(_regions.Last!);
    }

    private void Remove(LinkedListNode<ImageDetailRegion> node)
    {
        ByteSize -= node.Value.ByteSize;
        _regions.Remove(node);
    }
}
