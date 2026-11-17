using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CSTViewer;

internal static class TiffSearch
{
    // 見開きやファイル切り替えで、大判ページの前処理が同時に実行されるのを防ぐ。
    private static readonly SemaphoreSlim OcrGate = new(1);

    public static Task<OcrResult> RecognizePagesAsync(string path, int pageCount, CancellationToken token,
        IProgress<int>? preparedPages = null,
        Func<Stream, IReadOnlyList<Size>, CancellationToken, Task<OcrSearchPage[]>>? recognize = null) =>
        Task.Run(async () =>
        {
            await OcrGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (recognize is null)
                {
                    var startInfo = TiffOcr.CreateStartInfo();
                    recognize = (input, sizes, cancellationToken) =>
                        TiffOcr.RecognizeSearchPagesAsync(input, startInfo, sizes, cancellationToken);
                }
                var preparedTiff = OcrResult.CreatePreparedTiff();
                try
                {
                    BitmapFrame[] frames = DrawingLineRemoval.PrepareFrames(path, pageCount, token, preparedPages);
                    Size[] sizes = frames.Select(frame => new Size(frame.PixelWidth, frame.PixelHeight)).ToArray();
                    // 結果画面から保存するTIFFには、全ページを元の順で入れる。
                    DrawingLineRemoval.WriteTiff(preparedTiff, frames, token);
                    preparedTiff.Flush();
                    preparedTiff.Position = 0;

                    int workerCount = Math.Min(pageCount, Math.Min(OcrSearchSettings.MaxTesseractProcesses,
                        Environment.ProcessorCount));
                    OcrSearchPage[] pages = workerCount == 1
                        ? await recognize(preparedTiff, sizes, token).ConfigureAwait(false)
                        : await RecognizeBatchesAsync(frames, sizes, workerCount, recognize, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new OcrResult(pages, preparedTiff);
                }
                catch
                {
                    preparedTiff.Dispose();
                    throw;
                }
            }
            finally
            {
                OcrGate.Release();
            }
        }, token);

    private static async Task<OcrSearchPage[]> RecognizeBatchesAsync(BitmapFrame[] frames, Size[] sizes, int workerCount,
        Func<Stream, IReadOnlyList<Size>, CancellationToken, Task<OcrSearchPage[]>> recognize, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new Task<OcrSearchPage[]>[workerCount];
        int firstPage = 0;
        int pagesPerWorker = frames.Length / workerCount;
        int remainder = frames.Length % workerCount;
        for (int worker = 0; worker < workerCount; worker++)
        {
            // 連続するページを均等に割り当て、各プロセスで認識モデルを使い回す。
            int count = pagesPerWorker + (worker < remainder ? 1 : 0);
            tasks[worker] = RecognizeBatchAsync(frames.AsMemory(firstPage, count),
                sizes[firstPage..(firstPage + count)], recognize, cancellation);
            firstPage += count;
        }

        // WhenAllの結果は完了順ではなく、渡したタスクの順に並ぶ。
        OcrSearchPage[][] batches = await Task.WhenAll(tasks).ConfigureAwait(false);
        return batches.SelectMany(batch => batch).ToArray();
    }

    private static async Task<OcrSearchPage[]> RecognizeBatchAsync(ReadOnlyMemory<BitmapFrame> frames, Size[] sizes,
        Func<Stream, IReadOnlyList<Size>, CancellationToken, Task<OcrSearchPage[]>> recognize,
        CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using var input = OcrResult.CreatePreparedTiff();
            DrawingLineRemoval.WriteTiff(input, frames.Span, cancellation.Token);
            input.Position = 0;
            return await recognize(input, sizes, cancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            // 一組でも失敗したら、残りのTesseractも終了させる。
            cancellation.Cancel();
            throw;
        }
    }
}
