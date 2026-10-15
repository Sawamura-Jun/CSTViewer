using System.Windows;

namespace CSTViewer;

public partial class MainWindow
{
    private bool _hasUserResizedWindow;
    private bool _isApplyingAutomaticWindowSize;
    private Size? _lastWindowSize;

    internal void PrepareEmptyStartup()
    {
        // 画像未指定時は、既定サイズの比率を横長の仮想画像として使う。
        ApplyAutomaticImageWindowSize(new Size(ViewerSettings.WindowWidth, ViewerSettings.WindowHeight));
    }

    private void ApplyAutomaticImageWindowSize(Size imageSize)
    {
        _isApplyingAutomaticWindowSize = true;
        try
        {
            // HWNDだけを先に作るため、初回表示前もページ切り替え後も同じ計算を使える。
            Size chrome = MeasureWindowChrome();
            Rect monitorBounds = WindowScreen.MonitorBounds(this);
            Rect workArea = WindowScreen.WorkArea(this);
            WindowStartupLocation = WindowStartupLocation.Manual;
            for (int pass = 0; pass < 2; pass++)
            {
                Rect bounds = WindowSizing.StartupBounds(imageSize, chrome, new Size(MinWidth, MinHeight),
                    monitorBounds, workArea);
                ApplyWindowBounds(bounds);
                // ツールバーの折り返しとサイズ通知を、自動調整の期間内に確定する。
                UpdateLayout();
                chrome = MeasureWindowChrome();
            }
        }
        finally { _isApplyingAutomaticWindowSize = false; }
    }

    private void ResizeWindowForPageOrientation(ImagePage page)
    {
        // 同期表示では左右配置を維持し、手動変更の履歴はウィンドウを閉じるまで保持する。
        if (_hasUserResizedWindow || WindowState != WindowState.Normal
            || _spreadController.EnabledPartner(this) is not null) return;
        ApplyAutomaticImageWindowSize(new Size(page.SourceWidth, page.SourceHeight));
    }

    private void ObserveWindowSizeChange(Size size)
    {
        // 最小化でサイズ通知が来ても、復元時は最小化前の寸法と比較する。
        if (WindowState == WindowState.Minimized) return;
        if (IsLoaded && !_isApplyingAutomaticWindowSize && !_applyingSpreadWindowBounds
            && _lastWindowSize is { } previous && previous != size)
            _hasUserResizedWindow = true;
        _lastWindowSize = size;
    }
}
