namespace CSTViewer;

/// <summary>最近使ったページを優先し、指定されたページ数まで保持する。</summary>
internal sealed class PageImageCache(int pageLimit, long byteLimit = long.MaxValue)
{
    private readonly LinkedList<ImagePage> _pages = new();
    public long ByteSize { get; private set; }
    public int Count => _pages.Count;

    public ImagePage? Get(int index)
    {
        var node = Find(index);
        if (node is null) return null;
        _pages.Remove(node);
        _pages.AddFirst(node);
        return node.Value;
    }

    public void Add(ImagePage page)
    {
        if (Find(page.Index) is { } previous) Remove(previous);
        if (page.ByteSize > byteLimit) return;
        _pages.AddFirst(page);
        ByteSize += page.ByteSize;
        while (_pages.Count > pageLimit || ByteSize > byteLimit) Remove(_pages.Last!);
    }

    private LinkedListNode<ImagePage>? Find(int index)
    {
        for (var node = _pages.First; node is not null; node = node.Next)
            if (node.Value.Index == index) return node;
        return null;
    }

    private void Remove(LinkedListNode<ImagePage> node)
    {
        ByteSize -= node.Value.ByteSize;
        _pages.Remove(node);
    }
}
