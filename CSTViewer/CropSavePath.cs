using System.IO;
using System.Text.RegularExpressions;

namespace CSTViewer;

/// <summary>Python版と同じ規則で保存先欄をフォルダー名・ファイル名として解釈する。</summary>
internal static class CropSavePath
{
    public static string DefaultText
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string? oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            string[] candidates = [Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                oneDrive is null ? "" : Path.Combine(oneDrive, "Pictures"), Path.Combine(profile, "Pictures"), profile];
            string directory = candidates.FirstOrDefault(path => !string.IsNullOrEmpty(path) && Directory.Exists(path)) ?? profile;
            return Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        }
    }

    public static string Resolve(string sourcePath, string? text, string? timestamp = null)
    {
        string source = Path.GetFullPath(sourcePath);
        string extension = Path.GetExtension(source);
        if (string.IsNullOrEmpty(extension)) extension = ".tif";
        timestamp ??= DateTime.Now.ToString(ViewerSettings.CropTimestampFormat);
        string automaticName = $"{Path.GetFileNameWithoutExtension(source)}_{timestamp}{extension}";
        string raw = (text ?? "").Trim().Trim('"');
        if (string.IsNullOrEmpty(raw)) return Path.Combine(Path.GetDirectoryName(source)!, automaticName);

        string expanded = Environment.ExpandEnvironmentVariables(raw);
        // Pythonのexpandvarsと同じく、%VAR%に加えて$VARと${VAR}も扱う。
        expanded = Regex.Replace(expanded, @"\$(?:\{(?<name>[^}]+)\}|(?<name>[A-Za-z_][A-Za-z0-9_]*))",
            match => Environment.GetEnvironmentVariable(match.Groups["name"].Value) ?? match.Value);
        if (expanded == "~" || expanded.StartsWith("~\\", StringComparison.Ordinal) || expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        if (raw.EndsWith('\\') || raw.EndsWith('/') || Directory.Exists(expanded))
            return Path.GetFullPath(Path.Combine(expanded, automaticName));

        string? parent = Path.GetDirectoryName(expanded);
        if (string.IsNullOrEmpty(parent) || parent == ".") expanded = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileName(expanded));
        string currentExtension = Path.GetExtension(expanded);
        bool tiffAlias = extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        if (!currentExtension.Equals(extension, StringComparison.OrdinalIgnoreCase)
            && !(tiffAlias && (currentExtension.Equals(".tif", StringComparison.OrdinalIgnoreCase) || currentExtension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))))
            expanded = Path.ChangeExtension(expanded, extension);
        return Path.GetFullPath(expanded);
    }
}
