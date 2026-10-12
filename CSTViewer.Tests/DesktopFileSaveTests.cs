using System.IO;

namespace CSTViewer.Tests;

public sealed class DesktopFileSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, $"desktop-save-{Guid.NewGuid():N}");

    public DesktopFileSaveTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("複数ページ 画像.TIFF")]
    [InlineData("scan.tif")]
    [InlineData("image.png")]
    [InlineData("image.jpeg")]
    [InlineData("image.bmp")]
    [InlineData("image.gif")]
    public async Task Copy_PreservesEveryByteFilenameAndModifiedTime(string name)
    {
        string source = Path.Combine(_directory, name);
        string desktop = Directory.CreateDirectory(Path.Combine(_directory, "Desktop")).FullName;
        byte[] bytes = new byte[ViewerSettings.DesktopSaveBufferSize * 2 + 37];
        new Random(123).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        DateTime modified = new(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        string target = DesktopFileSave.TargetPath(source, desktop);

        Assert.Equal(Path.Combine(desktop, name), await DesktopFileSave.CopyAsync(source, target, false));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(target));
        Assert.Single(Directory.GetFiles(desktop));
    }

    [Fact]
    public async Task ExistingFile_RequiresOverwriteAndFailedCopyLeavesItUntouched()
    {
        string source = Path.Combine(_directory, "source.tif"), target = Path.Combine(_directory, "target.tif");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(target, [4, 5, 6]);
        await Assert.ThrowsAsync<IOException>(() => DesktopFileSave.CopyAsync(source, target, false));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.GetFiles(_directory, ".CSTViewer-save-*.tmp"));
        Assert.Equal(target, await DesktopFileSave.CopyAsync(source, target, true));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task SameFile_LeavesContentAndTimestampUnchanged()
    {
        string source = Path.Combine(_directory, "source.tif");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        DateTime modified = File.GetLastWriteTimeUtc(source);
        Assert.Equal(source.ToUpperInvariant(), await DesktopFileSave.CopyAsync(source, source.ToUpperInvariant(), true));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(source));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(source));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingSourceDirectoryCollisionAndCancellation_DoNotCreateFiles()
    {
        string source = Path.Combine(_directory, "source.tif"), target = Path.Combine(_directory, "target.tif");
        await Assert.ThrowsAsync<FileNotFoundException>(() => DesktopFileSave.CopyAsync(source, target, false));
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        Directory.CreateDirectory(target);
        await Assert.ThrowsAsync<IOException>(() => DesktopFileSave.CopyAsync(source, target, true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DesktopFileSave.CopyAsync(source, Path.Combine(_directory, "cancelled.tif"), false, cancellation.Token));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task LockedDestination_PreservesExistingFileAndRemovesTemporaryFile()
    {
        string source = Path.Combine(_directory, "source.tif"), target = Path.Combine(_directory, "target.tif");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await File.WriteAllBytesAsync(target, [4, 5, 6]);
        using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Exception? exception = await Record.ExceptionAsync(() => DesktopFileSave.CopyAsync(source, target, true));
            // Windowsの保存先や共有設定によって、共有違反はアクセス拒否として返ることもある。
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.GetFiles(_directory, ".CSTViewer-save-*.tmp"));
    }

    [Fact]
    public void DefaultDirectory_UsesWindowsDesktopWhenAvailable()
    {
        Assert.True(Directory.Exists(DesktopFileSave.DefaultDirectory));
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(desktop)) Assert.Equal(desktop, DesktopFileSave.DefaultDirectory);
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
