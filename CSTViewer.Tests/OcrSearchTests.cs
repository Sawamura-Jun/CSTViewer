using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CSTViewer.Tests;

public class OcrSearchTests
{
    private const string TsvHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext";

    [Fact]
    public void Search_NormalizesWidthAndCaseAndKeepsBlankPagesAndWordBounds()
    {
        string tsv = string.Join('\n', TsvHeader,
            "1\t1\t0\t0\t0\t0\t0\t0\t100\t80\t-1\t",
            "1\t2\t0\t0\t0\t0\t0\t0\t200\t100\t-1\t",
            "4\t2\t1\t1\t1\t0\t10\t20\t120\t10\t-1\t",
            "5\t2\t1\t1\t1\t1\t10\t20\t20\t10\t90\tﾊﾟｲﾌﾟ",
            "5\t2\t1\t1\t1\t2\t32\t20\t15\t10\t90\t径",
            "5\t2\t1\t1\t1\t3\t58\t20\t24\t10\t90\tBracket",
            "5\t2\t1\t1\t1\t4\t94\t20\t36\t10\t90\tMOTOR",
            "4\t2\t1\t1\t2\t0\t58\t50\t24\t10\t-1\t",
            "5\t2\t1\t1\t2\t1\t58\t50\t24\t10\t90\tPLATE");
        OcrSearchPage[] pages = TiffOcr.ParseTsv(tsv, [new Size(100, 80), new Size(200, 100)]);

        Rect[][] japanese = OcrTextSearch.FindMatches(pages, "　ﾌﾟ径　", CancellationToken.None);
        Assert.Empty(japanese[0]);
        Assert.Equal(new Rect(0.05, 0.2, 0.185, 0.1), Assert.Single(japanese[1]));
        Rect[][] english = OcrTextSearch.FindMatches(pages, "ｃＫｅＴ ＭＯＴ", CancellationToken.None);
        Assert.Equal(new Rect(0.29, 0.2, 0.36, 0.1), Assert.Single(english[1]));
        Assert.Empty(OcrTextSearch.FindMatches(pages, "motor plate", CancellationToken.None)[1]);
    }

    [Theory]
    [InlineData(2, 100, 80, 1)]
    [InlineData(1, 80, 100, 1)]
    [InlineData(1, 100, 80, 2)]
    public void ParseTsv_RejectsIncorrectPageNumberDimensionsAndCount(int number, int width, int height, int count)
    {
        string tsv = $"{TsvHeader}\n1\t{number}\t0\t0\t0\t0\t0\t0\t{width}\t{height}\t-1\t";
        Size[] sizes = Enumerable.Repeat(new Size(100, 80), count).ToArray();
        Assert.Throws<InvalidDataException>(() => TiffOcr.ParseTsv(tsv, sizes));
    }

    [Fact]
    public void OcrResults_KeepPageHeadingsEmptyPagesAndOriginalText()
    {
        OcrSearchPage[] pages =
        [
            new([[new("BRACKET", Rect.Empty), new("MOTOR", Rect.Empty)],
                [new("設計", Rect.Empty), new("図", Rect.Empty), new("５９．２８", Rect.Empty)]], new Size(200, 100)),
            new([[]], new Size(100, 80))
        ];

        Assert.Equal(string.Join(Environment.NewLine, "Page 1 / 2", "BRACKET MOTOR", "設計図５９．２８", "",
            "Page 2 / 2", "No text recognized", ""), OcrResultWindow.FormatPages(pages));
    }

    [Fact]
    public async Task Preprocessing_MatchesDisplayedPixelsForAllTiffOrientations()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("CSTViewer-search-test-");
        string source = Path.Combine(directory.FullName, "図面.tif");
        string prepared = Path.Combine(directory.FullName, "text.tif");
        try
        {
            const int width = 140, height = 110;
            var pixels = new byte[width * height];
            Array.Fill(pixels, byte.MaxValue);
            // 反転や180°回転を検出できるよう、非対称の位置に黒の目印を置く。
            for (int y = 18; y < 34; y++)
                pixels.AsSpan(y * width + 100, 22).Fill(0);
            var bitmap = BitmapSource.Create(width, height, 400, 400, PixelFormats.Gray8, null, pixels, width);
            var encoder = new TiffBitmapEncoder();
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                var metadata = new BitmapMetadata("tiff");
                metadata.SetQuery("/ifd/{ushort=274}", orientation);
                encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            }
            using (var stream = File.Create(source)) encoder.Save(stream);
            BitmapFrame[] frames = DrawingLineRemoval.PrepareFrames(source, 8, CancellationToken.None);
            using (var output = File.Create(prepared))
                DrawingLineRemoval.WriteTiff(output, frames, CancellationToken.None);
            Size[] sizes = frames.Select(frame => new Size(frame.PixelWidth, frame.PixelHeight)).ToArray();
            ImagePage[] displayPages = await Task.WhenAll(Enumerable.Range(0, sizes.Length)
                .Select(index => ImageLoader.LoadPageAsync(source, index, new Size(width, width))));
            using var input = File.OpenRead(prepared);
            var decoder = new TiffBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(8, decoder.Frames.Count);
            for (int index = 0; index < sizes.Length; index++)
            {
                ImagePage display = displayPages[index];
                BitmapFrame frame = decoder.Frames[index];
                Assert.Equal(new Size(display.SourceWidth, display.SourceHeight), sizes[index]);
                Assert.Equal((display.SourceWidth, display.SourceHeight), (frame.PixelWidth, frame.PixelHeight));
                Assert.Equal(400, frame.DpiX, 3);
                Assert.Equal(400, frame.DpiY, 3);
                Assert.Equal(1, frame.Format.BitsPerPixel);
                Assert.Equal(ReadGrayPixels(display.Bitmap), ReadGrayPixels(frame));
            }
        }
        finally
        {
            File.Delete(prepared);
            File.Delete(source);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task Recognition_KeepsTiffPageOrderAndSavesAllPages(int pageCount)
    {
        string source = CreateBlankTiff(pageCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int expectedWorkers = Math.Min(pageCount, Math.Min(4, Environment.ProcessorCount));
        var allWorkersStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterBatchReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        try
        {
            using OcrResult result = await TiffSearch.RecognizePagesAsync(source, pageCount, timeout.Token,
                recognize: async (input, sizes, token) =>
                {
                    // ページごとに幅を変え、認識に渡すTIFFとページ情報の対応を確認する。
                    Assert.Equal(sizes.ToArray(), ReadPageSizes(input));
                    if (Interlocked.Increment(ref started) == expectedWorkers) allWorkersStarted.SetResult();
                    await allWorkersStarted.Task.WaitAsync(token);
                    if (expectedWorkers > 1 && sizes[0].Width == 140)
                        await laterBatchReady.Task.WaitAsync(token);
                    else
                        laterBatchReady.TrySetResult();
                    return sizes.Select(size => new OcrSearchPage([], size)).ToArray();
                });

            Assert.Equal(expectedWorkers, started);
            Size[] expectedSizes = Enumerable.Range(0, pageCount).Select(index => new Size(140 + index, 110)).ToArray();
            Assert.Equal(expectedSizes, result.Pages.Select(page => page.PixelSize).ToArray());
            using FileStream saved = result.OpenPreparedTiff();
            Assert.Equal(expectedSizes, ReadPageSizes(saved));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task Recognition_CancelsOtherBatchesAndClosesTheirInputsOnFailure()
    {
        if (Environment.ProcessorCount < 2) return;
        string source = CreateBlankTiff(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waitingBatchStarted = new TaskCompletionSource<FileStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var failure = new InvalidDataException("Test recognition failure");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                TiffSearch.RecognizePagesAsync(source, 2, timeout.Token, recognize: async (input, sizes, token) =>
                {
                    if (sizes[0].Width == 140)
                    {
                        await waitingBatchStarted.Task.WaitAsync(token);
                        throw failure;
                    }

                    waitingBatchStarted.SetResult(Assert.IsType<FileStream>(input));
                    try { await Task.Delay(Timeout.Infinite, token); }
                    finally { stopped.SetResult(); }
                    return [];
                }));
            Assert.Same(failure, exception);
            Assert.True(stopped.Task.IsCompletedSuccessfully);
            Assert.False((await waitingBatchStarted.Task).CanRead);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task SavingPreparedTiff_PreservesExactBytesAfterOcrCacheIsClosed()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("CSTViewer-search-save-test-");
        string target = Path.Combine(directory.FullName, "saved.tif");
        try
        {
            byte[] bytes = new byte[ViewerSettings.DesktopSaveBufferSize + 37];
            new Random(123).NextBytes(bytes);
            using var prepared = OcrResult.CreatePreparedTiff();
            string preparedPath = prepared.Name;
            await prepared.WriteAsync(bytes);
            prepared.Flush();
            using var result = new OcrResult([], prepared);
            using (var first = result.OpenPreparedTiff())
            using (var second = result.OpenPreparedTiff())
            {
                // 元画像を切り替えた後も、既に開いたOCRウィンドウからそれぞれ保存できる。
                result.Dispose();
                await DesktopFileSave.CopyAsync(first, target, overwrite: false);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
                await DesktopFileSave.CopyAsync(second, target, overwrite: true);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
            }
            Assert.False(File.Exists(preparedPath));
        }
        finally
        {
            File.Delete(target);
            directory.Delete();
        }
    }

    private static byte[] ReadGrayPixels(BitmapSource source)
    {
        var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
        var pixels = new byte[gray.PixelWidth * gray.PixelHeight];
        gray.CopyPixels(pixels, gray.PixelWidth, 0);
        return pixels;
    }

    private static string CreateBlankTiff(int pageCount)
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-OCR-batch-test-{Guid.NewGuid():N}.tif");
        var encoder = new TiffBitmapEncoder();
        const int height = 110;
        for (int index = 0; index < pageCount; index++)
        {
            int width = 140 + index;
            byte[] pixels = new byte[width * height];
            Array.Fill(pixels, byte.MaxValue);
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 400, 400,
                PixelFormats.Gray8, null, pixels, width)));
        }
        using var output = File.Create(path);
        encoder.Save(output);
        return path;
    }

    private static Size[] ReadPageSizes(Stream input) =>
        new TiffBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
            .Frames.Select(frame => new Size(frame.PixelWidth, frame.PixelHeight)).ToArray();
}
