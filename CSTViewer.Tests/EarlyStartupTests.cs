using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace CSTViewer.Tests;

public class EarlyStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingOwner_ReceivesRequestWithoutStartingWpf(bool withImage)
    {
        string name = PipeName();
        string[]? received = null;
        using var owner = SingleInstanceBroker.TryStart(name, paths => received = paths);
        Assert.NotNull(owner);
        string[] paths = withImage ? [@"C:\画像 フォルダー\一枚目.tif"] : [];

        int exitCode = Program.Run(paths, name,
            (_, _, _) => throw new InvalidOperationException("転送中にWPFを初期化しました。"),
            () => throw new InvalidOperationException("転送に失敗しました。"));

        Assert.Equal(0, exitCode);
        Assert.Equal(paths, received);
    }

    [Fact]
    public void FirstLaunch_OwnsPipeBeforeUiAndReplaysRequestsInArrivalOrder()
    {
        string name = PipeName();
        string[] initial = [@"C:\images\first.tif"];
        var received = new List<string[]>();

        int exitCode = Program.Run(initial, name, (_, requests, paths) =>
        {
            Assert.Same(initial, paths);
            using var otherOwner = SingleInstanceBroker.TryStart(name, _ => Assert.Fail("受信役が二重に起動しました。"));
            Assert.Null(otherOwner);
            SingleInstanceBroker.ForwardAsync(name, [@"C:\images\second.tif"]).GetAwaiter().GetResult();
            SingleInstanceBroker.ForwardAsync(name, []).GetAwaiter().GetResult();
            Assert.Empty(received);

            requests.Attach(received.Add);
            SingleInstanceBroker.ForwardAsync(name, [@"C:\images\third.tif"]).GetAwaiter().GetResult();
            return 0;
        }, () => throw new InvalidOperationException("初回起動に失敗しました。"));

        Assert.Equal(0, exitCode);
        Assert.Equal(3, received.Count);
        Assert.Equal(new[] { @"C:\images\second.tif" }, received[0]);
        Assert.Empty(received[1]);
        Assert.Equal(new[] { @"C:\images\third.tif" }, received[2]);
        using var nextOwner = SingleInstanceBroker.TryStart(name, _ => { });
        Assert.NotNull(nextOwner);
    }

    [Fact]
    public async Task ConcurrentLaunches_StartOneUiAndKeepEveryRequestDuringInitialization()
    {
        string name = PipeName();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<string[]>();
        int uiStarts = 0;
        Task<int> first = Task.Run(() => Program.Run([], name, (_, requests, _) =>
        {
            Interlocked.Increment(ref uiStarts);
            ready.SetResult();
            // UIが準備できる前に、別プロセス相当の要求をまとめて送る。
            release.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            requests.Attach(received.Enqueue);
            return 0;
        }, () => 1));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            int[] exitCodes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
                Program.Run([$@"C:\images\{index}.tif"], name,
                    (_, _, _) => { Interlocked.Increment(ref uiStarts); return 1; }, () => 1))));
            Assert.All(exitCodes, code => Assert.Equal(0, code));
        }
        finally { release.TrySetResult(); }

        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(1, uiStarts);
        Assert.Equal(8, received.Count);
        Assert.Equal(8, received.Select(paths => Assert.Single(paths)).Distinct().Count());
    }

    [Fact]
    public async Task OwnerExitsDuringForwarding_NewProcessTakesOwnership()
    {
        string name = PipeName();
        using var oldOwner = CreatePipe(name);
        Task closing = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await oldOwner.WaitForConnectionAsync(timeout.Token);
            await oldOwner.ReadExactlyAsync(new byte[sizeof(int)], timeout.Token);
            // 応答を返す前に終了し、転送の失敗後に受信口が空く状況を再現する。
            oldOwner.Dispose();
        });
        bool uiStarted = false;

        int exitCode = Program.Run([], name, (_, _, _) => { uiStarted = true; return 0; }, () => 1);

        await closing;
        Assert.Equal(0, exitCode);
        Assert.True(uiStarted);
    }

    [Fact]
    public async Task ForwardingFailsWhileOwnerRemains_DoesNotStartIndependentUi()
    {
        string name = PipeName();
        using var oldOwner = CreatePipe(name);
        Task rejecting = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await oldOwner.WaitForConnectionAsync(timeout.Token);
            await oldOwner.ReadExactlyAsync(new byte[sizeof(int)], timeout.Token);
            await oldOwner.ReadExactlyAsync(new byte[2], timeout.Token); // 空配列のJSONは[]の2バイト。
            await oldOwner.WriteAsync(new byte[] { 0 }, timeout.Token);
        });
        bool errorShown = false;

        int exitCode = Program.Run([], name,
            (_, _, _) => throw new InvalidOperationException("独立したUIを起動しました。"),
            () => { errorShown = true; return 1; });

        await rejecting;
        Assert.Equal(1, exitCode);
        Assert.True(errorShown);
    }

    [Fact]
    public void UiInitializationThrows_ReleasesPipeForNextLaunch()
    {
        string name = PipeName();
        Assert.Throws<InvalidOperationException>(() => Program.Run([], name,
            (_, _, _) => throw new InvalidOperationException("初期化失敗"), () => 1));

        using var nextOwner = SingleInstanceBroker.TryStart(name, _ => { });
        Assert.NotNull(nextOwner);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executable_ForwardsWithOnlyCoreRuntimeAndWithoutWpfOrVips(bool withImages)
    {
        string directory = Path.Combine(Path.GetTempPath(), "CSTViewer 起動確認 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? process = null;
        try
        {
            string assembly = typeof(Program).Assembly.Location;
            string sourceDirectory = Path.GetDirectoryName(assembly)!;
            string stem = Path.GetFileNameWithoutExtension(assembly);
            foreach (string extension in new[] { ".exe", ".dll", ".deps.json" })
                File.Copy(Path.Combine(sourceDirectory, stem + extension), Path.Combine(directory, stem + extension));
            // WPFを利用できない実行環境にし、生成されたMainやUI設定の初期化も検出する。
            File.WriteAllText(Path.Combine(directory, stem + ".runtimeconfig.json"), """
                {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
                """);
            string name = SingleInstanceBroker.GetApplicationPipeName(directory + Path.DirectorySeparatorChar);
            var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var owner = SingleInstanceBroker.TryStart(name, paths => received.TrySetResult(paths));
            Assert.NotNull(owner);
            var startInfo = new ProcessStartInfo(Path.Combine(directory, stem + ".exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = directory,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (withImages)
            {
                startInfo.ArgumentList.Add("一枚目.TIF");
                startInfo.ArgumentList.Add("二枚目.png");
                startInfo.ArgumentList.Add("ignore.txt");
            }
            process = Process.Start(startInfo)!;
            Task<string> errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(process.ExitCode == 0, await errors);
            string[] expected = withImages ? [Path.Combine(directory, "一枚目.TIF"), Path.Combine(directory, "二枚目.png")] : [];
            Assert.Equal(expected, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                process.Dispose();
            }
            // 作成した4ファイルだけを削除し、再帰的な削除は行わない。
            string stem = Path.GetFileNameWithoutExtension(typeof(Program).Assembly.Location);
            foreach (string extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
                File.Delete(Path.Combine(directory, stem + extension));
            Directory.Delete(directory);
        }
    }

    private static NamedPipeServerStream CreatePipe(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);

    private static string PipeName() => "CSTViewer.EarlyStartup.Tests." + Guid.NewGuid().ToString("N");
}
