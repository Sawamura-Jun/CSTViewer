namespace CSTViewer;

/// <summary>WPFの初期化中に届いた要求を、到着順でUIへ引き渡す。</summary>
internal sealed class StartupRequestBuffer
{
    private readonly object _gate = new();
    private readonly Queue<string[]> _pending = new();
    private Action<string[]>? _receive;

    public void Enqueue(string[] paths)
    {
        lock (_gate)
        {
            if (_receive is null) _pending.Enqueue(paths);
            else _receive(paths);
        }
    }

    public void Attach(Action<string[]> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        lock (_gate)
        {
            if (_receive is not null) throw new InvalidOperationException("The startup receiver is already attached.");
            _receive = receive;
            // 引き渡し中の新しい要求も同じロックで待たせ、到着順を維持する。
            while (_pending.TryDequeue(out string[]? paths)) _receive(paths);
        }
    }
}
