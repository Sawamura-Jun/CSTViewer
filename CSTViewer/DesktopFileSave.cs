using System.IO;

namespace CSTViewer;

/// <summary>表示中の元ファイルを、再エンコードせずにデスクトップへコピーする。</summary>
internal static class DesktopFileSave
{
    public static string DefaultDirectory
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            // Windowsの既知フォルダーを優先し、OneDriveへの移動にも対応する。
            string[] candidates = [
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                string.IsNullOrWhiteSpace(oneDrive) ? "" : Path.Combine(oneDrive, "Desktop"),
                Path.Combine(profile, "Desktop"), profile];
            return candidates.FirstOrDefault(Directory.Exists)
                ?? throw new DirectoryNotFoundException("The desktop folder could not be found.");
        }
    }

    public static string TargetPath(string sourcePath, string? desktopDirectory = null) =>
        Path.Combine(Path.GetFullPath(desktopDirectory ?? DefaultDirectory), Path.GetFileName(sourcePath));

    public static Task<string> CopyAsync(string sourcePath, string targetPath, bool overwrite,
        CancellationToken token = default) => Task.Run(async () =>
    {
        token.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(sourcePath);
        string target = Path.GetFullPath(targetPath);
        if (!File.Exists(source)) throw new FileNotFoundException("The source file could not be found.", source);
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) return target;
        if (Directory.Exists(target)) throw new IOException("The save location is a folder with the same name as the source file.");

        string temporary = Path.Combine(Path.GetDirectoryName(target)!, $".CSTViewer-save-{Guid.NewGuid():N}.tmp");
        try
        {
            DateTime modified = File.GetLastWriteTimeUtc(source);
            DateTime accessed = File.GetLastAccessTimeUtc(source);
            // 同じフォルダーの一時ファイルへ書き、失敗・キャンセル時に既存ファイルを保つ。
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                ViewerSettings.DesktopSaveBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                ViewerSettings.DesktopSaveBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ViewerSettings.DesktopSaveBufferSize, token);
                await output.FlushAsync(token);
            }
            File.SetLastWriteTimeUtc(temporary, modified);
            File.SetLastAccessTimeUtc(temporary, accessed);
            token.ThrowIfCancellationRequested();
            // overwrite=falseなら確認後に別のファイルが作られても上書きしない。
            File.Move(temporary, target, overwrite);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }, token);
}
