using System.Windows;

namespace CSTViewer;

public partial class MainWindow
{
    private bool CanApplyFullDetail(ImageLoadSession session, ImagePage page) => !_isClosed && !_isLoading
        && _session == session && _currentPage == page && !session.Token.IsCancellationRequested
        && !_isPanning && !_isZoomInteracting;

    /// <summary>② 表示用高精細画像のページ全体版をバックグラウンド生成し、キャッシュへ保存する。</summary>
    private void StartFullPageDetails(ImageLoadSession session, ImagePage page)
    {
        if (!CanApplyFullDetail(session, page) || _isCropBusy || IsDiffBusy || IsSaveBusy
            || session.IsFullPageLoading(page.Index)) return;
        var bounds = new Rect(0, 0, page.SourceWidth, page.SourceHeight);
        double displayScale = GetDisplayScale();
        // Fit時など、先行領域が既にページ全体と必要解像度を満たす場合は二重生成しない。
        if (session.Details.Get(page.Index, bounds, displayScale) is not null) return;
        if (page.Bitmap.PixelWidth == page.SourceWidth && page.Bitmap.PixelHeight == page.SourceHeight)
        {
            // プレビューが既に元解像度なら、同じページをデコードし直さず共有する。
            session.Details.Add(new ImageDetailRegion(page.Index,
                new Int32Rect(0, 0, page.SourceWidth, page.SourceHeight), page.Bitmap));
            return;
        }
        // 次ページを早くめくれるよう、②のページ全体版は①のプレビュー先読み完了後に生成する。
        // 先読み完了時の詳細更新予約で、その時点で表示しているページを再確認する。
        if (session.IsPreloading) return;
        if (NeedsDetailRegion(page, displayScale)
            && _currentDetailRegion?.Covers(page.Index, _viewport.VisibleSourceBounds, displayScale) != true) return;
        _ = LoadFullPageDetailsAsync(session, page, displayScale);
    }

    private async Task LoadFullPageDetailsAsync(ImageLoadSession session, ImagePage page, double displayScale)
    {
        var measurement = CreateMeasurement(session, "PageDetailFull");
        bool cancelled = false;
        bool retry = false;
        try
        {
            Task<ImageDetailRegion> loading = session.LoadFullPageAsync(page, displayScale, measurement);
            UpdateStatusText();
            var region = await loading;
            if (CanApplyFullDetail(session, page)
                && region.ContainsView(page.Index, _viewport.VisibleSourceBounds))
            {
                await PrepareDetailMipmapAsync(session, region);
                // ファイルやページの変更、パン・ズームが挟まった場合はキャッシュだけを残す。
                if (CanApplyFullDetail(session, page))
                {
                    measurement?.StartStage("UiApply");
                    // 新しい領域画像の読み込み中も、現在使える最も細かい画像を先に表示する。
                    UpdateDetailRegionViewport();
                }
            }
            // 暫定表示に使えても、拡大後の1.8倍に不足する場合は解像度の更新を予約する。
            retry = CanApplyFullDetail(session, page)
                && session.Details.Get(page.Index, _viewport.ImageBounds, GetDisplayScale()) is null;
            WriteMeasurement(measurement, "Completed");
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            WriteMeasurement(measurement, "Cancelled");
        }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            WriteMeasurement(measurement, "Failed", exception);
            if (_session == session) session.Error = "Failed to load the full image";
        }
        finally
        {
            if (!_isClosed && _session == session)
            {
                UpdatePageControls();
                if ((cancelled || retry) && CanApplyFullDetail(session, page) && _detailLoadTimer?.IsEnabled != true)
                    ScheduleDetailLoad(0, preserveRunningDetail: true);
            }
        }
    }
}
