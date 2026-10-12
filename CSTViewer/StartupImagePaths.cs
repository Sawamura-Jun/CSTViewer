using System.IO;

namespace CSTViewer;

/// <summary>起動元の作業フォルダーを基準に、画像の引数を絶対パスへ揃える。</summary>
internal static class StartupImagePaths
{
    internal static string[] Resolve(IEnumerable<string> arguments, string workingDirectory)
    {
        var paths = new List<string>();
        foreach (string argument in arguments)
        {
            string path = argument.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                    path = uri.LocalPath;
                path = Path.GetFullPath(path, workingDirectory);
                if (StartupSettings.SupportedImageExtensions.Contains(Path.GetExtension(path)))
                    paths.Add(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // 不正な引数を飛ばし、残りの画像は開けるようにする。
            }
        }
        return paths.ToArray();
    }
}
