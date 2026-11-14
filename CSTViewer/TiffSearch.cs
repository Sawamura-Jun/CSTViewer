using System.IO;

namespace CSTViewer;

internal static class TiffSearch
{
    // 見開きやファイル切り替えで、大判ページの前処理が同時に実行されるのを防ぐ。
    private static readonly SemaphoreSlim OcrGate = new(1);

    public static Task<OcrSearchPage[]> RecognizePagesAsync(string path, int pageCount, CancellationToken token,
        IProgress<int>? preparedPages = null) =>
        Task.Run(async () =>
        {
            await OcrGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var startInfo = TiffOcr.CreateStartInfo();
                DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory("CSTViewer-OCR-");
                string preparedPath = Path.Combine(workingDirectory.FullName, "text.tif");
                try
                {
                    var sizes = DrawingLineRemoval.SaveAs(path, preparedPath, pageCount, token, preparedPages);
                    return await TiffOcr.RecognizeSearchPagesAsync(preparedPath, startInfo, sizes, token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    File.Delete(preparedPath);
                    workingDirectory.Delete();
                }
            }
            finally
            {
                OcrGate.Release();
            }
        }, token);
}
