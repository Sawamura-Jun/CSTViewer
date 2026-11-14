using System.IO;
using System.Windows.Media;

namespace CSTViewer;

internal static class OcrSearchSettings
{
    public const string Languages = "eng+jpn";
    public const string VerticalJapaneseModel = "jpn_vert";
    public const string EngineMode = "1";
    public const string PageSegmentationMode = "12";
    public const int LineRemovalInkThreshold = 127;
    public const double LineRemovalReferenceDpi = 400;
    public const int LineRemovalMaxGlyphSize = 160;
    public const int LineRemovalLongLineLength = 151;
    public const double LineRemovalMarginRatio = 0.08;
    public const double SearchBoxWidth = 144;
    public const double ProgressBarWidth = 120;
    public const double ResultWindowWidth = 760;
    public const double ResultWindowHeight = 540;
    public const double ResultWindowMinimumWidth = 420;
    public const double ResultWindowMinimumHeight = 260;
    public const double ResultTextFontSize = 14;
    public static readonly Color HighlightColor = Color.FromArgb(89, 255, 0, 0);

    public static readonly string BundledTesseractDirectory = Path.Combine(AppContext.BaseDirectory, "Tesseract-OCR");
}
