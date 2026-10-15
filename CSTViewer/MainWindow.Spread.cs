using System.Windows;
using System.Windows.Interop;

namespace CSTViewer;

public partial class MainWindow
{
    private readonly SpreadController _spreadController;
    private Size? _minimumSizeBeforeSpread;
    private string? _spreadStatus;

    internal bool IsSpreadBusy => _isClosed || _isLoading || _isChangingPage || _isCropBusy || IsDiffBusy || IsSaveBusy;
    internal Point SpreadCenter => WindowScreen.Center(this);
    internal ViewportSyncState? SpreadViewState => _viewport.GetSyncState();
    private bool CanNavigateSpread => !_spreadController.IsChangingImages(this) && !IsSpreadBusy
        && _spreadController.EnabledPartner(this)?.IsSpreadBusy != true;

    private void NewWindowButton_Click(object sender, RoutedEventArgs e) => CreateNewWindow();
    private void SpreadLayoutButton_Click(object sender, RoutedEventArgs e) => ToggleSpread();
    internal void ToggleSpread() => _spreadController.Toggle(this);

    internal MainWindow CreateNewWindow()
    {
        var window = new MainWindow(_performanceLogPath, spreadController: _spreadController)
        { WindowStartupLocation = WindowStartupLocation.Manual };
        Rect area = WindowScreen.WorkArea(this);
        window.Left = area.Left + Math.Max(0, (area.Width - window.Width) / 2);
        window.Top = area.Top + Math.Max(0, (area.Height - window.Height) / 2);
        window.PrepareEmptyStartup();
        window.Show();
        return window;
    }

    internal void ArrangeSpread(Rect bounds)
    {
        _applyingSpreadWindowBounds = true;
        try
        {
            // 最大化中も、配置前の通常表示サイズを復元先として保存する。
            Size normalSize = WindowState != WindowState.Normal && !RestoreBounds.IsEmpty
                ? RestoreBounds.Size : new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            // boundsは表示枠の物理ピクセル。WPFの最小寸法だけDIPへ変換する。
            Rect logicalBounds = WindowScreen.FromPixels(this, bounds);
            _minimumSizeBeforeSpread ??= new Size(MinWidth, MinHeight);
            MinWidth = Math.Min(ViewerSettings.SpreadMinimumWindowWidth, logicalBounds.Width);
            MinHeight = Math.Min(MinHeight, logicalBounds.Height);
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var handle = new WindowInteropHelper(this).Handle;
            SetSpreadWindowPos(handle, IntPtr.Zero, (int)bounds.Left, (int)bounds.Top,
                (int)bounds.Width, (int)bounds.Height, SwpNoZOrder | SwpNoActivate);
            UpdateLayout();
            // 通常表示へ戻して移動先のDPIを確定してから、透明な枠を補正する。
            logicalBounds = WindowScreen.FromPixels(this, bounds);
            MinWidth = Math.Min(ViewerSettings.SpreadMinimumWindowWidth, logicalBounds.Width);
            MinHeight = Math.Min(_minimumSizeBeforeSpread.Value.Height, logicalBounds.Height);
            Rect outer = WindowScreen.OuterBoundsForVisibleFrame(this, bounds);
            SetSpreadWindowPos(handle, IntPtr.Zero,
                (int)outer.Left, (int)outer.Top, (int)outer.Width, (int)outer.Height, SwpNoZOrder | SwpNoActivate);
            UpdateLayout();
            _spreadWindowPlacement.Capture(normalSize, new Size(ActualWidth, ActualHeight));
            // 左右への配置時はウィンドウ寸法を保ち、画像だけを全体表示にする。
            FitImageLocally(resizeWindow: false);
        }
        finally { _applyingSpreadWindowBounds = false; }
    }

    internal void RestoreSpreadMinimumSize()
    {
        _spreadWindowPlacement.Cancel();
        RestoreSpreadMinimumConstraints();
    }

    private void RestoreSpreadMinimumConstraints()
    {
        if (_minimumSizeBeforeSpread is not { } minimum) return;
        MinWidth = minimum.Width;
        MinHeight = minimum.Height;
        _minimumSizeBeforeSpread = null;
    }

    internal void SetSpreadEnabled(bool enabled)
    {
        SpreadLayoutButton.IsChecked = enabled;
        _spreadStatus = null;
    }

    internal void ShowSpreadMessage(string message)
    {
        _saveStatus = null;
        _diffStatus = null;
        _cropStatus = null;
        _spreadStatus = message;
        UpdateStatusText();
    }

    internal void UpdateSpreadControls()
    {
        var partner = _spreadController.EnabledPartner(this);
        bool available = CanNavigateSpread;
        PreviousPageButton.IsEnabled = available && (CanStepPage(-1) || partner?.CanStepPage(-1) == true);
        NextPageButton.IsEnabled = available && (CanStepPage(1) || partner?.CanStepPage(1) == true);
        PreviousFileButton.IsEnabled = available && (_directoryImages.GetNeighbor(-1) is not null
            || partner?._directoryImages.GetNeighbor(-1) is not null);
        NextFileButton.IsEnabled = available && (_directoryImages.GetNeighbor(1) is not null
            || partner?._directoryImages.GetNeighbor(1) is not null);
        FitImageButton.IsEnabled = available && (_viewport.HasImage || partner?._viewport.HasImage == true);
        SpreadLayoutButton.IsEnabled = available;
        SaveImageButton.IsEnabled = !IsSpreadBusy && !_spreadController.IsChangingImages(this) && _currentPage is not null;
        DiffImageButton.IsEnabled = !IsSpreadBusy && !_spreadController.IsChangingImages(this) && DiffImagePath is not null;
    }

    private bool CanStepPage(int step) => _session is not null && _currentPage is { } page
        && page.Index + step >= 0 && page.Index + step < _session.LoadedPages;

    internal Task ChangeImageLocallyAsync(int step, bool file) => file ? ChangeFileLocallyAsync(step) : ChangePageLocallyAsync(step);

    internal void ApplySpreadView(ViewportSyncState state)
    {
        if (IsSpreadBusy || _viewport.GetSyncState() is not { } previousState) return;
        double previousRatio = previousState.ZoomRatio;
        CancelCropDrag();
        if (!_viewport.ApplySyncState(state)) return;
        if (state.IsFitMode)
        {
            _zoomInteractionTimer?.Stop();
            _isZoomInteracting = _isInteracting = false;
            RestoreCachedDetail();
        }
        else if (Math.Abs(state.ZoomRatio - previousRatio) > Math.Max(state.ZoomRatio, previousRatio)
            * ViewerSettings.SpreadSyncZoomRatioEpsilon) BeginZoomInteraction();
        else InvalidateViewport();
        ApplyViewport();
        ScheduleDetailLoad();
    }
}
