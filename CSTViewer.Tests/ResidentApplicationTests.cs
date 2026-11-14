using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace CSTViewer.Tests;

public class ResidentApplicationTests
{
    [Fact]
    public async Task ClosingLastWindow_KeepsProcessAndAcceptsLaterLaunchesWithAndWithoutImages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-resident-{Guid.NewGuid()}.tif");
        WriteTiff(path);
        using var owner = StartViewer(null, path);
        try
        {
            await WaitForAsync(owner, () => owner.MainWindowTitle.Contains(Path.GetFileName(path)));
            await CloseViewerWindowAsync(owner);

            // 画像を指定しない追加起動でも、常駐プロセスに新しい画面を作らせる。
            await RunExtraLaunchAsync();
            await WaitForAsync(owner, () => owner.MainWindowTitle == ViewerSettings.ApplicationTitle);
            await CloseViewerWindowAsync(owner);

            // すべて閉じた状態からTIFFを開き直し、追加プロセスが残らないことも確認する。
            await RunExtraLaunchAsync(path);
            await WaitForAsync(owner, () => owner.MainWindowTitle.Contains(Path.GetFileName(path)));
            await CloseViewerWindowAsync(owner);
            Assert.False(owner.HasExited);
        }
        finally
        {
            // このテストで作ったプロセスだけを終了し、通知領域に常駐を残さない。
            await StopTestProcessAsync(owner);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ClosingStartupImage_AllowsDeletingLaunchDirectoryWhileResident()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("CSTViewer-resident-");
        const string fileName = "startup image.tif";
        string path = Path.Combine(directory.FullName, fileName);
        try
        {
            WriteTiff(path);
            // 画像のフォルダーを作業フォルダーに指定し、相対パスで起動する。
            using var owner = StartViewer(directory.FullName, fileName);
            try
            {
                await WaitForAsync(owner, () => owner.MainWindowTitle.Contains(fileName));
                await CloseViewerWindowAsync(owner);

                File.Delete(path);
                Directory.Delete(directory.FullName);
                Assert.False(owner.HasExited);
            }
            finally { await StopTestProcessAsync(owner); }
        }
        finally
        {
            if (Directory.Exists(directory.FullName))
            {
                File.Delete(path);
                Directory.Delete(directory.FullName);
            }
        }
    }

    private static void WriteTiff(string path)
    {
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(80, 60, 137)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task RunExtraLaunchAsync(params string[] paths)
    {
        using var process = StartViewer(null, paths);
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
        }
        finally { await StopTestProcessAsync(process); }
    }

    private static async Task StopTestProcessAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static Process StartViewer(string? workingDirectory, params string[] paths)
    {
        string executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!
        };
        foreach (string path in paths) startInfo.ArgumentList.Add(path);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the test viewer.");
    }

    private static async Task CloseViewerWindowAsync(Process process)
    {
        process.Refresh();
        Assert.True(process.CloseMainWindow());
        await WaitForAsync(process, () => process.MainWindowHandle == IntPtr.Zero);
    }

    private static async Task WaitForAsync(Process process, Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            process.Refresh();
            Assert.False(process.HasExited, "常駐プロセスが予期せず終了しました。");
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("常駐プロセスの画面操作が時間内に完了しませんでした。");
    }
}
