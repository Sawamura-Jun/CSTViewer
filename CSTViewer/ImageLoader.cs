using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using VipsImage = NetVips.Image;

namespace CSTViewer;

internal static class ImageLoader
{
    // Lazyは初期化をTIFFのワーカー処理まで遅らせ、複数スレッドでも一度だけ実行する。
    private static readonly Lazy<bool> VipsInitialized = new(() =>
    {
        // 操作キャッシュはファイルを保持するため、表示画像だけを別途キャッシュする。
        Cache.Max = 0;
        return true;
    });

    internal static void InitializeVips() => _ = VipsInitialized.Value;

    public static string? FindFirstSupportedPath(IEnumerable<string> paths) => paths.FirstOrDefault(path =>
        ViewerSettings.SupportedImageExtensions.Contains(Path.GetExtension(path)));

    public static bool IsTiff(string path) => Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    public static Size ReadSourceSize(string path)
    {
        // 起動時の寸法決定では全画素を展開せず、先頭ページのヘッダーだけを読む。
        if (IsTiff(path))
        {
            _ = VipsInitialized.Value;
            using var source = VipsImage.Tiffload(path, page: 0, n: 1, autorotate: true,
                access: Enums.Access.Sequential, revalidate: true);
            return new Size(source.Width, source.Height);
        }

        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return new Size(decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    public static async Task<BitmapSource> LoadAsync(string path, ImageLoadMeasurement? measurement = null) =>
        (await LoadPageAsync(path, 0, new Size(int.MaxValue, int.MaxValue), measurement)).Bitmap;

    public static BitmapSource Load(string path, ImageLoadMeasurement? measurement = null) =>
        LoadPage(path, 0, new Size(int.MaxValue, int.MaxValue), measurement, CancellationToken.None).Bitmap;

    public static Task<ImagePage> LoadPageAsync(string path, int pageIndex, Size decodeSize,
        ImageLoadMeasurement? measurement = null, CancellationToken cancellationToken = default)
    {
        measurement?.StartStage("WorkerQueue");
        // ページ単位でワーカーへ渡し、先頭ページの表示を全ページの完了まで待たせない。
        return Task.Run(() =>
        {
            ImagePage page = LoadPage(path, pageIndex, decodeSize, measurement, cancellationToken);
            measurement?.StartStage("UiDispatch");
            return page;
        }, cancellationToken);
    }

    private static ImagePage LoadPage(string path, int pageIndex, Size decodeSize,
        ImageLoadMeasurement? measurement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (decodeSize.Width <= 0 || decodeSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(decodeSize));
        measurement?.StartStage("FileOpen");
        long fileLength = new FileInfo(path).Length;
        if (measurement is not null)
        {
            measurement.FileSizeBytes = fileLength;
            measurement.PageIndex = pageIndex;
            measurement.Backend = IsTiff(path) ? "NetVips" : "WPF";
        }
        measurement?.StartStage("DecodeAndRead");
        ImagePage result = IsTiff(path)
            ? LoadTiffPage(path, pageIndex, decodeSize, measurement, cancellationToken)
            : LoadOtherImage(path, pageIndex, measurement, cancellationToken);
        measurement?.FinishStage();
        return result;
    }

    private static ImagePage LoadTiffPage(string path, int pageIndex, Size decodeSize,
        ImageLoadMeasurement? measurement, CancellationToken cancellationToken)
    {
        _ = VipsInitialized.Value;
        // n=1で異なるページ寸法にも対応する。Sequentialは上から順に画素を処理する指定。
        using var source = VipsImage.Tiffload(path, page: pageIndex, n: 1, autorotate: true,
            access: Enums.Access.Sequential, revalidate: true);
        int pageCount = source.GetTypeOf("n-pages") != 0 ? (int)source.Get("n-pages") : 1;
        double scale = Math.Min(1, Math.Min(decodeSize.Width / source.Width, decodeSize.Height / source.Height));
        // 遅延評価により、縮小結果だけをWPFへ実体化する。元画像全体のBitmapは作らない。
        using var resized = scale < 1 ? source.Resize(scale, kernel: Enums.Kernel.Linear) : source.Copy();
        var bitmap = CreateTiffBitmap(resized, measurement, cancellationToken);
        SetMetadata(measurement, bitmap, source.Width, source.Height, pageCount);
        measurement?.StartStage("FileClose");
        return new ImagePage(pageIndex, pageCount, source.Width, source.Height, bitmap);
    }

    public static Task<ImageDetailRegion> LoadDetailRegionAsync(string path, int pageIndex,
        ImageDetailRequest request, ImageLoadMeasurement? measurement = null,
        CancellationToken cancellationToken = default)
    {
        measurement?.StartStage("WorkerQueue");
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
            if (!IsTiff(path)) throw new ArgumentException("Detail regions require a TIFF image.", nameof(path));
            if (!double.IsFinite(request.DecodeScale) || request.DecodeScale <= 0 || request.DecodeScale > 1)
                throw new ArgumentOutOfRangeException(nameof(request));
            measurement?.StartStage("FileOpen");
            if (measurement is not null)
            {
                measurement.FileSizeBytes = new FileInfo(path).Length;
                measurement.PageIndex = pageIndex;
                measurement.Backend = "NetVips";
                measurement.RegionX = request.Rectangle.X;
                measurement.RegionY = request.Rectangle.Y;
                measurement.RegionWidth = request.Rectangle.Width;
                measurement.RegionHeight = request.Rectangle.Height;
            }
            _ = VipsInitialized.Value;
            measurement?.StartStage("DecodeAndRead");
            using var source = VipsImage.Tiffload(path, page: pageIndex, n: 1, autorotate: true,
                access: Enums.Access.Random, revalidate: true);
            ValidateCrop(request.Rectangle, source.Width, source.Height);
            cancellationToken.ThrowIfCancellationRequested();
            // 元画像を切り抜いてから縮小し、画面外の画素をWPF用Bitmapへ展開しない。
            using var cropped = source.Crop(request.Rectangle.X, request.Rectangle.Y,
                request.Rectangle.Width, request.Rectangle.Height);
            using var resized = request.DecodeScale < 1
                ? cropped.Resize(request.DecodeScale, kernel: Enums.Kernel.Linear) : cropped.Copy();
            var bitmap = CreateTiffBitmap(resized, measurement, cancellationToken);
            int pageCount = source.GetTypeOf("n-pages") != 0 ? (int)source.Get("n-pages") : 1;
            SetMetadata(measurement, bitmap, source.Width, source.Height, pageCount);
            measurement?.StartStage("UiDispatch");
            return new ImageDetailRegion(pageIndex, request.Rectangle, bitmap);
        }, cancellationToken);
    }

    public static async Task<ImageDetailRegion> LoadFullDetailAsync(string path, int pageIndex,
        ImageDetailRequest request, ImageLoadMeasurement? measurement = null,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(request.DecodeScale) || request.DecodeScale <= 0 || request.DecodeScale > 1
            || request.Rectangle.X != 0 || request.Rectangle.Y != 0
            || request.Rectangle.Width <= 0 || request.Rectangle.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(request));
        // 全体モードでは従来のSequential読み込みを使い、ページ全体を一度に生成する。
        var size = new Size(Math.Max(1, request.Rectangle.Width * request.DecodeScale),
            Math.Max(1, request.Rectangle.Height * request.DecodeScale));
        var page = await LoadPageAsync(path, pageIndex, size, measurement, cancellationToken);
        if (measurement is not null)
        {
            measurement.RegionX = measurement.RegionY = 0;
            measurement.RegionWidth = page.SourceWidth;
            measurement.RegionHeight = page.SourceHeight;
        }
        return new ImageDetailRegion(pageIndex, new Int32Rect(0, 0, page.SourceWidth, page.SourceHeight), page.Bitmap);
    }

    public static Task<BitmapSource> LoadCropAsync(string path, int pageIndex, Int32Rect rectangle,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsTiff(path))
        {
            var page = LoadOtherImage(path, pageIndex, null, cancellationToken);
            ValidateCrop(rectangle, page.SourceWidth, page.SourceHeight);
            var bitmap = new CroppedBitmap(page.Bitmap, rectangle);
            bitmap.Freeze();
            return (BitmapSource)bitmap;
        }
        _ = VipsInitialized.Value;
        // Randomは任意の領域を読む指定。プレビューを拡大せず、元のページから切り抜く。
        using var source = VipsImage.Tiffload(path, page: pageIndex, n: 1, autorotate: true,
            access: Enums.Access.Random, revalidate: true);
        ValidateCrop(rectangle, source.Width, source.Height);
        using var cropped = source.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
        return CreateTiffBitmap(cropped, null, cancellationToken);
    }, cancellationToken);

    public static Task SaveCropAsync(string path, int pageIndex, Int32Rect rectangle, string outputPath,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(outputPath).ToLowerInvariant();
        CropSaveFormat.ValidateOutput(path, outputPath);
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a save location different from the source image.", nameof(outputPath));

        // 同じフォルダーの一時ファイルに書き、成功後に置き換えて保存失敗による破損を防ぐ。
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory,
            $".CSTViewer-crop-{Guid.NewGuid():N}{extension}");
        try
        {
            if (IsTiff(path))
            {
                TiffEncoding encoding = TiffEncoding.Read(path, pageIndex);
                _ = VipsInitialized.Value;
                using var source = VipsImage.Tiffload(path, page: pageIndex, n: 1, autorotate: true,
                    access: Enums.Access.Random, revalidate: true);
                ValidateCrop(rectangle, source.Width, source.Height);
                using var cropped = source.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                cancellationToken.ThrowIfCancellationRequested();
                // 表示用の8bit化を通さず、元ページの圧縮形式・階調で保存する。
                encoding.Save(cropped, temporaryPath);
                if (TiffEncoding.Read(temporaryPath, 0).Compression != encoding.Compression)
                    throw new NotSupportedException("Cannot save using the source image's TIFF compression format.");
            }
            else if (extension is ".jpg" or ".jpeg")
            {
                if (pageIndex != 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
                _ = VipsInitialized.Value;
                bool progressive = CropSaveFormat.IsProgressiveJpeg(path);
                using var source = VipsImage.Jpegload(path, autorotate: false,
                    access: Enums.Access.Random, revalidate: true);
                ValidateCrop(rectangle, source.Width, source.Height);
                using var cropped = source.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                // JPEGは同じDCT圧縮とプログレッシブ設定で再エンコードする。
                cropped.Jpegsave(temporaryPath, interlace: progressive, q: ViewerSettings.CropJpegQuality);
            }
            else
            {
                BitmapSource bitmap = await LoadCropAsync(path, pageIndex, rectangle, cancellationToken);
                BitmapEncoder encoder = extension switch
                {
                    ".png" => new PngBitmapEncoder(),
                    ".bmp" => new BmpBitmapEncoder(),
                    ".gif" => new GifBitmapEncoder(),
                    _ => throw new NotSupportedException("Cannot save in the same format as the source image.")
                };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(temporaryPath)) encoder.Save(stream);
                if (extension == ".bmp" && CropSaveFormat.ReadBmpCompression(path) != CropSaveFormat.ReadBmpCompression(temporaryPath))
                    throw new NotSupportedException("Cannot save using the source image's BMP compression format.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }, cancellationToken);

    public static Task<CropPng> LoadCropPngAsync(string path, int pageIndex, Int32Rect rectangle,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] png;
        bool saveAlongsideTiff = false;
        if (IsTiff(path))
        {
            _ = VipsInitialized.Value;
            TiffEncoding encoding = TiffEncoding.Read(path, pageIndex);
            using var source = VipsImage.Tiffload(path, page: pageIndex, n: 1, autorotate: true,
                access: Enums.Access.Random, revalidate: true);
            ValidateCrop(rectangle, source.Width, source.Height);
            using var cropped = source.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
            // 表示用の8bit化を避ける。白黒TIFFはPython版と同じ1bit PNGでコピーする。
            bool monochrome = encoding.BitsPerSample == 1 && encoding.Photometric is 0 or 1;
            png = cropped.PngsaveBuffer(compression: ViewerSettings.CropPngCompression, bitdepth: monochrome ? 1 : null);
            saveAlongsideTiff = encoding.Compression == 5;
        }
        else
        {
            BitmapSource bitmap = await LoadCropAsync(path, pageIndex, rectangle, cancellationToken);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            png = stream.ToArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new CropPng(png, saveAlongsideTiff);
    }, cancellationToken);

    public static async Task SavePngAsync(byte[] png, string outputPath, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".CSTViewer-crop-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, png, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static void ValidateCrop(Int32Rect rectangle, int width, int height)
    {
        if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0
            || (long)rectangle.X + rectangle.Width > width || (long)rectangle.Y + rectangle.Height > height)
            throw new ArgumentOutOfRangeException(nameof(rectangle), "The selection is outside the image bounds.");
    }

    private static BitmapSource CreateTiffBitmap(VipsImage image, ImageLoadMeasurement? measurement,
        CancellationToken cancellationToken)
    {
        // Python版と同じ順序で8bit化してから、必要な色空間だけsRGBへ変換する。
        using var bytesImage = image.Format == Enums.BandFormat.Uchar ? image.Copy() : image.Cast(Enums.BandFormat.Uchar);
        using var colour = bytesImage.Bands == 1 || bytesImage.Interpretation is Enums.Interpretation.Bw
            or Enums.Interpretation.Srgb or Enums.Interpretation.Rgb16
            ? bytesImage.Copy() : bytesImage.Colourspace(Enums.Interpretation.Srgb);
        using var normalized = colour.Format == Enums.BandFormat.Uchar ? colour.Copy() : colour.Cast(Enums.BandFormat.Uchar);
        using var displayImage = normalized.Bands switch
        {
            1 or 3 or 4 => normalized.Copy(),
            2 => GrayAlphaToRgba(normalized),
            _ => normalized.ExtractBand(0, n: 4)
        };
        PixelFormat format = displayImage.Bands switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Rgb24,
            _ => PixelFormats.Bgra32
        };
        // libvipsのRGBAを、WPFが扱うBGRAのバンド順へ並べ替える。
        using var ordered = displayImage.Bands == 4 ? RgbaToBgra(displayImage) : displayImage.Copy();
        cancellationToken.ThrowIfCancellationRequested();
        byte[] pixels = ordered.WriteToMemory<byte>();
        cancellationToken.ThrowIfCancellationRequested();
        measurement?.StartStage("BitmapCreate");
        int stride = checked(ordered.Width * ordered.Bands);
        // Createは画素をコピーするので、libvipsの画像を破棄しても安全に表示できる。
        var bitmap = BitmapSource.Create(ordered.Width, ordered.Height, 96, 96, format, null, pixels, stride);
        measurement?.StartStage("Freeze");
        bitmap.Freeze();
        return bitmap;
    }

    private static VipsImage GrayAlphaToRgba(VipsImage image)
    {
        using var gray = image.ExtractBand(0);
        using var alpha = image.ExtractBand(1);
        return gray.Bandjoin([gray, gray, alpha]);
    }

    private static VipsImage RgbaToBgra(VipsImage image)
    {
        using var blue = image.ExtractBand(2);
        using var green = image.ExtractBand(1);
        using var red = image.ExtractBand(0);
        using var alpha = image.ExtractBand(3);
        return blue.Bandjoin([green, red, alpha]);
    }

    private static ImagePage LoadOtherImage(string path, int pageIndex,
        ImageLoadMeasurement? measurement, CancellationToken cancellationToken)
    {
        if (pageIndex != 0) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        // TIFF以外は従来のWPF対応形式とDPI情報を維持する。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        cancellationToken.ThrowIfCancellationRequested();
        measurement?.StartStage("Freeze");
        bitmap.Freeze();
        SetMetadata(measurement, bitmap, bitmap.PixelWidth, bitmap.PixelHeight, 1);
        measurement?.StartStage("FileClose");
        return new ImagePage(0, 1, bitmap.PixelWidth, bitmap.PixelHeight, bitmap);
    }

    private static void SetMetadata(ImageLoadMeasurement? measurement, BitmapSource bitmap,
        int width, int height, int pageCount)
    {
        if (measurement is null) return;
        measurement.PixelWidth = bitmap.PixelWidth;
        measurement.PixelHeight = bitmap.PixelHeight;
        measurement.PixelFormat = bitmap.Format.ToString();
        measurement.SourceWidth = width;
        measurement.SourceHeight = height;
        measurement.PageCount = pageCount;
    }
}

internal sealed record CropPng(byte[] Bytes, bool SaveAlongsideTiff);
