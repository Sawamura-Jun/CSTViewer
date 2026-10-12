using System.IO;

namespace CSTViewer.Tests;

public class DiffSavePathTests
{
    [Theory]
    [InlineData("scan.TIF", "", "scan_dif.TIF")]
    [InlineData("scan.tiff", "", "scan_dif.tiff")]
    [InlineData("scan.png", "", "scan_dif.tif")]
    [InlineData("scan.tif", "result", "result.tif")]
    [InlineData("scan.tif", "result.png", "result.tif")]
    [InlineData("scan.tif", "  \"result.tiff\"  ", "result.tiff")]
    [InlineData("scan.tif", ".\\result.tif", "result.tif")]
    public void Resolve_BlankAndFilenameFollowPythonRules(string filename, string text, string expected)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "images");
        Assert.Equal(Path.Combine(directory, expected), DiffSavePath.Resolve(Path.Combine(directory, filename), text));
    }

    [Fact]
    public void Resolve_DirectoryAndRelativePathUseSpecifiedDestination()
    {
        string source = Path.Combine(AppContext.BaseDirectory, "images", "new.tif");
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "new_dif.tif"), DiffSavePath.Resolve(source, AppContext.BaseDirectory));
        Assert.Equal(Path.GetFullPath("new folder/new_dif.tif"), DiffSavePath.Resolve(source, "new folder\\"));
        Assert.Equal(Path.GetFullPath("new folder/result.tif"), DiffSavePath.Resolve(source, "new folder/result.png"));
    }
}
