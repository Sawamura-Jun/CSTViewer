using System.IO;
using System.Buffers.Binary;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer.Tests;

public class CropImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Crop_LoadsOriginalRegionAndReleasesSource(bool tiff)
    {
        string path = TemporaryPath(tiff ? ".tif" : ".png");
        try
        {
            SavePattern(path, tiff);
            var rectangle = new Int32Rect(13, 21, 17, 11);
            BitmapSource crop = await ImageLoader.LoadCropAsync(path, 0, rectangle);
            Assert.Equal((17, 11), (crop.PixelWidth, crop.PixelHeight));
            Assert.True(crop.IsFrozen);
            var pixels = new byte[17 * 11];
            crop.CopyPixels(pixels, 17, 0);
            for (int y = 0; y < 11; y++)
                for (int x = 0; x < 17; x++) Assert.Equal((byte)(x + 13 + y + 21), pixels[y * 17 + x]);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveCrop_PreservesSourceFormatAndExpectedPixels(bool tiff)
    {
        string path = TemporaryPath(tiff ? ".tif" : ".png");
        string output = TemporaryPath(tiff ? ".tif" : ".png");
        try
        {
            SavePattern(path, tiff);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            BitmapSource actual = await ImageLoader.LoadAsync(output);
            Assert.Equal((17, 11), (actual.PixelWidth, actual.PixelHeight));
            var pixels = new byte[17 * 11];
            actual.CopyPixels(pixels, 17, 0);
            Assert.Equal(34, pixels[0]);
            Assert.Equal(60, pixels[^1]);
            if (tiff) Assert.Equal(TiffEncoding.Read(path, 0).Compression, TiffEncoding.Read(output, 0).Compression);
            using var exclusive = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task SaveTiffCrop_PreservesSixteenBitValues()
    {
        string path = TemporaryPath(".tif");
        string output = TemporaryPath(".tif");
        try
        {
            using (var raw = VipsImage.NewFromMemory(new ushort[] { 128, 256, 4095, 65535 }, 4, 1, 1, Enums.BandFormat.Ushort))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Grey16)) image.Tiffsave(path);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(1, 0, 2, 1), output);
            using var actual = VipsImage.NewFromFile(output);
            Assert.Equal(Enums.BandFormat.Ushort, actual.Format);
            Assert.Equal(new ushort[] { 256, 4095 }, actual.WriteToMemory<ushort>());
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task ClipboardPng_PreservesSixteenBitValues()
    {
        string path = TemporaryPath(".tif");
        try
        {
            using (var raw = VipsImage.NewFromMemory(new ushort[] { 128, 256, 4095, 65535 }, 4, 1, 1, Enums.BandFormat.Ushort))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Grey16)) image.Tiffsave(path);
            CropPng png = await ImageLoader.LoadCropPngAsync(path, 0, new Int32Rect(1, 0, 2, 1));
            Assert.False(png.SaveAlongsideTiff);
            using var actual = VipsImage.NewFromBuffer(png.Bytes);
            Assert.Equal(Enums.BandFormat.Ushort, actual.Format);
            Assert.Equal(new ushort[] { 256, 4095 }, actual.WriteToMemory<ushort>());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(Enums.ForeignTiffCompression.Lzw, true)]
    [InlineData(Enums.ForeignTiffCompression.Ccittfax4, false)]
    public async Task ClipboardPng_MonochromeUsesOneBitAndOnlyLzwRequestsExtraFile(Enums.ForeignTiffCompression compression, bool extra)
    {
        string path = TemporaryPath(".tif");
        try
        {
            using (var image = VipsImage.NewFromMemory(new byte[] { 0, 255, 0, 255 }, 4, 1, 1, Enums.BandFormat.Uchar))
                image.Tiffsave(path, compression: compression, bitdepth: 1, predictor: Enums.ForeignTiffPredictor.None);
            CropPng png = await ImageLoader.LoadCropPngAsync(path, 0, new Int32Rect(1, 0, 2, 1));
            Assert.Equal(extra, png.SaveAlongsideTiff);
            Assert.Equal(1, png.Bytes[24]); // PNGのIHDRに記録されたビット深度を確認する。
            using var actual = VipsImage.NewFromBuffer(png.Bytes);
            Assert.Equal(new byte[] { 255, 0 }, actual.WriteToMemory<byte>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CropTiff_UsesCurrentPageAndOrientedCoordinates()
    {
        string path = TemporaryPath(".tif");
        try
        {
            var encoder = new TiffBitmapEncoder();
            var first = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Gray8, null, new byte[80 * 60], 80);
            encoder.Frames.Add(BitmapFrame.Create(first));
            var metadata = new BitmapMetadata("tiff");
            metadata.SetQuery("/ifd/{ushort=274}", (ushort)6);
            var second = BitmapSource.Create(40, 20, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)179, 40 * 20).ToArray(), 40);
            encoder.Frames.Add(BitmapFrame.Create(second, null, metadata, null));
            using (var stream = File.Create(path)) encoder.Save(stream);
            BitmapSource crop = await ImageLoader.LoadCropAsync(path, 1, new Int32Rect(5, 25, 10, 10));
            var pixels = new byte[100];
            crop.CopyPixels(pixels, 10, 0);
            Assert.All(pixels, value => Assert.Equal(179, value));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InvalidOrCancelledCrop_PreservesOutputAndSource()
    {
        string path = TemporaryPath(".png");
        string output = TemporaryPath(".png");
        try
        {
            SavePattern(path, false);
            byte[] source = File.ReadAllBytes(path);
            byte[] existing = [1, 2, 3, 4];
            File.WriteAllBytes(output, existing);
            await Assert.ThrowsAsync<ArgumentException>(() => ImageLoader.SaveCropAsync(path, 0, new Int32Rect(0, 0, 10, 10), path));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ImageLoader.SaveCropAsync(path, 0, new Int32Rect(79, 59, 2, 2), output));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageLoader.SaveCropAsync(path, 0,
                new Int32Rect(0, 0, 10, 10), output, new CancellationToken(true)));
            Assert.Equal(source, File.ReadAllBytes(path));
            Assert.Equal(existing, File.ReadAllBytes(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    private static string TemporaryPath(string extension) => Path.Combine(AppContext.BaseDirectory, $"CSTViewer-crop-{Guid.NewGuid():N}{extension}");

    [Theory]
    [InlineData(TiffCompressOption.None)]
    [InlineData(TiffCompressOption.Lzw)]
    [InlineData(TiffCompressOption.Zip)]
    [InlineData(TiffCompressOption.Rle)]
    [InlineData(TiffCompressOption.Ccitt3)]
    [InlineData(TiffCompressOption.Ccitt4)]
    public async Task MonochromeTiff_InheritsCompressionAndOneBitPixels(TiffCompressOption compression)
    {
        string path = TemporaryPath(".tif");
        string output = TemporaryPath(".tiff");
        try
        {
            var bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.BlackWhite,
                null, Enumerable.Repeat((byte)0xAA, 10 * 60).ToArray(), 10);
            var encoder = new TiffBitmapEncoder { Compression = compression };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            TiffEncoding original = TiffEncoding.Read(path, 0), saved = TiffEncoding.Read(output, 0);
            Assert.Equal(original.Compression, saved.Compression);
            Assert.Equal(1, saved.BitsPerSample);
            BitmapSource actual = await ImageLoader.LoadAsync(output);
            var pixels = new byte[17 * 11];
            actual.CopyPixels(pixels, 17, 0);
            for (int y = 0; y < 11; y++)
                for (int x = 0; x < 17; x++) Assert.Equal(x % 2 == 0 ? (byte)0 : (byte)255, pixels[y * 17 + x]);
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BigAndClassicTiff_InheritContainerCompressionAndPredictor(bool big)
    {
        string path = TemporaryPath(".tif");
        string output = TemporaryPath(".tif");
        try
        {
            using (var raw = VipsImage.NewFromMemory(Enumerable.Repeat((byte)137, 80 * 60).ToArray(), 80, 60, 1, Enums.BandFormat.Uchar))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Bw))
                image.Tiffsave(path, bigtiff: big, compression: Enums.ForeignTiffCompression.Lzw, predictor: Enums.ForeignTiffPredictor.Horizontal);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            TiffEncoding original = TiffEncoding.Read(path, 0), saved = TiffEncoding.Read(output, 0);
            Assert.Equal(original.IsBigTiff, saved.IsBigTiff);
            Assert.Equal(original.Compression, saved.Compression);
            Assert.Equal(original.Predictor, saved.Predictor);
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task MultipageTiff_InheritsSelectedPagesCompression()
    {
        string path = TemporaryPath(".tif");
        string output = TemporaryPath(".tif");
        try
        {
            // 2ページ目だけ旧Deflateタグにし、先頭ページのタグを誤用しないことを確認する。
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
            var bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Gray8, null, new byte[80 * 60], 80);
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            TiffEncoding second = TiffEncoding.Read(path, 1);
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Write))
            {
                stream.Position = second.CompressionValueOffset;
                byte[] legacy = new byte[2];
                if (second.IsLittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(legacy, 32946);
                else BinaryPrimitives.WriteUInt16BigEndian(legacy, 32946);
                stream.Write(legacy);
            }
            Assert.NotEqual(TiffEncoding.Read(path, 0).Compression, TiffEncoding.Read(path, 1).Compression);
            await ImageLoader.SaveCropAsync(path, 1, new Int32Rect(13, 21, 17, 11), output);
            Assert.Equal(32946, TiffEncoding.Read(output, 0).Compression);
            Assert.Throws<ArgumentOutOfRangeException>(() => TiffEncoding.Read(output, 1));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".gif")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    public async Task OtherFormats_SaveUsingSameContainer(string extension)
    {
        string path = TemporaryPath(extension), output = TemporaryPath(extension);
        try
        {
            var bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Rgb24, null,
                Enumerable.Repeat((byte)137, 80 * 60 * 3).ToArray(), 80 * 3);
            BitmapEncoder encoder = extension switch
            {
                ".png" => new PngBitmapEncoder(), ".bmp" => new BmpBitmapEncoder(),
                ".gif" => new GifBitmapEncoder(), _ => new JpegBitmapEncoder { QualityLevel = 90 }
            };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            using var originalStream = File.OpenRead(path);
            using var savedStream = File.OpenRead(output);
            var original = BitmapDecoder.Create(originalStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            var saved = BitmapDecoder.Create(savedStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
            Assert.Equal(original.CodecInfo.ContainerFormat, saved.CodecInfo.ContainerFormat);
            Assert.Equal((17, 11), (saved.Frames[0].PixelWidth, saved.Frames[0].PixelHeight));
            if (extension == ".bmp") Assert.Equal(CropSaveFormat.ReadBmpCompression(path), CropSaveFormat.ReadBmpCompression(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jpeg_PreservesBaselineOrProgressiveMode(bool progressive)
    {
        string path = TemporaryPath(".jpg"), output = TemporaryPath(".jpg");
        try
        {
            using (var raw = VipsImage.NewFromMemory(Enumerable.Repeat((byte)137, 80 * 60 * 3).ToArray(), 80, 60, 3, Enums.BandFormat.Uchar))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Srgb)) image.Jpegsave(path, interlace: progressive);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            Assert.Equal(progressive, CropSaveFormat.IsProgressiveJpeg(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task DifferentOutputFormat_IsRejectedWithoutOverwritingDestination()
    {
        string path = TemporaryPath(".tif"), output = TemporaryPath(".png");
        try
        {
            SavePattern(path, true);
            File.WriteAllBytes(output, [1, 2, 3]);
            await Assert.ThrowsAsync<ArgumentException>(() => ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task JpegCompressedTiff_InheritsJpegCompression()
    {
        string path = TemporaryPath(".tif"), output = TemporaryPath(".tif");
        try
        {
            using (var raw = VipsImage.NewFromMemory(Enumerable.Repeat((byte)137, 80 * 60 * 3).ToArray(), 80, 60, 3, Enums.BandFormat.Uchar))
            using (var image = raw.Copy(interpretation: Enums.Interpretation.Srgb))
                image.Tiffsave(path, compression: Enums.ForeignTiffCompression.Jpeg);
            await ImageLoader.SaveCropAsync(path, 0, new Int32Rect(13, 21, 17, 11), output);
            Assert.Equal(7, TiffEncoding.Read(output, 0).Compression);
            BitmapSource bitmap = await ImageLoader.LoadAsync(output);
            Assert.Equal((17, 11), (bitmap.PixelWidth, bitmap.PixelHeight));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Theory]
    [InlineData("source.tiff", ".tiff")]
    [InlineData("source.JPEG", ".jpeg")]
    [InlineData("source.gif", ".gif")]
    public void SaveFormat_RecognizesOriginalExtension(string source, string extension)
    {
        Assert.Equal(extension, CropSaveFormat.Extension(source));
    }

    private static void SavePattern(string path, bool tiff)
    {
        var pixels = new byte[80 * 60];
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 80; x++) pixels[y * 80 + x] = (byte)(x + y);
        var bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Gray8, null, pixels, 80);
        BitmapEncoder encoder = tiff ? new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
