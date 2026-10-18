using System.Windows;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    private ImageDetailRegion? _currentDetailRegion;
    private CancellationTokenSource? _detailRegionCancellation;
    private bool _isDetailDecoding;
    private bool _detailAfterPanRequested;

    // Fitでも表示寸法の1.8倍を確保し、初期プレビューだけで高精細化を打ち切らない。
    private static bool NeedsDetailRegion(ImagePage page, double displayScale) =>
        !HasEnoughResolution(page.Bitmap, page.DetailDecodeSize(displayScale));

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
        // 表示範囲が鮮明なら再利用し、パンで周辺の余白がずれただけでは再デコードしない。
        Rect required = useRegion ? _viewport.VisibleSourceBounds
            : new Rect(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height);
        if (_currentDetailRegion?.Covers(page.Index, required, displayScale) == true) return;
        if (session.Details.Get(page.Index, required, displayScale) is { } cached)
        {
            SetDisplayedDetailRegion(cached);
            return;
        }
        long revision = _viewRevision;
        using var cancellation = new CancellationTokenSource();
        _detailRegionCancellation = cancellation;
        _isDetailDecoding = false;
        _isDetailLoading = true;
        UpdateStatusText();
        var measurement = CreateMeasurement(session, useRegion ? "PageDetailRegion" : "PageDetailFull");
        bool retry = false;
        try
        {
            var region = await session.LoadDetailRegionAsync(page.Index, request, measurement, cancellation.Token, useRegion,
                onDecodeStarted: () => _isDetailDecoding = true);
            if (_session == session && !session.Token.IsCancellationRequested && !_isClosed)
            {
                session.Details.Add(region);
                // パン後は更新番号が変わっていても、移動先と必要解像度を満たす結果を直ちに使う。
                bool coversCurrentView = !cancellation.IsCancellationRequested
                    && region.Covers(page.Index, useRegion ? _viewport.VisibleSourceBounds : _viewport.ImageBounds, GetDisplayScale());
                if ((revision == _viewRevision || coversCurrentView) && _currentPage == page && !_isPanning && !_isZoomInteracting)
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
            _isDetailDecoding = false;
            _isDetailLoading = false;
            if (!_isClosed)
            {
                UpdatePageControls();
                if (retry || revision != _viewRevision) ScheduleDetailRetry();
            }
        }
    }

    private void CancelPendingDetailRegion(bool preserveRunningDetail = false)
    {
        // パンでは開始済みの1件だけを完走させ、順番待ちの要求は取り消す。
        if (!preserveRunningDetail || !_isDetailDecoding) _detailRegionCancellation?.Cancel();
    }

    private void ScheduleDetailLoadAfterPan()
    {
        // キャッシュを直ちに表示し、不足する場合の新規デコードも待ち時間なしで予約する。
        _detailAfterPanRequested = true;
        ScheduleDetailLoad(0, preserveRunningDetail: true);
        if (!_isClosed && !_isLoading && !_isChangingPage && !_isCropBusy && !IsDiffBusy && !IsSaveBusy && !_isZoomInteracting)
            RestoreCachedDetail();
    }

    private void ScheduleDetailRetry()
    {
        if (_isPanning) return;
        // パン前から続く読み込みの完了後も、移動先の確認を遅らせない。
        ScheduleDetailLoad(_detailAfterPanRequested ? 0 : _isZoomInteracting
            ? ViewerSettings.DetailLoadDelayMilliseconds : ViewerSettings.DetailAfterZoomIdleDelayMilliseconds,
            preserveRunningDetail: _detailAfterPanRequested);
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
        // パン中に完了した画像も含め、キャッシュへの差し替えは操作終了後に行う。
        Rect visible = _viewport.VisibleSourceBounds;
        if (!_isPanning && _currentDetailRegion?.Covers(page.Index, visible, displayScale) != true
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
