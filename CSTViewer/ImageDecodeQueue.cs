namespace CSTViewer;

internal enum ImageDecodePriority { Visible, Preview, FullPage }

/// <summary>表示領域、他ページのプレビュー、ページ全体の順にデコード枠を割り当てる。</summary>
internal sealed class ImageDecodeQueue(SemaphoreSlim gate)
{
    private sealed class Waiter(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
    }

    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _foreground = new();
    private readonly LinkedList<Waiter> _fullPages = new();
    private readonly LinkedList<Waiter> _preloads = new();
    private bool _dispatching;

    public Task WaitAsync(CancellationToken token, bool isPreload = false) =>
        WaitAsync(token, isPreload ? ImageDecodePriority.Preview : ImageDecodePriority.Visible);

    public async Task WaitAsync(CancellationToken token, ImageDecodePriority priority)
    {
        token.ThrowIfCancellationRequested();
        var waiter = new Waiter(token);
        bool startDispatch;
        lock (_sync)
        {
            var queue = priority switch
            {
                ImageDecodePriority.Visible => _foreground,
                ImageDecodePriority.FullPage => _fullPages,
                _ => _preloads
            };
            waiter.Node = queue.AddLast(waiter);
            startDispatch = !_dispatching;
            _dispatching = true;
        }
        // 待機中のページ変更やウィンドウ終了で、不要になった要求を直ちに外す。
        using var registration = token.Register(() => Cancel(waiter));
        if (startDispatch) _ = DispatchAsync();
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    public void Release() => gate.Release();

    private void Cancel(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is { } node)
            {
                node.List!.Remove(node);
                waiter.Node = null;
            }
        }
        waiter.Completion.TrySetCanceled(waiter.Token);
    }

    private async Task DispatchAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_foreground.Count == 0 && _fullPages.Count == 0 && _preloads.Count == 0)
                {
                    _dispatching = false;
                    return;
                }
            }

            // 実行中のデコードを待ってから選ぶことで、その間に届いた高優先度の要求も先に通す。
            // ConfigureAwait(false)は、UIスレッドの処理待ちで読み込み順が滞らないための指定。
            await gate.WaitAsync().ConfigureAwait(false);
            Waiter? next = null;
            lock (_sync)
            {
                var queue = _foreground.Count > 0 ? _foreground : _preloads.Count > 0 ? _preloads : _fullPages;
                if (queue.First is { } node)
                {
                    queue.RemoveFirst();
                    next = node.Value;
                    next.Node = null;
                }
            }
            // 選択とキャンセルが重なった場合は、取得したデコード枠を次の要求へ返す。
            if (next is null || !next.Completion.TrySetResult()) gate.Release();
        }
    }
}
