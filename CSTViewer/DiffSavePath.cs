using System.IO;

namespace CSTViewer;

internal static class DiffSavePath
{
    public static string Resolve(string newImagePath, string? text)
    {
        // 保存先欄の展開規則を共用し、差分だけは元形式によらずTIFFへ保存する。
        string source = ImageLoader.IsTiff(newImagePath) ? newImagePath : Path.ChangeExtension(newImagePath, ".tif");
        return CropSavePath.Resolve(source, text, "dif");
    }

    public static void Validate(string oldImagePath, string newImagePath, string outputPath)
    {
        string target = Path.GetFullPath(outputPath);
        if (!ImageLoader.IsTiff(target)) throw new ArgumentException("Choose a TIFF file as the diff save location.");
        if (new[] { oldImagePath, newImagePath }.Any(path =>
            target.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The output cannot be the same file as the new or old image.");
    }
}
