using System.IO;
using System.Windows.Media;

namespace CSTViewer;

internal static class OcrSearchSettings
{
    public const string Languages = "eng+jpn";
    public const string EngineMode = "1";
    public const string PageSegmentationMode = "12";
    public const string TesseractEnvironmentVariable = "CSTVIEWER_TESSERACT_DIR";
    public const int LineRemovalInkThreshold = 127;
    public const double LineRemovalReferenceDpi = 400;
    public const int LineRemovalMaxGlyphSize = 160;
    public const int LineRemovalLongLineLength = 151;
    public const double LineRemovalMarginRatio = 0.08;
    public const double SearchBoxWidth = 144;
    public static readonly Color HighlightColor = Color.FromArgb(89, 255, 0, 0);

    public static readonly string DefaultTesseractDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR");
}
