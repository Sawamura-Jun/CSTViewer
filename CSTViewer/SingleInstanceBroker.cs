using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CSTViewer;

/// <summary>追加起動の画像パスを、同じログオン中の起動済みアプリへ渡す。</summary>
internal sealed class SingleInstanceBroker : IDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action<string[]> _receive;
    private readonly Task _listener;
    private int _disposed;

    private SingleInstanceBroker(NamedPipeServerStream server, Action<string[]> receive)
    {
        _server = server;
        _receive = receive;
        _listener = Task.Run(ListenAsync);
    }

    internal static string ApplicationPipeName => GetApplicationPipeName(AppContext.BaseDirectory);

    internal static string GetApplicationPipeName(string applicationDirectory)
    {
        // ユーザー・ログオンセッション・配置先が違うアプリとは混ぜない。
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        string scope = $"{identity.User?.Value}|{process.SessionId}|{applicationDirectory.ToUpperInvariant()}";
        return "CSTViewer.Open." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
    }

    internal static SingleInstanceBroker? TryStart(string pipeName, Action<string[]> receive)
    {
        try
        {
            // FirstPipeInstanceで同時起動時にも受信役を1プロセスだけに限定する。
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            return new SingleInstanceBroker(server, receive);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static Task ForwardAsync(string pipeName, string[] paths, CancellationToken cancellationToken = default) =>
        ForwardAsync(pipeName, paths, AllowSetForegroundWindow, cancellationToken);

    internal static async Task ForwardAsync(string pipeName, string[] paths,
        Func<uint, bool> allowSetForegroundWindow, CancellationToken cancellationToken = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(paths);
        if (paths.Length > StartupSettings.RequestPathLimit || payload.Length > StartupSettings.RequestByteLimit)
            throw new ArgumentException("Too many images requested at once.", nameof(paths));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StartupSettings.RequestTimeoutMilliseconds);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        // 起動操作を受けた追加プロセスから、接続先だけに前面化の許可を渡す。
        // 受信直後のActivateに間に合わせるため送信前に行い、許可されなくても画像は開く。
        if (GetNamedPipeServerProcessId(client.SafePipeHandle, out uint processId))
            _ = allowSetForegroundWindow(processId);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        var acknowledgement = new byte[1];
        await client.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
        if (acknowledgement[0] != 1) throw new IOException("The request to open the images was not accepted.");
        await client.WriteAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(StartupSettings.RequestTimeoutMilliseconds);
                var header = new byte[sizeof(int)];
                await _server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > StartupSettings.RequestByteLimit) continue;
                var payload = new byte[length];
                await _server.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
                string[]? paths = JsonSerializer.Deserialize<string[]>(payload);
                if (paths is null || paths.Length > StartupSettings.RequestPathLimit || paths.Any(string.IsNullOrWhiteSpace))
                    continue;
                // 読み込み完了を待たず、UIのキューに入った時点で追加プロセスを終了できる。
                _receive(paths);
                await _server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                // 相手が応答を読み終えるまで接続を保ち、未読の応答が切断時に失われるのを防ぐ。
                await _server.ReadExactlyAsync(new byte[1], timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or JsonException or OperationCanceledException or ObjectDisposedException)
            {
                // 送信側の終了や不正な要求でも、次の「プログラムから開く」を受け付け続ける。
            }
            finally
            {
                try
                {
                    if (!_shutdown.IsCancellationRequested && _server.IsConnected) _server.Disconnect();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                    // 終了処理が接続の後始末と重なった場合も、受信ループを安全に終了する。
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _server.Dispose();
        // 受信ループがトークンを使い終えてから解放する。
        _ = _listener.ContinueWith(_ => _shutdown.Dispose(), TaskScheduler.Default);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
