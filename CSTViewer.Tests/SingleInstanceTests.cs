using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;

namespace CSTViewer.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void StartupPaths_ResolvesEveryImageUsingTheLaunchingDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "起動元 フォルダー");
        string fullPath = Path.Combine(directory, "別の画像.tiff");
        string[] paths = StartupImagePaths.Resolve(new[]
        {
            "\"一枚目.TIF\"", new Uri(fullPath).AbsoluteUri, "二枚目.png", "ignore.txt", "", "bad\0.tif"
        }, directory);

        Assert.Equal(new[] { Path.Combine(directory, "一枚目.TIF"), fullPath, Path.Combine(directory, "二枚目.png") }, paths);
    }

    [Fact]
    public async Task ExtraLaunch_ForwardsEveryPathToOneOwnerIncludingUnicodeAndSpaces()
    {
        string name = PipeName();
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = SingleInstanceBroker.TryStart(name, paths => received.TrySetResult(paths));
        Assert.NotNull(owner);
        using var secondOwner = SingleInstanceBroker.TryStart(name, _ => Assert.Fail("受信役が二重に起動しました。"));
        Assert.Null(secondOwner);
        string[] paths = { @"C:\画像 フォルダー\一枚目.tif", @"C:\画像 フォルダー\二枚目.tiff" };

        await SingleInstanceBroker.ForwardAsync(name, paths);

        Assert.Equal(paths, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentLaunches_AreAllReceivedWithoutReplacingEarlierRequests()
    {
        string name = PipeName();
        var received = new ConcurrentQueue<string[]>();
        using var owner = SingleInstanceBroker.TryStart(name, received.Enqueue);
        Assert.NotNull(owner);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            SingleInstanceBroker.ForwardAsync(name, new[] { $@"C:\images\{index}.tif" })));

        Assert.Equal(12, received.Count);
        Assert.Equal(12, received.Select(paths => Assert.Single(paths)).Distinct().Count());
    }

    [Fact]
    public async Task LaunchWithoutPaths_IsReceivedAsAnActivationRequest()
    {
        string name = PipeName();
        string[]? received = null;
        using var owner = SingleInstanceBroker.TryStart(name, paths => received = paths);
        Assert.NotNull(owner);

        await SingleInstanceBroker.ForwardAsync(name, Array.Empty<string>());

        Assert.NotNull(received);
        Assert.Empty(received);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExtraLaunch_GrantsForegroundPermissionToPipeOwnerBeforeDeliveringRequest(
        bool withImage, bool permissionAllowed)
    {
        string name = PipeName();
        uint permittedProcessId = 0;
        uint permittedProcessIdAtDelivery = 0;
        int permissionRequests = 0;
        string[]? received = null;
        using var owner = SingleInstanceBroker.TryStart(name, paths =>
        {
            permittedProcessIdAtDelivery = permittedProcessId;
            received = paths;
        });
        Assert.NotNull(owner);
        string[] paths = withImage ? [@"C:\画像 フォルダー\追加画像.tif"] : [];

        // OSの許可結果に左右されず、許可先・送信順序・要求の転送を検証する。
        await SingleInstanceBroker.ForwardAsync(name, paths, processId =>
        {
            permissionRequests++;
            permittedProcessId = processId;
            return permissionAllowed;
        });

        Assert.Equal(1, permissionRequests);
        Assert.Equal((uint)Environment.ProcessId, permittedProcessId);
        Assert.Equal(permittedProcessId, permittedProcessIdAtDelivery);
        // 前面化が拒否された場合も、画像付き・画像なしの起動要求を失わない。
        Assert.Equal(paths, received);
    }

    [Fact]
    public async Task InvalidRequest_IsRejectedAndTheNextImageCanStillBeOpened()
    {
        string name = PipeName();
        string[]? received = null;
        using var owner = SingleInstanceBroker.TryStart(name, paths => received = paths);
        Assert.NotNull(owner);
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, ViewerSettings.StartupRequestByteLimit + 1);
            await client.WriteAsync(header, timeout.Token);
            Assert.Equal(0, await client.ReadAsync(new byte[1], timeout.Token));
        }

        await SingleInstanceBroker.ForwardAsync(name, new[] { @"C:\images\next.tif" });

        Assert.Equal(new[] { @"C:\images\next.tif" }, received);
    }

    [Fact]
    public async Task ClosingOwner_ReleasesTheNameSoAnotherLaunchCanBecomeTheOwner()
    {
        string name = PipeName();
        var owner = SingleInstanceBroker.TryStart(name, _ => { });
        Assert.NotNull(owner);
        owner.Dispose();
        owner.Dispose();
        using var nextOwner = SingleInstanceBroker.TryStart(name, _ => { });
        Assert.NotNull(nextOwner);

        await SingleInstanceBroker.ForwardAsync(name, new[] { @"C:\images\reopened.tif" });
    }

    [Fact]
    public async Task MissingOwner_CanBeCancelledWithoutStartingAnIndependentViewer()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SingleInstanceBroker.ForwardAsync(PipeName(), new[] { @"C:\images\one.tif" }, cancellation.Token));
    }

    private static string PipeName() => "CSTViewer.Tests." + Guid.NewGuid().ToString("N");
}
