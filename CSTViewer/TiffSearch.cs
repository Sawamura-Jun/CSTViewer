using System.IO;
using System.Windows;

namespace CSTViewer;

internal static class TiffSearch
{
    // 見開きやファイル切り替えで、大判ページの前処理が同時に実行されるのを防ぐ。
    private static readonly SemaphoreSlim SearchGate = new(1);

    public static Task<Rect[][]> FindMatchesAsync(string path, int pageCount, string query, CancellationToken token) =>
        Task.Run(async () =>
        {
            await SearchGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var startInfo = TiffOcr.CreateStartInfo();
                DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory("CSTViewer-OCR-");
                string preparedPath = Path.Combine(workingDirectory.FullName, "text.tif");
                try
                {
                    Size[] sizes = DrawingLineRemoval.SaveAs(path, preparedPath, pageCount, token);
                    OcrSearchPage[] pages = await TiffOcr.RecognizeSearchPagesAsync(preparedPath, startInfo, sizes, token)
                        .ConfigureAwait(false);
                    return OcrTextSearch.FindMatches(pages, query, token);
                }
                finally
                {
                    File.Delete(preparedPath);
                    workingDirectory.Delete();
                }
            }
            finally
            {
                SearchGate.Release();
            }
        }, token);
}
