using System.Windows;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    private ImageDetailRegion? _currentDetailRegion;
    private CancellationTokenSource? _detailRegionCancellation;
    private long _panDetailNotBeforeTicks;

    private static bool NeedsDetailRegion(ImagePage page, double displayScale) =>
        !HasEnoughResolution(page.Bitmap, page.DetailDecodeSize(displayScale / ViewerSettings.DetailOversamplingFactor));

    private async Task LoadVisibleDetailsAsync(ImageLoadSession session, ImagePage page)
    {
        double displayScale = GetDisplayScale();
        if (!NeedsDetailRegion(page, displayScale))
        {
            ClearDisplayedDetailRegion();
            return;
        }
        bool useRegion = ViewerSettings.UseRegionalDetailLoading;
        ImageDetailRequest request = useRegion
            ? ImageDetailRequest.Create(_viewport, displayScale)
            : ImageDetailRequest.CreateFullImage(_viewport, displayScale);
        if (request.Rectangle.IsEmpty) return;
        // 選択したモードの範囲（周辺の余白またはページ全体）が足りているか確認する。
        Rect required = new(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height);
        if (_currentDetailRegion?.Covers(page.Index, required, displayScale) == true) return;
        if (session.Details.Get(page.Index, required, displayScale) is { } cached)
        {
            SetDisplayedDetailRegion(cached);
            return;
        }
        long revision = _viewRevision;
        using var cancellation = new CancellationTokenSource();
        _detailRegionCancellation = cancellation;
        _isDetailLoading = true;
        UpdateStatusText();
        var measurement = CreateMeasurement(session, useRegion ? "PageDetailRegion" : "PageDetailFull");
        bool retry = false;
        try
        {
            var region = await session.LoadDetailRegionAsync(page.Index, request, measurement, cancellation.Token, useRegion);
            if (_session == session && !session.Token.IsCancellationRequested && !_isClosed)
            {
                session.Details.Add(region);
                // 読み込み中に動いた場合もキャッシュには残し、古い位置の表示は適用しない。
                if (revision == _viewRevision && _currentPage == page && !_isPanning && !_isZoomInteracting)
                {
                    measurement?.StartStage("UiApply");
                    SetDisplayedDetailRegion(region);
                }
                else retry = true;
            }
            WriteMeasurement(measurement, "Completed");
        }
        catch (OperationCanceledException)
        {
            WriteMeasurement(measurement, "Cancelled");
            retry = true;
        }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            WriteMeasurement(measurement, "Failed", exception);
            if (_session == session) session.Error = "Failed to update the image display";
        }
        finally
        {
            if (ReferenceEquals(_detailRegionCancellation, cancellation)) _detailRegionCancellation = null;
            _isDetailLoading = false;
            if (!_isClosed)
            {
                UpdatePageControls();
                if (retry || revision != _viewRevision) ScheduleDetailLoad(_isZoomInteracting
                    ? ViewerSettings.DetailLoadDelayMilliseconds : ViewerSettings.DetailAfterZoomIdleDelayMilliseconds);
            }
        }
    }

    private void CancelPendingDetailRegion() => _detailRegionCancellation?.Cancel();

    private void ScheduleDetailLoadAfterPan()
    {
        // 単調増加する時計で開始可能時刻を保ち、再試行でもパン後の待機を短縮しない。
        _panDetailNotBeforeTicks = Environment.TickCount64 + ViewerSettings.DetailLoadDelayMilliseconds;
        ScheduleDetailLoad();
    }

    private void SetDisplayedDetailRegion(ImageDetailRegion region)
    {
        _currentDetailRegion = region;
        DetailRegionImage.Source = region.Bitmap;
        DetailRegionImage.Width = region.Rectangle.Width;
        DetailRegionImage.Height = region.Rectangle.Height;
        DetailRegionImage.Visibility = Visibility.Visible;
        // 半透明の画素を二重に重ねないよう、高精細領域の下だけプレビューをくり抜く。
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(_viewport.ImageBounds), new RectangleGeometry(region.Bounds));
        clip.Freeze();
        DisplayedImage.Clip = clip;
        UpdateDetailRegionTransform();
        UpdateDetailRegionScalingMode();
    }

    private void ClearDisplayedDetailRegion()
    {
        _currentDetailRegion = null;
        DetailRegionImage.Source = null;
        DetailRegionImage.Visibility = Visibility.Collapsed;
        DisplayedImage.Clip = null;
    }

    private void UpdateDetailRegionViewport()
    {
        if (_session is not { } session || _currentPage is not { } page || !ImageLoader.IsTiff(session.Path)) return;
        double displayScale = GetDisplayScale();
        if (!NeedsDetailRegion(page, displayScale))
        {
            if (_currentDetailRegion is not null) ClearDisplayedDetailRegion();
            return;
        }
        // パン中は読み込まず、保持済みの領域を同じ座標変換で動かす。
        Rect visible = _viewport.VisibleSourceBounds;
        if (_currentDetailRegion?.Covers(page.Index, visible, displayScale) != true
            && session.Details.Get(page.Index, visible, displayScale) is { } cached)
            SetDisplayedDetailRegion(cached);
        UpdateDetailRegionTransform();
    }

    private void UpdateDetailRegionTransform()
    {
        if (_currentDetailRegion is not { } region) return;
        DetailRegionTransform.Matrix = new Matrix(_viewport.Scale, 0, 0, _viewport.Scale,
            _viewport.Offset.X + region.Rectangle.X * _viewport.Scale,
            _viewport.Offset.Y + region.Rectangle.Y * _viewport.Scale);
    }

    private void UpdateDetailRegionScalingMode()
    {
        if (_currentDetailRegion is not { } region) return;
        double scale = GetDisplayScale() * Math.Min(region.Rectangle.Width / (double)region.Bitmap.PixelWidth,
            region.Rectangle.Height / (double)region.Bitmap.PixelHeight);
        RenderOptions.SetBitmapScalingMode(DetailRegionImage, scale < ViewerSettings.BitmapSmoothScaleThreshold
            ? (_isInteracting ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality)
            : BitmapScalingMode.NearestNeighbor);
    }
}
