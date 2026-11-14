using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer.Tests;

public class ImageLoaderTests
{
    [Theory]
    [InlineData("sample.TIF")]
    [InlineData("sample.tiff")]
    [InlineData("sample.PNG")]
    [InlineData("sample.jpeg")]
    [InlineData("sample.jpg")]
    [InlineData("sample.bmp")]
    [InlineData("sample.gif")]
    [InlineData("sample.WebP")]
    public void FindFirstSupportedPath_AcceptsKnownExtensions(string path)
    {
        Assert.Equal(path, ImageLoader.FindFirstSupportedPath([path]));
    }

    [Fact]
    public void FindFirstSupportedPath_PicksFirstImageInDropOrder()
    {
        Assert.Equal("first.tif", ImageLoader.FindFirstSupportedPath(
            ["notes.txt", "first.tif", "second.png"]));
        Assert.Null(ImageLoader.FindFirstSupportedPath(["notes.txt", "folder"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Webp_LoadsWithAlphaAndSavesCropWithoutFurtherLoss(bool lossless)
    {
        string path = TemporaryPath(".WEBP"), output = TemporaryPath(".webp");
        try
        {
            using var raw = VipsImage.NewFromMemory(new byte[] { 5, 15, 25, 255, 20, 40, 60, 127, 30, 60, 90, 0 },
                3, 1, 4, Enums.BandFormat.Uchar);
            using var source = raw.Copy(interpretation: Enums.Interpretation.Srgb);
            source.Webpsave(path, lossless: lossless, exact: true);

            Assert.Equal(new Size(3, 1), ImageLoader.ReadSourceSize(path));
            BitmapSource loaded = await ImageLoader.LoadAsync(path);
            Assert.True(loaded.IsFrozen);
            Assert.Equal(PixelFormats.Bgra32, loaded.Format);
            var rectangle = new Int32Rect(1, 0, 2, 1);
            var expected = new byte[8];
            loaded.CopyPixels(rectangle, expected, expected.Length, 0);
            Assert.Equal(127, expected[3]);
            Assert.Equal(0, expected[7]);
            if (lossless) Assert.Equal(new byte[] { 60, 40, 20, 127, 90, 60, 30, 0 }, expected);

            var crop = await ImageLoader.LoadCropAsync(path, 0, rectangle);
            var croppedPixels = new byte[8];
            crop.CopyPixels(croppedPixels, croppedPixels.Length, 0);
            Assert.Equal(expected, croppedPixels);

            await ImageLoader.SaveCropAsync(path, 0, rectangle, output);
            BitmapSource saved = await ImageLoader.LoadAsync(output);
            Assert.Equal((2, 1), (saved.PixelWidth, saved.PixelHeight));
            var savedPixels = new byte[8];
            saved.CopyPixels(savedPixels, savedPixels.Length, 0);
            Assert.Equal(expected, savedPixels);
            using var exclusiveStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(TiffCompressOption.None)]
    [InlineData(TiffCompressOption.Lzw)]
    [InlineData(TiffCompressOption.Zip)]
    public async Task LoadAsync_ReadsFirstTiffPageAndReleasesFile(TiffCompressOption compression)
    {
        string path = TemporaryPath(".tiff");
        try
        {
            var encoder = new TiffBitmapEncoder { Compression = compression };
            encoder.Frames.Add(BitmapFrame.Create(CreateImage(80, 60)));
            encoder.Frames.Add(BitmapFrame.Create(CreateImage(40, 30)));
            SaveImage(path, encoder);

            var measurement = new ImageLoadMeasurement(path);
            BitmapSource image = await ImageLoader.LoadAsync(path, measurement);
            measurement.FinishStage();

            Assert.Equal(80, image.PixelWidth);
            Assert.Equal(60, image.PixelHeight);
            Assert.True(image.IsFrozen);
            Assert.Equal(new FileInfo(path).Length, measurement.FileSizeBytes);
            Assert.Equal(80, measurement.PixelWidth);
            Assert.Equal(60, measurement.PixelHeight);
            Assert.Equal(image.Format.ToString(), measurement.PixelFormat);
            Assert.Contains("WorkerQueue", measurement.StageMilliseconds.Keys);
            Assert.Contains("FileOpen", measurement.StageMilliseconds.Keys);
            Assert.Contains("DecodeAndRead", measurement.StageMilliseconds.Keys);
            Assert.Contains("Freeze", measurement.StageMilliseconds.Keys);
            Assert.Contains("FileClose", measurement.StageMilliseconds.Keys);
            Assert.Contains("UiDispatch", measurement.StageMilliseconds.Keys);
            Assert.All(measurement.StageMilliseconds.Values, duration => Assert.True(duration >= 0));
            // 排他アクセスできれば、表示中の画像が元ファイルをロックしていない。
            using var exclusiveStream = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            var pixels = new byte[80 * 60 * 3];
            image.CopyPixels(pixels, 80 * 3, 0);
            Assert.Equal(0x80, pixels[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_ReadsMonochromeCcittTiff()
    {
        string path = TemporaryPath(".tif");
        try
        {
            var pixels = Enumerable.Repeat((byte)0xAA, 10 * 60).ToArray();
            var image = BitmapSource.Create(80, 60, 96, 96, PixelFormats.BlackWhite,
                null, pixels, 10);
            image.Freeze();
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Ccitt4 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            SaveImage(path, encoder);

            BitmapSource loaded = await ImageLoader.LoadAsync(path);

            Assert.Equal(80, loaded.PixelWidth);
            // libvipsの縮小処理は1bitの原稿をGray8へ展開する。白黒の極性も検証する。
            Assert.Equal(PixelFormats.Gray8, loaded.Format);
            var loadedPixels = new byte[80 * 60];
            loaded.CopyPixels(loadedPixels, 80, 0);
            for (int x = 0; x < loadedPixels.Length; x++)
                Assert.Equal(x % 2 == 0 ? (byte)255 : (byte)0, loadedPixels[x]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_PreservesPixelDimensionsForHighDpiImage()
    {
        string path = TemporaryPath(".png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CreateImage(80, 60, 192)));
            SaveImage(path, encoder);

            BitmapSource loaded = await ImageLoader.LoadAsync(path);

            Assert.Equal(80, loaded.PixelWidth);
            Assert.Equal(60, loaded.PixelHeight);
            Assert.Equal(192, loaded.DpiX, 1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_RejectsInvalidImageAndReleasesFile()
    {
        string path = TemporaryPath(".tif");
        try
        {
            File.WriteAllText(path, "これは画像ではありません");

            var measurement = new ImageLoadMeasurement(path);
            await Assert.ThrowsAnyAsync<Exception>(() => ImageLoader.LoadAsync(path, measurement));
            Assert.Equal("DecodeAndRead", measurement.ActiveStage);

            using var exclusiveStream = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static BitmapSource CreateImage(int width, int height, double dpi = 96)
    {
        byte[] pixels = Enumerable.Repeat((byte)0x80, width * height * 3).ToArray();
        var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Rgb24,
            null, pixels, width * 3);
        image.Freeze();
        return image;
    }

    private static string TemporaryPath(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"CSTViewer-{Guid.NewGuid()}{extension}");
    }

    private static void SaveImage(string path, BitmapEncoder encoder)
    {
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
