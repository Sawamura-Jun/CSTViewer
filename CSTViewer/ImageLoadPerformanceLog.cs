using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CSTViewer;

/// <summary>計測結果をJSON Lines形式で保存し、ログの失敗を画像表示に波及させない。</summary>
internal static class ImageLoadPerformanceLog
{
    private static readonly object WriteLock = new();

    public static Task WriteAsync(ImageLoadMeasurement measurement, string outcome,
        Exception? exception = null, string? logPath = null)
    {
        string? failedStage = exception is null ? null : measurement.ActiveStage;
        measurement.FinishStage();
        // 非同期の書き込みに渡す前に、UIスレッド上で計測結果を確定する。
        var entry = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            measurement.LoadId,
            measurement.FilePath,
            measurement.SessionId,
            measurement.Operation,
            measurement.Backend,
            measurement.PageIndex,
            measurement.PageCount,
            measurement.LoadedPages,
            measurement.SourceWidth,
            measurement.SourceHeight,
            Outcome = outcome,
            measurement.FileSizeBytes,
            measurement.PixelWidth,
            measurement.PixelHeight,
            measurement.PixelFormat,
            StagesMs = new Dictionary<string, double>(measurement.StageMilliseconds),
            ImageReadyMs = measurement.ImageReadyMilliseconds,
            TotalMs = measurement.ElapsedMilliseconds,
            FailedStage = failedStage,
            ErrorType = exception?.GetType().FullName,
            ErrorMessage = exception?.Message
        };
        string destination = logPath ?? ViewerSettings.PerformanceLogPath;

        // ディスクへのログ書き込みをUIスレッドから切り離す。
        return Task.Run(() =>
        {
            try
            {
                // JSONの整形とデバッグ出力も描画イベント内では実行しない。
                string line = JsonSerializer.Serialize(entry);
                Debug.WriteLine($"[ImageLoadPerformance] {line}");
                lock (WriteLock)
                {
                    string? directory = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    File.AppendAllText(destination, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException
                                                or ArgumentException or NotSupportedException
                                                or System.Security.SecurityException)
            {
                Debug.WriteLine($"Could not save the image loading performance log: {logException.Message}");
            }
        });
    }
}
