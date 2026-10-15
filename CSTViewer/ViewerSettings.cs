using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CSTViewer;

/// <summary>表示と操作感を調整するための主要パラメータ。</summary>
public static class ViewerSettings
{
    public const string ApplicationTitle = "CSTViewer v1.0.4";
    public const string UiCultureName = StartupSettings.UiCultureName;
    public const string TrayIconResourceName = "CSTViewer.Assets.CSTViewer.ico";
    public const string TrayMenuFontFamily = "Segoe UI";
    public const string TrayMenuBackground = "#2B2B2B";
    public const string TrayMenuForeground = "#E6E6E6";
    public const string TrayMenuHoverBackground = "#3D3D3D";
    public const string TrayMenuBorder = "#454545";
    // 画像を指定せずに起動したときのウィンドウ全体のサイズ（DIP）。
    public const double WindowWidth = 1000; // 1000
    public const double WindowHeight = 796;
    // 狭い画面ではツールバーと保存先欄を折り返す。同期モードには別の最小幅を使う。
    public const double MinimumWindowWidth = 600;  // 600
    public const double MinimumWindowHeight = 400; // 400
    // 起動先モニター全体に対する、枠やツールバーを含むウィンドウ寸法の割合。
    public const double StartupLandscapeWindowWidthRatio = 2.0 / 3.0;
    public const double StartupPortraitWindowHeightRatio = 9.0 / 10.0;
    public const double CtrlWheelWindowScaleFactor = 1.12;
    // WPFの文字サイズはDIP単位のため、9pt × 96 / 72 = 12を指定する。
    public const double UiFontSize = 12;
    public const double TitleBarSeparatorHeight = 1;
    public const double ToolbarButtonHeight = 24;
    // x:StaticでColorプロパティに渡すため、文字列ではなくColor型で保持する。
    public static readonly Color ToolbarButtonHoverBackground = Color.FromRgb(61, 61, 61);
    public static readonly Color ToolbarCheckedButtonHoverBackground = Color.FromRgb(120, 213, 255);
    public const int DesktopSaveBufferSize = 128 * 1024;
    // 上下の余白を揃えるため2DIP上へ移し、余白の合計は従来の4DIPに保つ。
    public static readonly Thickness MainToolbarMargin = new(0, -2, 0, 6);
    public static readonly Thickness ToolbarButtonPadding = new(10, 1, 10, 1);
    public static readonly Thickness ToolbarButtonMargin = new(0, 0, 4, 4);
    public static readonly Thickness ToolbarTextBoxPadding = new(6, 1, 6, 1);
    public const double CropBorderWidth = 2;
    public const double CropHandleSize = 9;
    public const double CropEdgeGrabWidth = 10;
    public const double CropDragStartDistance = 3;
    public const double CropMinimumViewSize = 2;
    public const int CropJpegQuality = 90;
    public const double CropSaveTextMinimumWidth = 240;
    public const string CropTimestampFormat = "yyyyMMdd_HHmmss_fff";
    public const int CropPngCompression = 9;
    public const Key PreviousFileShortcutKey = Key.B;
    public const Key NextFileShortcutKey = Key.N;
    public const Key RefreshFileListShortcutKey = Key.F5;
    public const Key SpreadShortcutKey = Key.S;
    public const Key DiffShortcutKey = Key.D;
    public const string DiffNoChangeText = "No differences detected";
    public const string DiffNoChangeFontFamily = "Meiryo";
    public const double DiffNoChangeFontSizeRatio = 1.0 / 15.0;
    public const double DiffProgressBarWidth = 160;
    public const int DiffCopyBufferSize = 128 * 1024;
    public const int DiffReplaceRetryCount = 8;
    public const int DiffReplaceRetryMilliseconds = 250;
    public const NetVips.Enums.ForeignTiffCompression DiffTiffCompression = NetVips.Enums.ForeignTiffCompression.Lzw;
    public static readonly double[] DiffAddedColor = [255, 0, 0];
    public static readonly double[] DiffDeletedColor = [0, 0, 255];
    public const Key FitShortcutKey = Key.F;
    public const Key CloseShortcutKey = Key.C;
    public const double SpreadMinimumWindowWidth = 480;
    // DPI変換による最大1物理ピクセル程度の丸めで、復元情報を破棄しない許容差（DIP）。
    public const double SpreadSizeTolerance = 1;
    public const int SpreadSyncIntervalMilliseconds = 16;
    public const double SpreadSyncZoomRatioEpsilon = 1e-4;
    public const int StartupRequestTimeoutMilliseconds = StartupSettings.RequestTimeoutMilliseconds;
    public const int StartupRequestPathLimit = StartupSettings.RequestPathLimit;
    public const int StartupRequestByteLimit = StartupSettings.RequestByteLimit;
    public const double WheelZoomFactor = 1.25;
    public const double MouseWheelDeltaPerStep = 120;
    public const double MaximumZoomScale = 32;
    public const double FitSnapToleranceRatio = 1.01;
    // 高DPIでのウィンドウ寸法の丸めを、Fitの余白として積み重ねない許容幅（DIP）。
    public const double FitWindowPaddingTolerance = 1;
    public const int MinimumPreviewDimension = 512;
    public const int ImageDecodeConcurrency = 1;
    public const int DetailLoadDelayMilliseconds = 280;
    public const int ZoomInteractionIdleMilliseconds = 220;
    public const int DetailAfterZoomIdleDelayMilliseconds = 40;
    public const double DetailOversamplingFactor = 1.8;
    // Python版と同じく、縮小率が0.4未満なら1/2、1/4などの画像を選ぶ。
    public const double MipmapMinimumDisplayScale = 0.4;
    public const double BitmapSmoothScaleThreshold = 0.7;
    public const int MipmapCacheBitmapLimit = 64;
    public const long MipmapCacheByteLimit = 64 * 1024 * 1024;
    public const int PreviewCachePageLimit = 64;
    public const int DetailCachePageLimit = 4;
    public const long PreviewCacheByteLimit = 128 * 1024 * 1024;
    public const long DetailCacheByteLimit = 128 * 1024 * 1024;
    public static readonly bool EnablePerformanceLogging = false;
    public const int FirstRenderMeasurementTimeoutMilliseconds = 5000;
    public static string PerformanceLogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CSTViewer", "Logs", "image-load-performance.jsonl");
    public const string ImageFileFilter =
        "Image files|*.tif;*.tiff;*.png;*.jpg;*.jpeg;*.bmp;*.gif|TIFF images|*.tif;*.tiff|All files|*.*";

    internal static readonly IReadOnlySet<string> SupportedImageExtensions = StartupSettings.SupportedImageExtensions;
}
