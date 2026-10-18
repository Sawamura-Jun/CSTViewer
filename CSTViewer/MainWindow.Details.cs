using System.Windows;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    // 表示中の② 表示用高精細画像。先行する領域画像と、その後に完成するページ全体画像で共用する。
    private ImageDetailRegion? _currentDetailRegion;
    private CancellationTokenSource? _detailRegionCancellation;
    private bool _isDetailDecoding;
    private bool _detailAfterPanRequested;

    // Fit時も①だけで打ち切らず、必要なら②として表示寸法の1.8倍（元解像度が上限）を確保する。
    private static bool NeedsDetailRegion(ImagePage page, double displayScale) =>
        !HasEnoughResolution(page.Bitmap, page.DetailDecodeSize(displayScale));

    /// <summary>② 表示用高精細画像（Loading image details…）を優先して読み込む。</summary>
    private async Task LoadVisibleDetailsAsync(ImageLoadSession session, ImagePage page)
    {
        double displayScale = GetDisplayScale();
        if (!NeedsDetailRegion(page, displayScale))
        {
            ClearDisplayedDetailRegion();
            return;
        }
        ImageDetailRequest request = ImageDetailRequest.Create(_viewport, displayScale);
        if (request.Rectangle.IsEmpty) return;
        // 表示範囲が鮮明なら再利用し、パンで周辺の余白がずれただけでは再デコードしない。
        Rect required = _viewport.VisibleSourceBounds;
        if (_currentDetailRegion?.Covers(page.Index, required, displayScale) == true)
        {
            await ShowCachedDetailAsync(session, page, _currentDetailRegion);
            return;
        }
        if (session.Details.Get(page.Index, required, displayScale) is { } cached)
        {
            await ShowCachedDetailAsync(session, page, cached);
            return;
        }
        long revision = _viewRevision;
        using var cancellation = new CancellationTokenSource();
        _detailRegionCancellation = cancellation;
        _isDetailDecoding = false;
        _isDetailLoading = true;
        UpdateStatusText();
        var measurement = CreateMeasurement(session, "PageDetailRegion");
        bool retry = false;
        try
        {
            var region = await session.LoadDetailRegionAsync(page.Index, request, measurement, cancellation.Token,
                onDecodeStarted: () => _isDetailDecoding = true);
            if (_session == session && !session.Token.IsCancellationRequested && !_isClosed)
            {
                session.Details.Add(region);
                // パン後は更新番号が変わっていても、移動先と必要解像度を満たす結果を直ちに使う。
                bool coversCurrentView = !cancellation.IsCancellationRequested
                    && region.Covers(page.Index, _viewport.VisibleSourceBounds, GetDisplayScale());
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
        _session?.CancelQueuedFullPages();
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

    // ②の領域画像とページ全体画像で表示処理を共用し、縮小時には段階画像（mipmap）も使う。
    private void SetDisplayedDetailRegion(ImageDetailRegion region)
    {
        _currentDetailRegion = region;
        DetailRegionImage.Source = _session?.Mipmaps.Get(region.Bitmap, GetDetailMipmapLevel(region), region.PageIndex) ?? region.Bitmap;
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
        HideDisplayedDetailRegion();
    }

    private void HideDisplayedDetailRegion()
    {
        // 高精細データは保持し、描画元とくり抜きを外してプレビュー全体を描画する。
        DetailRegionImage.Source = null;
        DetailRegionImage.Visibility = Visibility.Collapsed;
        DisplayedImage.Clip = null;
    }

    private void UpdateDetailRegionViewport()
    {
        if (_session is not { } session || _currentPage is not { } page || !ImageLoader.IsTiff(session.Path)) return;
        if (_isZoomInteracting)
        {
            HideDisplayedDetailRegion();
            return;
        }
        double displayScale = GetDisplayScale();
        if (!NeedsDetailRegion(page, displayScale))
        {
            if (_currentDetailRegion is not null) ClearDisplayedDetailRegion();
            return;
        }
        // 操作中に完了した画像も含め、キャッシュへの差し替えは操作終了後に行う。
        Rect visible = _viewport.VisibleSourceBounds;
        if (!_isPanning)
        {
            var cached = session.Details.Get(page.Index, _viewport.ImageBounds, displayScale)
                ?? (_currentDetailRegion?.Covers(page.Index, visible, displayScale) == true
                    ? _currentDetailRegion : session.Details.Get(page.Index, visible, displayScale));
            // 同じ画像でも、ズーム中に隠していた場合は表示とくり抜きを復元する。
            if (cached is not null && (!ReferenceEquals(cached, _currentDetailRegion)
                || DetailRegionImage.Visibility != Visibility.Visible))
                SetDisplayedDetailRegion(cached);
        }
        UpdateDetailRegionTransform();
        if (_currentDetailRegion is { } region
            && session.Mipmaps.Get(region.Bitmap, GetDetailMipmapLevel(region), region.PageIndex) is { } mipmap)
            DetailRegionImage.Source = mipmap;
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
        var bitmap = DetailRegionImage.Source as System.Windows.Media.Imaging.BitmapSource ?? region.Bitmap;
        double scale = GetDisplayScale() * Math.Min(region.Rectangle.Width / (double)bitmap.PixelWidth,
            region.Rectangle.Height / (double)bitmap.PixelHeight);
        RenderOptions.SetBitmapScalingMode(DetailRegionImage, scale < ViewerSettings.BitmapSmoothScaleThreshold
            ? (_isInteracting ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality)
            : BitmapScalingMode.NearestNeighbor);
    }

    private int GetDetailMipmapLevel(ImageDetailRegion region) => BitmapMipmapCache.RequiredLevel(GetDisplayScale()
        * Math.Min(region.Rectangle.Width / (double)region.Bitmap.PixelWidth, region.Rectangle.Height / (double)region.Bitmap.PixelHeight));

    private Task<System.Windows.Media.Imaging.BitmapSource> PrepareDetailMipmapAsync(ImageLoadSession session, ImageDetailRegion region) =>
        session.Mipmaps.GetOrCreateAsync(region.Bitmap, GetDetailMipmapLevel(region), session.Token, region.PageIndex);

    private async Task ShowCachedDetailAsync(ImageLoadSession session, ImagePage page, ImageDetailRegion region)
    {
        try
        {
            await PrepareDetailMipmapAsync(session, region);
            if (CanApplyFullDetail(session, page) && region.Covers(page.Index, _viewport.VisibleSourceBounds, GetDisplayScale()))
                SetDisplayedDetailRegion(region);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            if (_session == session) session.Error = "Failed to update the image display";
        }
    }
}
