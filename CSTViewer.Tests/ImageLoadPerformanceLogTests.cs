using System.IO;
using System.Text.Json;

namespace CSTViewer.Tests;

public class ImageLoadPerformanceLogTests
{
    [Fact]
    public async Task WriteAsync_PreservesFilePathAndMillisecondValuesInJson()
    {
        string logPath = TemporaryLogPath();
        try
        {
            var measurement = new ImageLoadMeasurement("日本語\\画像\"名前.tiff")
            {
                FileSizeBytes = 12345,
                PixelWidth = 80,
                PixelHeight = 60,
                PixelFormat = "Rgb24",
                ImageReadyMilliseconds = 12.5
            };
            measurement.StartStage("FirstRenderWait");

            Task write = ImageLoadPerformanceLog.WriteAsync(measurement, "FirstRenderCallback", logPath: logPath);
            // 書き込み開始後の変更が、確定済みのログに混ざらないことを確認する。
            measurement.PixelWidth = 999;
            measurement.StartStage("LaterOperation");
            measurement.FinishStage();
            await write;

            string line = Assert.Single(await File.ReadAllLinesAsync(logPath));
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement entry = json.RootElement;
            Assert.Equal(measurement.FilePath, entry.GetProperty("FilePath").GetString());
            Assert.Equal(measurement.LoadId, entry.GetProperty("LoadId").GetGuid());
            Assert.Equal("FirstRenderCallback", entry.GetProperty("Outcome").GetString());
            Assert.Equal(12345, entry.GetProperty("FileSizeBytes").GetInt64());
            Assert.Equal(80, entry.GetProperty("PixelWidth").GetInt32());
            Assert.Equal(12.5, entry.GetProperty("ImageReadyMs").GetDouble());
            Assert.True(entry.GetProperty("TotalMs").GetDouble() >= 0);
            Assert.True(entry.GetProperty("StagesMs").GetProperty("FirstRenderWait").GetDouble() >= 0);
            Assert.False(entry.GetProperty("StagesMs").TryGetProperty("LaterOperation", out _));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWritesKeepSeparateJsonRecords()
    {
        string logPath = TemporaryLogPath();
        try
        {
            var measurements = Enumerable.Range(0, 8)
                .Select(index => new ImageLoadMeasurement($"image-{index}.tiff")).ToArray();

            await Task.WhenAll(measurements.Select(measurement =>
                ImageLoadPerformanceLog.WriteAsync(measurement, "FirstRenderCallback", logPath: logPath)));

            string[] lines = await File.ReadAllLinesAsync(logPath);
            Assert.Equal(measurements.Length, lines.Length);
            var loadIds = new HashSet<Guid>();
            foreach (string line in lines)
            {
                using JsonDocument json = JsonDocument.Parse(line);
                loadIds.Add(json.RootElement.GetProperty("LoadId").GetGuid());
            }
            Assert.True(loadIds.SetEquals(measurements.Select(measurement => measurement.LoadId)));
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task WriteAsync_RecordsFailedFileOpenWithOriginalError()
    {
        string logPath = TemporaryLogPath();
        var measurement = new ImageLoadMeasurement(Path.ChangeExtension(logPath, ".missing.tiff"));
        try
        {
            var exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                ImageLoader.LoadAsync(measurement.FilePath, measurement));

            await ImageLoadPerformanceLog.WriteAsync(measurement, "Failed", exception, logPath);

            using JsonDocument json = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(logPath)));
            JsonElement entry = json.RootElement;
            Assert.Equal("Failed", entry.GetProperty("Outcome").GetString());
            Assert.Equal("FileOpen", entry.GetProperty("FailedStage").GetString());
            Assert.Equal(typeof(FileNotFoundException).FullName, entry.GetProperty("ErrorType").GetString());
            Assert.True(entry.GetProperty("StagesMs").GetProperty("FileOpen").GetDouble() >= 0);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("PixelWidth").ValueKind);
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    [Fact]
    public async Task WriteAsync_UnwritableDestinationDoesNotThrow()
    {
        string blockedDirectory = TemporaryLogPath();
        try
        {
            // 同名のファイルで保存先ディレクトリを塞ぎ、ログ保存の失敗を再現する。
            await File.WriteAllTextAsync(blockedDirectory, "既存ファイル");

            await ImageLoadPerformanceLog.WriteAsync(new ImageLoadMeasurement("sample.tiff"),
                "FirstRenderCallback", logPath: Path.Combine(blockedDirectory, "performance.jsonl"));

            Assert.Equal("既存ファイル", await File.ReadAllTextAsync(blockedDirectory));
        }
        finally
        {
            File.Delete(blockedDirectory);
        }
    }

    private static string TemporaryLogPath()
    {
        return Path.Combine(Path.GetTempPath(), $"CSTViewer-performance-{Guid.NewGuid()}.jsonl");
    }
}
