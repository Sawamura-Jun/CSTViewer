using System.Windows;

namespace CSTViewer;

/// <summary>最近表示した高精細領域を、件数と画素バイト数の上限内で保持する。</summary>
internal sealed class ImageDetailRegionCache(int regionLimit, long byteLimit)
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

    public void Add(ImageDetailRegion region)
    {
        if (region.ByteSize > byteLimit) return;
        for (var node = _regions.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.PageIndex == region.PageIndex && node.Value.Rectangle == region.Rectangle) Remove(node);
            node = next;
        }
        _regions.AddFirst(region);
        ByteSize += region.ByteSize;
        while (_regions.Count > regionLimit || ByteSize > byteLimit) Remove(_regions.Last!);
    }

    private void Remove(LinkedListNode<ImageDetailRegion> node)
    {
        ByteSize -= node.Value.ByteSize;
        _regions.Remove(node);
    }
}
