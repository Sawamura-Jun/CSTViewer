using System.Windows;

namespace CSTViewer;

public partial class MainWindow
{
    private bool CanRotateCurrentPage => CanNavigateSpread
        && (_currentPage is not null || _spreadController.EnabledPartner(this)?._currentPage is not null);

    private void RotateRightButton_Click(object sender, RoutedEventArgs e) => RotateCurrentPage(1);
    private void RotateLeftButton_Click(object sender, RoutedEventArgs e) => RotateCurrentPage(-1);

    internal void RotateCurrentPage(int step) => _spreadController.Rotate(this, step);

    internal void RotatePageLocally(int step)
    {
        if (IsSpreadBusy || _session is not { } session || _currentPage is not { } page) return;
        StopPanning();
        ClearCropSelection();
        CancelPendingDetailRegion();
        _detailLoadTimer?.Stop();
        _zoomInteractionTimer?.Stop();
        _isInteracting = _isZoomInteracting = false;
        _detailAfterPanRequested = false;
        // 元ファイルと画像キャッシュを変更せず、現在のページの表示座標だけを回転する。
        int rotation = session.RotatePage(page.Index, step);
        _viewport.SetImage(new Size(page.SourceWidth, page.SourceHeight), rotation);
        // 手動リサイズ前は、回転後の縦横比にも起動時と同じ自動サイズ調整を適用する。
        ResizeWindowForPageOrientation(_viewport.DisplayImageSize);
        RestoreCachedDetail();
        ApplyViewport();
        ScheduleDetailLoad();
    }
}
