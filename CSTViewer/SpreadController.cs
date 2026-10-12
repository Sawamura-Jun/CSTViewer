using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace CSTViewer;

/// <summary>同じUIスレッドのウィンドウを管理し、同期モードの左右配置と操作の同期を行う。</summary>
internal sealed class SpreadController
{
    // Dispatcherごとに共有し、テストや別UIスレッドのウィンドウを混在させない。
    private static readonly ConditionalWeakTable<Dispatcher, SpreadController> Controllers = new();
    private readonly List<MainWindow> _windows = [];
    private MainWindow? _left;
    private MainWindow? _right;
    private MainWindow? _pendingSource;
    private DispatcherTimer? _syncTimer;
    private bool _enabled;
    private bool _applyingView;
    private bool _changingImages;

    public static SpreadController ForDispatcher(Dispatcher dispatcher) => Controllers.GetValue(dispatcher, _ => new());
    public void Register(MainWindow window) => _windows.Add(window);
    private MainWindow? Partner(MainWindow source) => source == _left ? _right : source == _right ? _left : null;
    public MainWindow? EnabledPartner(MainWindow source) => _enabled ? Partner(source) : null;
    public bool IsChangingImages(MainWindow source) => _changingImages && Partner(source) is not null;

    public MainWindow? NearestImageWindow(MainWindow source) => _windows
        .Where(window => window != source && window.IsVisible && window.WindowState != WindowState.Minimized
            && window.DiffImagePath is not null)
        .OrderBy(window => (window.SpreadCenter - source.SpreadCenter).LengthSquared)
        .ThenBy(window => Math.Abs(window.SpreadCenter.X - source.SpreadCenter.X)).FirstOrDefault();

    public void Unregister(MainWindow window)
    {
        if (Partner(window) is not null) ClearPair();
        _windows.Remove(window);
    }

    public void Toggle(MainWindow source)
    {
        if (IsChangingImages(source) || source.IsSpreadBusy) return;
        if (Partner(source) is not null)
        {
            SetEnabled(!_enabled);
            if (_enabled) SynchronizeNow(source);
            ShowMessage(_enabled ? "Sync mode enabled" : "Sync mode disabled");
            BringToFront(source);
            return;
        }

        IntPtr monitor = WindowScreen.MonitorAtCenter(source);
        var candidates = _windows.Where(w => w.IsVisible && w.WindowState != WindowState.Minimized
            && WindowScreen.MonitorAtCenter(w, nearestIfOutside: false) == monitor).ToArray();
        MainWindow? other = candidates.Where(w => w != source)
            .OrderBy(w => (w.SpreadCenter - source.SpreadCenter).LengthSquared)
            .ThenBy(w => Math.Abs(w.SpreadCenter.X - source.SpreadCenter.X)).FirstOrDefault();
        if (other is null)
        {
            source.ShowSpreadMessage("Sync mode requires two windows on the same monitor");
            source.UpdateSpreadControls();
            return;
        }
        if (other.IsSpreadBusy)
        {
            source.ShowSpreadMessage("Wait for the other window to finish loading or saving before enabling sync mode");
            source.UpdateSpreadControls();
            return;
        }

        ClearPair();
        var selected = new[] { source, other }.OrderBy(w => w.SpreadCenter.X)
            .ThenBy(w => _windows.IndexOf(w)).ToArray();
        _left = selected[0];
        _right = selected[1];
        Rect area = WindowScreen.WorkArea(source);
        double halfWidth = Math.Floor(area.Width / 2);
        SetEnabled(true);
        _applyingView = true;
        try
        {
            _left.ArrangeSpread(new Rect(area.Left, area.Top, halfWidth, area.Height));
            _right.ArrangeSpread(new Rect(area.Left + halfWidth, area.Top, area.Width - halfWidth, area.Height));
        }
        finally { _applyingView = false; }
        ShowMessage("Sync mode enabled");
        BringToFront(source);
    }

    private void BringToFront(MainWindow source)
    {
        foreach (var window in new[] { Partner(source), source })
        {
            if (window is null) continue;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
    }

    private void ShowMessage(string message)
    {
        _left?.ShowSpreadMessage(message);
        _right?.ShowSpreadMessage(message);
    }

    private void SetEnabled(bool enabled)
    {
        CancelPendingView();
        _enabled = enabled;
        _left?.SetSpreadEnabled(enabled);
        _right?.SetSpreadEnabled(enabled);
        RefreshControls();
    }

    private void ClearPair()
    {
        SetEnabled(false);
        _left?.RestoreSpreadMinimumSize();
        _right?.RestoreSpreadMinimumSize();
        _left = _right = null;
    }

    public void RefreshControls(MainWindow? changed = null)
    {
        if (changed is not null && Partner(changed) is null) return;
        _left?.UpdateSpreadControls();
        _right?.UpdateSpreadControls();
    }

    public void ScheduleView(MainWindow source)
    {
        if (!_enabled || _applyingView || _changingImages || Partner(source) is null) return;
        _pendingSource = source;
        if (_syncTimer is null)
        {
            _syncTimer = new DispatcherTimer(DispatcherPriority.Input, source.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(ViewerSettings.SpreadSyncIntervalMilliseconds) };
            _syncTimer.Tick += (_, _) =>
            {
                var pending = _pendingSource;
                CancelPendingView();
                if (pending is not null) SynchronizeNow(pending);
            };
        }
        // 連続ドラッグ中は最新の状態だけを約60fpsで送る。
        if (!_syncTimer.IsEnabled) _syncTimer.Start();
    }

    private void CancelPendingView()
    {
        _syncTimer?.Stop();
        _pendingSource = null;
    }

    private void SynchronizeNow(MainWindow source)
    {
        if (!_enabled || _applyingView || _changingImages || EnabledPartner(source) is not { } partner
            || source.SpreadViewState is not { } state) return;
        _applyingView = true;
        try { partner.ApplySpreadView(state); }
        finally { _applyingView = false; }
    }

    public async Task ChangeImagesAsync(MainWindow source, int step, bool file)
    {
        var partner = EnabledPartner(source);
        if (partner is null)
        {
            await source.ChangeImageLocallyAsync(step, file);
            return;
        }
        if (_changingImages || source.IsSpreadBusy || partner.IsSpreadBusy) return;
        CancelPendingView();
        // ファイル移動は先頭プレビューが出た時点で操作を戻し、残りの先読みを待たない。
        if (!file) _changingImages = true;
        RefreshControls();
        try
        {
            // 片側が先頭・末尾でも、移動できる側のページ／ファイルは進める。
            await Task.WhenAll(source.ChangeImageLocallyAsync(step, file), partner.ChangeImageLocallyAsync(step, file));
        }
        finally
        {
            if (!file) _changingImages = false;
            RefreshControls();
        }
    }

    public void Fit(MainWindow source)
    {
        var partner = EnabledPartner(source);
        if (IsChangingImages(source) || source.IsSpreadBusy || partner?.IsSpreadBusy == true) return;
        CancelPendingView();
        _applyingView = true;
        try
        {
            source.FitImageLocally();
            partner?.FitImageLocally();
        }
        finally { _applyingView = false; }
    }

    public void Close(MainWindow source)
    {
        var partner = EnabledPartner(source);
        if (partner is not null)
        {
            ClearPair();
            partner.Close();
        }
        source.Close();
    }
}
