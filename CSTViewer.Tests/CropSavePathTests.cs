using System.IO;

namespace CSTViewer.Tests;

public class CropSavePathTests
{
    private const string Timestamp = "20261011_123456_789";
    private static string Source => Path.Combine(AppContext.BaseDirectory, "original", "scan.TIF");

    [Theory]
    [InlineData(null, "scan_20261011_123456_789.TIF")]
    [InlineData("  ", "scan_20261011_123456_789.TIF")]
    [InlineData("clip", "clip.TIF")]
    [InlineData("clip.png", "clip.TIF")]
    [InlineData("clip.tiff", "clip.tiff")]
    [InlineData("  \"clip.tif\"  ", "clip.tif")]
    [InlineData(".\\clip", "clip.TIF")]
    public void BlankOrFilename_UsesOriginalDirectoryAndFormat(string? text, string filename)
    {
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Source)!, filename), CropSavePath.Resolve(Source, text, Timestamp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directory_UsesOriginalNameWithTimestamp(bool trailingSeparator)
    {
        string directory = trailingSeparator ? Path.Combine(AppContext.BaseDirectory, "new folder") + "\\" : AppContext.BaseDirectory;
        Assert.Equal(Path.Combine(directory, "scan_20261011_123456_789.TIF"), CropSavePath.Resolve(Source, directory, Timestamp));
    }

    [Fact]
    public void FullAndRelativePaths_KeepSpecifiedDirectory()
    {
        string target = Path.Combine(AppContext.BaseDirectory, "new folder", "clip.png");
        Assert.Equal(Path.ChangeExtension(target, ".TIF"), CropSavePath.Resolve(Source, target, Timestamp));
        Assert.Equal(Path.GetFullPath("new folder/clip.TIF"), CropSavePath.Resolve(Source, "new folder/clip", Timestamp));
    }

    [Theory]
    [InlineData("source.jpeg", "clip.jpg", "clip.jpeg")]
    [InlineData("source.png", "clip.tif", "clip.png")]
    [InlineData("source.bmp", "clip", "clip.bmp")]
    public void Extension_IsAdjustedLikePython(string source, string text, string expected)
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, expected),
            CropSavePath.Resolve(Path.Combine(AppContext.BaseDirectory, source), text, Timestamp));
    }

    [Fact]
    public void EnvironmentAndHome_AreExpanded()
    {
        const string variable = "CSTVIEWER_CROP_PATH_TEST";
        Environment.SetEnvironmentVariable(variable, AppContext.BaseDirectory);
        try
        {
            foreach (string prefix in new[] { "%" + variable + "%", "$" + variable, "${" + variable + "}" })
                Assert.Equal(Path.Combine(AppContext.BaseDirectory, "clip.TIF"),
                    CropSavePath.Resolve(Source, prefix + "/clip", Timestamp));
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "clip.TIF"),
                CropSavePath.Resolve(Source, "~/clip", Timestamp));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public void DefaultText_IsAnExistingDirectory()
    {
        Assert.True(Directory.Exists(CropSavePath.DefaultText));
        Assert.EndsWith("\\", CropSavePath.DefaultText);
    }
}
