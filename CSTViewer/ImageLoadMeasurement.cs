using System.Diagnostics;

namespace CSTViewer;

/// <summary>画像を開く操作ごとに、単調増加する時計で各工程の経過時間を記録する。</summary>
internal sealed class ImageLoadMeasurement(string filePath)
{
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly Dictionary<string, double> _stageMilliseconds = new();
    private long _stageStartedAt;

    public Guid LoadId { get; } = Guid.NewGuid();
    public string FilePath { get; } = filePath;
    public Guid? SessionId { get; set; }
    public string Operation { get; set; } = "Image";
    public string? Backend { get; set; }
    public int? PageIndex { get; set; }
    public int? PageCount { get; set; }
    public int? LoadedPages { get; set; }
    public int? SourceWidth { get; set; }
    public int? SourceHeight { get; set; }
    public long? FileSizeBytes { get; set; }
    public int? PixelWidth { get; set; }
    public int? PixelHeight { get; set; }
    public int? RegionX { get; set; }
    public int? RegionY { get; set; }
    public int? RegionWidth { get; set; }
    public int? RegionHeight { get; set; }
    public string? PixelFormat { get; set; }
    public double? ImageReadyMilliseconds { get; set; }
    public string? ActiveStage { get; private set; }
    public double ElapsedMilliseconds => Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
    public IReadOnlyDictionary<string, double> StageMilliseconds => _stageMilliseconds;

    public void StartStage(string stage)
    {
        FinishStage();
        ActiveStage = stage;
        _stageStartedAt = Stopwatch.GetTimestamp();
    }

    public void FinishStage()
    {
        if (ActiveStage is not string stage)
        {
            return;
        }

        _stageMilliseconds[stage] = Stopwatch.GetElapsedTime(_stageStartedAt).TotalMilliseconds;
        ActiveStage = null;
    }
}
