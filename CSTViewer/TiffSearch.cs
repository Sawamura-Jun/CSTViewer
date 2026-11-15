namespace CSTViewer;

internal static class TiffSearch
{
    // 見開きやファイル切り替えで、大判ページの前処理が同時に実行されるのを防ぐ。
    private static readonly SemaphoreSlim OcrGate = new(1);

    public static Task<OcrResult> RecognizePagesAsync(string path, int pageCount, CancellationToken token,
        IProgress<int>? preparedPages = null) =>
        Task.Run(async () =>
        {
            await OcrGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var startInfo = TiffOcr.CreateStartInfo();
                var preparedTiff = OcrResult.CreatePreparedTiff();
                try
                {
                    var sizes = DrawingLineRemoval.WriteTiff(path, preparedTiff, pageCount, token, preparedPages);
                    preparedTiff.Position = 0;
                    OcrSearchPage[] pages = await TiffOcr.RecognizeSearchPagesAsync(preparedTiff, startInfo, sizes, token)
                        .ConfigureAwait(false);
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
}
