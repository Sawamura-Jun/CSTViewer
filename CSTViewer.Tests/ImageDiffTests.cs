using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer.Tests;

public class ImageDiffTests
{
    [Fact]
    public void Compare_UsesRedForAddedBlueForDeletedAndPreservesUnchangedPixels()
    {
        using var oldImage = Rgb(6, 1, [255, 255, 255, 0, 0, 0, 50, 50, 50, 255, 0, 0, 20, 40, 60, 255, 255, 255]);
        using var newImage = Rgb(6, 1, [0, 0, 0, 255, 255, 255, 50, 50, 50, 0, 255, 0, 20, 40, 60, 255, 255, 255]);
        using var result = ImageDiff.Compare(oldImage, newImage);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255, 50, 50, 50, 255, 0, 0, 20, 40, 60, 255, 255, 255 },
            result.WriteToMemory<byte>());
    }

    [Fact]
    public void Normalize_ConvertsGrayAndTransparentPixelsToRgbOnWhite()
    {
        using var gray = VipsImage.NewFromMemory(new byte[] { 0, 127, 255 }, 3, 1, 1, Enums.BandFormat.Uchar);
        using var rgb = ImageDiff.Normalize(gray);
        Assert.Equal(new byte[] { 0, 0, 0, 127, 127, 127, 255, 255, 255 }, rgb.WriteToMemory<byte>());
        using var rgba = VipsImage.NewFromMemory(new byte[] { 0, 0, 0, 0, 10, 20, 30, 255 }, 2, 1, 4,
            Enums.BandFormat.Uchar);
        using var srgb = rgba.Copy(interpretation: Enums.Interpretation.Srgb);
        using var flattened = ImageDiff.Normalize(srgb);
        Assert.Equal(new byte[] { 255, 255, 255, 10, 20, 30 }, flattened.WriteToMemory<byte>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_PreservesPageSizesAndTreatsMissingPagesAsWhite(bool extraOldPage)
    {
        string directory = CreateDirectory();
        try
        {
            string oldPath = Path.Combine(directory, "old.tif"), newPath = Path.Combine(directory, "new.tif");
            var oldPages = new List<(int, int, byte[])> { (2, 1, [0, 0, 0, 255, 255, 255]), (1, 2, [0, 0, 0, 0, 0, 0]) };
            var newPages = new List<(int, int, byte[])> { (3, 1, [255, 255, 255, 0, 0, 0, 0, 0, 0]), (2, 1, [255, 255, 255, 0, 0, 0]) };
            (extraOldPage ? oldPages : newPages).Add((1, 1, [0, 0, 0]));
            SavePages(oldPath, oldPages.ToArray());
            SavePages(newPath, newPages.ToArray());
            string output = Path.Combine(directory, "result.tiff");
            var updates = new List<DiffProgress>();
            await ImageDiff.CreateAsync(oldPath, newPath, output, new CaptureProgress(updates.Add));
            using var first = VipsImage.Tiffload(output, page: 0, n: 1);
            Assert.Equal(3, (int)first.Get("n-pages"));
            Assert.Equal((3, 1), (first.Width, first.Height));
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 0, 0 }, first.WriteToMemory<byte>());
            using var second = VipsImage.Tiffload(output, page: 1, n: 1);
            Assert.Equal((2, 2), (second.Width, second.Height));
            Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 0, 0, 255, 255, 255, 255 }, second.WriteToMemory<byte>());
            using var third = VipsImage.Tiffload(output, page: 2, n: 1);
            Assert.Equal(extraOldPage ? new byte[] { 0, 0, 255 } : new byte[] { 255, 0, 0 }, third.WriteToMemory<byte>());
            for (int index = 0; index < 3; index++)
            {
                var encoding = TiffEncoding.Read(output, index);
                Assert.True(encoding.IsBigTiff);
                Assert.Equal(5, encoding.Compression);
                Assert.Equal(8, encoding.BitsPerSample);
            }
            Assert.Equal(100, updates[^1].Percent);
            Assert.Empty(Directory.GetFiles(directory, ".CSTViewer-diff-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreateAsync_IdenticalPageAddsCenteredRedMessage()
    {
        string directory = CreateDirectory();
        try
        {
            string source = Path.Combine(directory, "same.tif"), output = Path.Combine(directory, "diff.tif");
            byte[] white = Enumerable.Repeat((byte)255, 600 * 300 * 3).ToArray();
            SavePages(source, (600, 300, white));
            await ImageDiff.CreateAsync(source, source, output);
            using var result = VipsImage.Tiffload(output);
            byte[] pixels = result.WriteToMemory<byte>();
            Assert.Equal(white[..3], pixels[..3]);
            var changed = Enumerable.Range(0, 600 * 300).Where(i => pixels[i * 3 + 1] < 255).ToArray();
            Assert.NotEmpty(changed);
            Assert.All(changed, i => Assert.Equal(255, pixels[i * 3]));
            Assert.InRange(changed.Average(i => i % 600), 290, 310);
            Assert.InRange(changed.Average(i => i / 600), 140, 160);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreateAsync_MultipleStripsRoundTripAndReplacesExistingOutput()
    {
        string directory = CreateDirectory();
        try
        {
            const int width = 301, height = 513;
            byte[] old = Enumerable.Repeat((byte)255, width * height * 3).ToArray();
            byte[] next = (byte[])old.Clone();
            for (int y = 0; y < height; y++) next[(y * width + y % width) * 3] = 0;
            string oldPath = Path.Combine(directory, "old.tif"), newPath = Path.Combine(directory, "new.tif");
            SavePages(oldPath, (width, height, old), (width, height + 1, Enumerable.Repeat((byte)255, width * (height + 1) * 3).ToArray()));
            SavePages(newPath, (width, height, next));
            string output = Path.Combine(directory, "out.tif");
            File.WriteAllText(output, "previous output");
            await ImageDiff.CreateAsync(oldPath, newPath, output);
            using var page = VipsImage.Tiffload(output, page: 0, n: 1);
            byte[] result = page.WriteToMemory<byte>();
            for (int y = 0; y < height; y++)
            {
                int changed = (y * width + y % width) * 3;
                Assert.Equal(new byte[] { 255, 0, 0 }, result[changed..(changed + 3)]);
            }
            using var last = VipsImage.Tiffload(output, page: 1, n: 1);
            Assert.Equal((width, height + 1), (last.Width, last.Height));
            Assert.All(last.WriteToMemory<byte>(), value => Assert.Equal(255, value));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreateAsync_RejectsSourceOverwriteAndKeepsExistingOutputWhenCancelled()
    {
        string directory = CreateDirectory();
        try
        {
            string source = Path.Combine(directory, "source.tif"), output = Path.Combine(directory, "diff.tif");
            SavePages(source, (1, 1, [0, 0, 0]));
            byte[] original = File.ReadAllBytes(source);
            await Assert.ThrowsAsync<ArgumentException>(() => ImageDiff.CreateAsync(source, source, source));
            Assert.Equal(original, File.ReadAllBytes(source));
            File.WriteAllText(output, "previous output");
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageDiff.CreateAsync(source, source, output,
                new CaptureProgress(update => { if (update.Percent >= 5) cancellation.Cancel(); }), cancellation.Token));
            Assert.Equal("previous output", File.ReadAllText(output));
            Assert.Empty(Directory.GetFiles(directory, ".CSTViewer-diff-*"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Writer_AppendsPagesFromTheSameExclusiveHandleAndDeletesScratchOnClose()
    {
        string directory = CreateDirectory();
        string scratch = Path.Combine(directory, "scratch.tmp"), output = Path.Combine(directory, "result.tif");
        try
        {
            using (var writer = new DiffTiffWriter(output))
            using (var page = new FileStream(scratch, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                ViewerSettings.DiffCopyBufferSize, FileOptions.DeleteOnClose))
            {
                foreach (var size in new[] { (Width: 3, Height: 1), (Width: 2, Height: 3) })
                {
                    page.Position = 0;
                    page.SetLength(0);
                    using var image = Rgb(size.Width, size.Height,
                        Enumerable.Repeat((byte)127, size.Width * size.Height * 3).ToArray());
                    image.TiffsaveStream(page, compression: ViewerSettings.DiffTiffCompression,
                        bigtiff: true, keep: Enums.ForeignKeep.None);
                    // 他のハンドルからは開く・削除する操作を拒否したまま、結合だけが成功する。
                    Assert.Throws<IOException>(() => { using var other = File.OpenRead(scratch); });
                    Assert.Throws<IOException>(() => File.Delete(scratch));
                    writer.Append(page, CancellationToken.None);
                    Assert.True(page.CanRead);
                }
            }
            Assert.False(File.Exists(scratch));
            using var first = VipsImage.Tiffload(output, page: 0, n: 1);
            using var second = VipsImage.Tiffload(output, page: 1, n: 1);
            Assert.Equal(2, (int)first.Get("n-pages"));
            Assert.Equal((3, 1), (first.Width, first.Height));
            Assert.Equal((2, 3), (second.Width, second.Height));
            Assert.All(first.WriteToMemory<byte>(), value => Assert.Equal(127, value));
            Assert.All(second.WriteToMemory<byte>(), value => Assert.Equal(127, value));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreateAsync_RepeatedSavesDoNotExposeTemporaryPageTiffs()
    {
        string directory = CreateDirectory();
        try
        {
            string oldPath = Path.Combine(directory, "old.tif"), newPath = Path.Combine(directory, "new.tif");
            SavePages(oldPath, (2, 1, [0, 0, 0, 255, 255, 255]), (1, 2, [0, 0, 0, 255, 255, 255]));
            SavePages(newPath, (2, 1, [255, 255, 255, 0, 0, 0]), (1, 2, [255, 255, 255, 0, 0, 0]));
            string output = Path.Combine(directory, "result.tif");
            bool exposed = false;
            var progress = new CaptureProgress(_ => exposed |= Directory.GetFiles(directory, "*.page.tif").Length > 0);
            for (int run = 0; run < 5; run++)
            {
                await ImageDiff.CreateAsync(oldPath, newPath, output, progress);
                using var first = VipsImage.Tiffload(output, page: 0, n: 1);
                using var second = VipsImage.Tiffload(output, page: 1, n: 1);
                Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0 }, first.WriteToMemory<byte>());
                Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0 }, second.WriteToMemory<byte>());
                Assert.Empty(Directory.GetFiles(directory, ".CSTViewer-diff-*"));
            }
            Assert.False(exposed);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CreateAsync_CleanupSharingViolationDoesNotHideOriginalFailure()
    {
        string directory = CreateDirectory();
        FileStream? locked = null;
        try
        {
            string oldPath = Path.Combine(directory, "old.tif"), newPath = Path.Combine(directory, "new.tif");
            SavePages(oldPath, (1, 1, [0, 0, 0]));
            SavePages(newPath, (1, 1, [255, 255, 255]));
            string output = Path.Combine(directory, "result.tif");
            File.WriteAllText(output, "previous output");
            const string originalError = "保存確定前の検証用エラー";
            var progress = new CaptureProgress(update =>
            {
                if (update.Percent != 96) return;
                // 読み取りハンドルで後始末の共有違反を再現し、先に起きた例外が残ることを確かめる。
                locked = File.OpenRead(Assert.Single(Directory.GetFiles(directory, ".CSTViewer-diff-*.tif")));
                throw new InvalidDataException(originalError);
            });
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ImageDiff.CreateAsync(oldPath, newPath, output, progress));
            Assert.Equal(originalError, exception.Message);
            Assert.NotNull(locked);
            Assert.Equal("previous output", File.ReadAllText(output));
        }
        finally { locked?.Dispose(); Directory.Delete(directory, true); }
    }

    internal static string CreateDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-diff-{Guid.NewGuid():N}")).FullName;

    internal static void SavePages(string path, params (int Width, int Height, byte[] Pixels)[] pages)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        foreach (var page in pages)
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(page.Width, page.Height, 96, 96,
                PixelFormats.Rgb24, null, page.Pixels, page.Width * 3)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static VipsImage Rgb(int width, int height, byte[] pixels)
    {
        using var image = VipsImage.NewFromMemory(pixels, width, height, 3, Enums.BandFormat.Uchar);
        return image.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private sealed class CaptureProgress(Action<DiffProgress> report) : IProgress<DiffProgress>
    {
        public void Report(DiffProgress value) => report(value);
    }
}
