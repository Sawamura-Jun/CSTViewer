namespace CSTViewer;

/// <summary>WPFを読み込まずに使える起動・通信の設定。</summary>
internal static class StartupSettings
{
    public const string UiCultureName = "en-US";
    public const int RequestTimeoutMilliseconds = 5000;
    public const int RequestPathLimit = 256;
    public const int RequestByteLimit = 1024 * 1024;

    public static readonly IReadOnlySet<string> SupportedImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".tif", ".tiff", ".png", ".jpg", ".jpeg", ".bmp", ".gif"
        };
}
