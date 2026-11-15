using System.IO;

namespace CSTViewer;

internal sealed class OcrResult(OcrSearchPage[] pages, FileStream preparedTiff) : IDisposable
{
    public OcrSearchPage[] Pages { get; } = pages;

    public static FileStream CreatePreparedTiff()
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-OCR-{Guid.NewGuid():N}.tif");
        // 全てのハンドルが閉じられたときにWindowsが一時ファイルを削除する。
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete,
            ViewerSettings.DesktopSaveBufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    // 結果ウィンドウは別のハンドルを持ち、メイン画面で画像を切り替えた後も保存できる。
    public FileStream OpenPreparedTiff() =>
        new(preparedTiff.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            ViewerSettings.DesktopSaveBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public void Dispose() => preparedTiff.Dispose();
}
