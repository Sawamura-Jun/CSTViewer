using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CSTViewer;

public partial class MainWindow : Window
{
    private readonly ImageViewport _viewport = new();
    private readonly string _performanceLogPath;
    private readonly DirectoryImageListCache _directoryImageCache;
    private bool _isLoading;
    private bool _isPanning;
    private bool _isClosed;
    private Point _lastPanPosition;
    private ImageLoadMeasurement? _pendingRenderMeasurement;
    private DispatcherTimer? _renderMeasurementTimeout;
    private DispatcherTimer? _detailLoadTimer;
    private DispatcherTimer? _zoomInteractionTimer;
    private ImageLoadSession? _session;
    private DirectoryImageList _directoryImages = DirectoryImageList.Empty;
    private ImageLoadSession? _openingSession;
    private ImagePage? _currentPage;
    private bool _isChangingPage;
    private bool _isDetailLoading;
    private bool _isInteracting;
    private bool _isZoomInteracting;
    private bool _isFittingWindow;
    private BitmapSource? _displaySource;
    private BitmapSource? _interactionPreview;
    private BitmapSource? _currentDetailBitmap;
    private long _viewRevision;
    private long _fileNavigationRevision;

    public MainWindow() : this(ViewerSettings.PerformanceLogPath)
    {
    }

    internal MainWindow(string performanceLogPath, DirectoryImageListCache? directoryImageCache = null,
        SpreadController? spreadController = null)
    {
        _performanceLogPath = performanceLogPath;
        _directoryImageCache = directoryImageCache ?? new DirectoryImageListCache();
        _spreadController = spreadController ?? SpreadController.ForDispatcher(Dispatcher);
        InitializeComponent();
        InitializeCropOverlay();
        _spreadController.Register(this);
        UpdateSpreadControls();
    }

    internal async Task PrepareStartupImageAsync(string path)
    {
        Size imageSize;
        try
        {
            imageSize = await Task.Run(() => ImageLoader.ReadSourceSize(path));
        }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            // 寸法取得に失敗しても起動を続け、通常の読み込み処理でエラーを表示する。
            return;
        }
        if (_isClosed) return;

        // HWNDだけを先に作り、非表示のまま枠・ツールバーを含む寸法を確定させる。
        Size chrome = MeasureWindowChrome();
        Rect bounds = WindowSizing.StartupBounds(imageSize, chrome, new Size(MinWidth, MinHeight), WindowScreen.WorkArea(this));
        WindowStartupLocation = WindowStartupLocation.Manual;
        ApplyWindowBounds(bounds);
        // 幅が狭い時はツールバーが折り返すので、その高さを初期サイズにも反映する。
        chrome = MeasureWindowChrome();
        bounds = WindowSizing.StartupBounds(imageSize, chrome, new Size(MinWidth, MinHeight), WindowScreen.WorkArea(this));
        ApplyWindowBounds(bounds);
    }

    private Size MeasureWindowChrome()
    {
        Size clientSize = WindowScreen.ClientSize(this);
        var content = (FrameworkElement)Content;
        content.Measure(clientSize);
        content.Arrange(new Rect(clientSize));
        content.UpdateLayout();
        return new Size(Math.Max(0, Width - ImageViewportCanvas.ActualWidth),
            Math.Max(0, Height - ImageViewportCanvas.ActualHeight));
    }

    private async void OpenImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isCropBusy || IsDiffBusy || IsSaveBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open image",
            Filter = ViewerSettings.ImageFileFilter,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            await OpenImageAsync(dialog.FileName);
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool canOpen = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy && GetDroppedImagePath(e.Data) is not null;
        e.Effects = canOpen ? DragDropEffects.Copy : DragDropEffects.None;
        DropImageOverlay.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        DropImageOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        DropImageOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy && GetDroppedImagePath(e.Data) is string path)
        {
            e.Effects = DragDropEffects.Copy;
            await OpenImageAsync(path);
        }
    }

    private static string? GetDroppedImagePath(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop)
            && data.GetData(DataFormats.FileDrop) is string[] paths
            ? ImageLoader.FindFirstSupportedPath(paths)
            : null;
    }

    internal async Task OpenImageAsync(string path)
    {
        if (_isLoading || _isCropBusy || IsDiffBusy || IsSaveBusy || _isClosed)
        {
            return;
        }

        var session = new ImageLoadSession(path, GetPreviewSize());
        _saveStatus = null;
        _spreadStatus = null;
        _diffStatus = null;
        _openingSession = session;
        var total = CreateMeasurement(session, "AllPagePreviews");
        total?.StartStage("AllPages");
        ImageLoadMeasurement? measurement = CreateMeasurement(session, "FirstPreview");
        measurement?.StartStage("UiPreparation");
        StopPanning();
        CancelCropDrag();
        SetLoadingState(true);
        try
        {
            ImagePage page = await session.LoadPageAsync(0, session.PreviewSize, measurement);
            session.Token.ThrowIfCancellationRequested();
            CompleteRenderMeasurement("SupersededBeforeRenderCallback");
            _session?.Cancel();
            _session = session;
            _openingSession = null;
            _ = UpdateFileNavigationAsync(session);
            session.PageCount = page.PageCount;
            session.LoadedPages = 1;
            if (total is not null)
            {
                total.PageCount = session.PageCount;
                total.LoadedPages = 1;
                total.FileSizeBytes = measurement?.FileSizeBytes;
            }
            session.Previews.Add(page);
            measurement?.StartStage("UiApply");
            ShowPage(page);
            EmptyImageHint.Visibility = Visibility.Collapsed;
            // 先頭ページが使える時点で操作を戻し、残りページは順次先読みする。
            SetLoadingState(false);
            ImageViewportCanvas.Focus();
            if (measurement is not null)
            {
                measurement.ImageReadyMilliseconds = measurement.ElapsedMilliseconds;
                BeginRenderMeasurement(measurement);
            }
            measurement = null;
            for (int index = 1; index < session.PageCount; index++)
            {
                measurement = CreateMeasurement(session, "PagePreview");
                page = await session.LoadPageAsync(index, session.PreviewSize, measurement);
                session.Token.ThrowIfCancellationRequested();
                session.Previews.Add(page);
                session.LoadedPages = index + 1;
                if (total is not null) total.LoadedPages = session.LoadedPages;
                if (_session == session) UpdatePageControls();
                WriteMeasurement(measurement, "Completed");
                measurement = null;
            }
            WriteMeasurement(total, "Completed");
        }
        catch (OperationCanceledException)
        {
            WriteMeasurement(measurement, "Cancelled");
            WriteMeasurement(total, "Cancelled");
        }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            WriteMeasurement(measurement, "Failed", exception);
            WriteMeasurement(total, "Failed", exception);
            session.Error = "Some pages could not be loaded";
            if (!_isClosed && (_session == session || _openingSession == session))
            {
                // 失敗時は表示中の画像を保ち、再度別のファイルを開けるようにする。
                MessageBox.Show(this, $"Could not load the image.\n{Path.GetFileName(path)}\n\n{exception.Message}",
                    "Image loading error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            session.IsPreloading = false;
            if (_openingSession == session)
            {
                _openingSession = null;
                session.Cancel();
                if (!_isClosed) SetLoadingState(false);
            }
            if (!_isClosed && _session == session)
            {
                UpdatePageControls();
                ScheduleDetailLoad();
            }
        }
    }

    private static bool IsImageLoadException(Exception exception) => exception is IOException
        or UnauthorizedAccessException or FormatException or NotSupportedException or ArgumentException
        or System.Runtime.InteropServices.COMException or InvalidOperationException or OutOfMemoryException
        or NetVips.VipsException or DllNotFoundException or TypeInitializationException;

    private ImageLoadMeasurement? CreateMeasurement(ImageLoadSession session, string operation) =>
        ViewerSettings.EnablePerformanceLogging ? new ImageLoadMeasurement(session.Path)
        {
            SessionId = session.Id, Operation = operation,
            Backend = ImageLoader.IsTiff(session.Path) ? "NetVips" : "WPF"
        } : null;

    private void WriteMeasurement(ImageLoadMeasurement? measurement, string outcome, Exception? exception = null)
    {
        if (measurement is not null)
            _ = ImageLoadPerformanceLog.WriteAsync(measurement, outcome, exception, _performanceLogPath);
    }

    private Size GetPreviewSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Size(Math.Max(ViewerSettings.MinimumPreviewDimension, ImageViewportCanvas.ActualWidth * dpi.DpiScaleX),
            Math.Max(ViewerSettings.MinimumPreviewDimension, ImageViewportCanvas.ActualHeight * dpi.DpiScaleY));
    }

    private void ShowPage(ImagePage page)
    {
        _saveStatus = null;
        StopPanning();
        ClearCropSelection();
        _viewRevision++;
        _detailLoadTimer?.Stop();
        _zoomInteractionTimer?.Stop();
        _currentPage = page;
        _isInteracting = false;
        _isZoomInteracting = false;
        _interactionPreview = page.Bitmap;
        _currentDetailBitmap = ImageLoader.IsTiff(_session!.Path) ? _session.Details.Get(page.Index)?.Bitmap : page.Bitmap;
        _displaySource = page.Bitmap;
        DisplayedImage.Source = page.Bitmap;
        // 低解像度のプレビューでも、パン・ズームと寸法表示には元画像の座標を使う。
        DisplayedImage.Width = page.SourceWidth;
        DisplayedImage.Height = page.SourceHeight;
        _viewport.SetImage(new Size(page.SourceWidth, page.SourceHeight));
        ApplyViewport();
        Title = $"{Path.GetFileName(_session.Path)} — Page {page.Index + 1} / {page.PageCount}"
            + $" — {page.SourceWidth:N0} × {page.SourceHeight:N0} px — {ViewerSettings.ApplicationTitle}";
        UpdatePageControls();
        ScheduleDetailLoad();
    }

    private void UpdatePageControls()
    {
        UpdateSpreadControls();
        UpdateStatusText();
        UpdateFileControls();
    }

    private void UpdateStatusText()
    {
        // 保存結果とエラーは既存の下部ステータス欄へ表示し、ツールバーの行数を増やさない。
        PreloadStatusText.Text = _saveStatus ?? _diffStatus ?? _cropStatus ?? _spreadStatus ?? (_session is null ? "Open an image file" : _session.Error
            ?? (_session.IsPreloading ? $"Preloading pages: {_session.LoadedPages} / {_session.PageCount}"
                : $"Page previews loaded: {_session.LoadedPages}"));
    }

    private async Task UpdateFileNavigationAsync(ImageLoadSession session, bool refresh = false)
    {
        long revision = ++_fileNavigationRevision;
        _directoryImages = DirectoryImageList.Empty;
        try
        {
            Task<DirectoryImageList> loading = _directoryImageCache.GetAsync(session.Path, refresh);
            UpdateFileControls();
            // 初回・手動更新だけ列挙し、同一フォルダーでは取得中の処理も共有する。
            var images = await loading;
            if (_isClosed || revision != _fileNavigationRevision || _session != session) return;
            _directoryImages = images.ForPath(session.Path);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // フォルダー変更後に届いた古い一覧で、現在のボタンを上書きしない。
            if (!_isClosed && revision == _fileNavigationRevision) UpdateFileControls();
        }
    }

    private void UpdateFileControls()
    {
        UpdateSpreadControls();
        _spreadController.RefreshControls(this);
    }

    private async void PreviousFileButton_Click(object sender, RoutedEventArgs e) => await ChangeFileAsync(-1);
    private async void NextFileButton_Click(object sender, RoutedEventArgs e) => await ChangeFileAsync(1);

    internal async Task RefreshFileListAsync()
    {
        if (_isClosed || _isLoading || _isChangingPage || _isCropBusy || IsDiffBusy || IsSaveBusy || _directoryImageCache.IsLoading
            || _session is not { } session
            || !ViewerSettings.SupportedImageExtensions.Contains(Path.GetExtension(session.Path))) return;
        await UpdateFileNavigationAsync(session, refresh: true);
    }

    internal Task ChangeFileAsync(int step) => _spreadController.ChangeImagesAsync(this, step, file: true);

    private async Task ChangeFileLocallyAsync(int step)
    {
        if (_isClosed || _isLoading || _isChangingPage || _isCropBusy || IsDiffBusy || IsSaveBusy || _session is null) return;
        if (_directoryImages.GetNeighbor(step) is string path) await OpenImageAsync(path);
    }

    private async void PreviousPageButton_Click(object sender, RoutedEventArgs e) => await ChangePageAsync(-1);
    private async void NextPageButton_Click(object sender, RoutedEventArgs e) => await ChangePageAsync(1);

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 入力欄では文字入力・コピーなどを優先し、画像操作のキーを横取りしない。
        if (Keyboard.FocusedElement is TextBoxBase || e.OriginalSource is TextBoxBase) return;
        if (e.Key == Key.N && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            if (!e.IsRepeat) CreateNewWindow();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == ViewerSettings.SpreadShortcutKey)
        {
            e.Handled = true;
            if (!e.IsRepeat) ToggleSpread();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == ViewerSettings.FitShortcutKey)
        {
            e.Handled = true;
            if (!e.IsRepeat) _spreadController.Fit(this);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == ViewerSettings.DiffShortcutKey)
        {
            e.Handled = true;
            if (!e.IsRepeat) await CreateDiffAsync();
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == ViewerSettings.CloseShortcutKey)
        {
            e.Handled = true;
            if (!e.IsRepeat) _spreadController.Close(this);
            return;
        }
        if (e.Key == Key.Escape && !_isCropBusy)
        {
            ClearCropSelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _cropSelection.HasSelection)
        {
            e.Handled = true;
            if (!e.IsRepeat) await CopyCropAsync();
            return;
        }
        if (e.Key is Key.PageUp or Key.PageDown)
        {
            e.Handled = true;
            await ChangePageAsync(e.Key == Key.PageUp ? -1 : 1);
        }
        else if (Keyboard.Modifiers == ModifierKeys.None
            && e.Key is ViewerSettings.PreviousFileShortcutKey or ViewerSettings.NextFileShortcutKey)
        {
            e.Handled = true;
            // 押しっぱなしのキーリピートで、複数ファイルを意図せず飛ばさない。
            if (!e.IsRepeat) await ChangeFileAsync(e.Key == ViewerSettings.PreviousFileShortcutKey ? -1 : 1);
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && e.Key == ViewerSettings.RefreshFileListShortcutKey)
        {
            e.Handled = true;
            if (!e.IsRepeat) await RefreshFileListAsync();
        }
    }

    internal Task ChangePageAsync(int delta) => _spreadController.ChangeImagesAsync(this, delta, file: false);

    private async Task ChangePageLocallyAsync(int delta)
    {
        if (_isLoading || _isChangingPage || _isCropBusy || IsDiffBusy || IsSaveBusy || _session is not { } session || _currentPage is not { } current) return;
        int index = current.Index + delta;
        if (index < 0 || index >= session.LoadedPages) return;
        StopPanning();
        CancelCropDrag();
        _isChangingPage = true;
        UpdatePageControls();
        ImageLoadMeasurement? measurement = null;
        try
        {
            var page = session.Previews.Get(index);
            if (page is null)
            {
                PreloadStatusText.Text = "Loading page…";
                measurement = CreateMeasurement(session, "PagePreviewReload");
                page = await session.LoadPageAsync(index, session.PreviewSize, measurement);
                session.Previews.Add(page);
            }
            if (!_isClosed && _session == session && !session.Token.IsCancellationRequested) ShowPage(page);
            WriteMeasurement(measurement, "Completed");
        }
        catch (OperationCanceledException) { WriteMeasurement(measurement, "Cancelled"); }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            WriteMeasurement(measurement, "Failed", exception);
            if (_session == session) session.Error = "Could not load the page";
        }
        finally
        {
            _isChangingPage = false;
            if (!_isClosed) UpdatePageControls();
        }
    }

    private void ScheduleDetailLoad(int delayMilliseconds = ViewerSettings.DetailLoadDelayMilliseconds)
    {
        _viewRevision++;
        _detailLoadTimer?.Stop();
        // 先読み中やTIFF以外でも、操作停止後に描画品質を戻すタイマーを動かす。
        if (_isClosed || _isLoading || _isCropBusy || IsDiffBusy || IsSaveBusy || _currentPage is null) return;
        _detailLoadTimer ??= CreateDetailLoadTimer();
        _detailLoadTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
        _detailLoadTimer.Start();
    }

    private DispatcherTimer CreateDetailLoadTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(ViewerSettings.DetailLoadDelayMilliseconds) };
        timer.Tick += DetailLoadTimer_Tick;
        return timer;
    }

    private async void DetailLoadTimer_Tick(object? sender, EventArgs e)
    {
        _detailLoadTimer?.Stop();
        if (_isClosed || _isLoading || _isCropBusy || IsDiffBusy || IsSaveBusy || _isPanning || _isZoomInteracting || _currentPage is not { } page) return;
        _isInteracting = false;
        UpdateBitmapScalingMode();
        if (_isDetailLoading || _session is not { } session) return;
        double displayScale = GetDisplayScale();
        Size requiredSize = page.DetailDecodeSize(displayScale / ViewerSettings.DetailOversamplingFactor);
        BitmapSource source = _currentDetailBitmap ?? page.Bitmap;
        bool decode = !HasEnoughResolution(source, requiredSize) && !session.IsPreloading && ImageLoader.IsTiff(session.Path);
        int level = GetMipmapLevel(source, page, displayScale);
        if (!decode && session.Mipmaps.Get(source, level) is { } cached)
        {
            SetDisplayedBitmap(source, cached);
            UpdateInteractionPreview(source, cached);
            return;
        }
        long revision = _viewRevision;
        _isDetailLoading = true;
        PreloadStatusText.Text = decode ? "Loading image details…" : "Adjusting image display…";
        var measurement = decode ? CreateMeasurement(session, "PageDetail") : null;
        bool retry = false;
        try
        {
            if (decode)
            {
                var detail = await session.LoadPageAsync(page.Index, page.DetailDecodeSize(displayScale), measurement);
                source = detail.Bitmap;
                if (_session == session && !session.Token.IsCancellationRequested)
                {
                    session.Details.Add(detail);
                    if (_currentPage == page) _currentDetailBitmap = source;
                }
            }
            var bitmap = await session.Mipmaps.GetOrCreateAsync(source, GetMipmapLevel(source, page, displayScale), session.Token);
            if (_session == session && !session.Token.IsCancellationRequested)
            {
                // 操作やページ変更で古くなった結果を、現在の表示に上書きしない。
                if (!_isClosed && revision == _viewRevision)
                {
                    SetDisplayedBitmap(source, bitmap);
                    UpdateInteractionPreview(source, bitmap);
                }
                else retry = true;
            }
            else retry = true;
            WriteMeasurement(measurement, "Completed");
        }
        catch (OperationCanceledException) { WriteMeasurement(measurement, "Cancelled"); retry = true; }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            WriteMeasurement(measurement, "Failed", exception);
            if (_session == session) session.Error = "Failed to update the image display";
        }
        finally
        {
            _isDetailLoading = false;
            if (!_isClosed)
            {
                UpdatePageControls();
                if (retry || revision != _viewRevision) ScheduleDetailLoad(_isZoomInteracting
                    ? ViewerSettings.DetailLoadDelayMilliseconds : ViewerSettings.DetailAfterZoomIdleDelayMilliseconds);
            }
        }
    }

    private void BeginRenderMeasurement(ImageLoadMeasurement measurement)
    {
        measurement.StartStage("FirstRenderWait");
        _pendingRenderMeasurement = measurement;
        // Renderingは描画直前の通知であり、GPU処理やモニターへの表示完了は計測しない。
        CompositionTarget.Rendering += Image_FirstRendering;
        _renderMeasurementTimeout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ViewerSettings.FirstRenderMeasurementTimeoutMilliseconds)
        };
        _renderMeasurementTimeout.Tick += RenderMeasurementTimeout_Tick;
        _renderMeasurementTimeout.Start();
    }

    private void Image_FirstRendering(object? sender, EventArgs e)
    {
        // 非表示・最小化中は、別ウィンドウの描画イベントを初回描画として扱わない。
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            CompleteRenderMeasurement("FirstRenderCallback");
        }
    }

    private void RenderMeasurementTimeout_Tick(object? sender, EventArgs e)
    {
        CompleteRenderMeasurement("RenderCallbackNotObserved");
    }

    private void CompleteRenderMeasurement(string outcome)
    {
        if (_pendingRenderMeasurement is not ImageLoadMeasurement measurement)
        {
            return;
        }

        _pendingRenderMeasurement = null;
        CompositionTarget.Rendering -= Image_FirstRendering;
        if (_renderMeasurementTimeout is not null)
        {
            _renderMeasurementTimeout.Stop();
            _renderMeasurementTimeout.Tick -= RenderMeasurementTimeout_Tick;
            _renderMeasurementTimeout = null;
        }
        _ = ImageLoadPerformanceLog.WriteAsync(measurement, outcome, logPath: _performanceLogPath);
    }

    private void SetLoadingState(bool isLoading)
    {
        _isLoading = isLoading;
        if (isLoading)
        {
            _viewRevision++;
            _detailLoadTimer?.Stop();
            _zoomInteractionTimer?.Stop();
            _isInteracting = false;
            _isZoomInteracting = false;
            UpdateBitmapScalingMode();
        }
        // 先読みの完了時も、実行中のクリップ保存・コピーの進行表示と操作制限を保つ。
        UpdateCropBusyState();
    }

    private void FitImageButton_Click(object sender, RoutedEventArgs e) => _spreadController.Fit(this);

    internal void FitImageLocally(bool resizeWindow = true)
    {
        if (IsSpreadBusy || !_viewport.HasImage) return;
        StopPanning();
        CancelCropDrag();
        _zoomInteractionTimer?.Stop();
        _isZoomInteracting = false;
        _isInteracting = false;
        // 全体表示中のFitは倍率を保ってウィンドウを詰め、拡大中は全体表示へ戻す。
        if (!resizeWindow || !_viewport.IsFitMode || !ResizeWindowToDisplayedImage()) _viewport.Fit();
        RestoreCachedDetail();
        ApplyViewport();
        ScheduleDetailLoad();
    }

    private bool ResizeWindowToDisplayedImage()
    {
        var viewportSize = new Size(ImageViewportCanvas.ActualWidth, ImageViewportCanvas.ActualHeight);
        if (viewportSize.Width <= 0 || viewportSize.Height <= 0) return false;
        Size displayedImageSize = _viewport.ImageToView(_viewport.ImageBounds).Size;
        Point center = WindowScreen.Center(this);
        Rect workArea = WindowScreen.WorkArea(this);
        _isFittingWindow = true;
        try
        {
            WindowState = WindowState.Normal;
            UpdateLayout();
            // 幅を詰めてツールバーが折り返した場合も、画像表示領域の高さを維持する。
            for (int pass = 0; pass < 2; pass++)
            {
                var chromeSize = new Size(Math.Max(0, ActualWidth - ImageViewportCanvas.ActualWidth),
                    Math.Max(0, ActualHeight - ImageViewportCanvas.ActualHeight));
                Rect bounds = WindowSizing.FitBounds(center, displayedImageSize, viewportSize,
                    chromeSize, new Size(MinWidth, MinHeight), workArea);
                ApplyWindowBounds(bounds);
                UpdateLayout();
            }
        }
        finally { _isFittingWindow = false; }
        return true;
    }

    private void ImageViewportCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isFittingWindow)
        {
            // 最大化解除やサイズ変更で再フィットせず、操作前の画像倍率を保つ。
            _viewport.Resize(e.NewSize, preserveScale: true);
            return;
        }
        CancelCropDrag();
        BeginViewportInteraction();
        _viewport.Resize(e.NewSize);
        ApplyViewport();
        ScheduleDetailLoad();
    }

    private void ImageViewportCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleViewportWheel(e.GetPosition(ImageViewportCanvas), e.Delta, Keyboard.Modifiers);
        e.Handled = true;
    }

    internal void HandleViewportWheel(Point anchor, int delta, ModifierKeys modifiers)
    {
        if (_isLoading || _isCropBusy || IsDiffBusy || IsSaveBusy || _isClosed || delta == 0) return;
        CancelCropDrag();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (WindowState != WindowState.Normal) return;
            StopPanning();
            Rect bounds = WindowSizing.WheelBounds(new Rect(Left, Top, ActualWidth, ActualHeight),
                new Size(ImageViewportCanvas.ActualWidth, ImageViewportCanvas.ActualHeight),
                new Size(MinWidth, MinHeight), WindowScreen.WorkArea(this), delta);
            ApplyWindowBounds(bounds);
            return;
        }
        if (!_viewport.HasImage || _isLoading)
        {
            return;
        }

        double previousScale = _viewport.Scale;
        _viewport.Zoom(anchor, delta);
        if (_viewport.Scale == previousScale)
        {
            return;
        }
        BeginZoomInteraction();
        ApplyViewport();
        ScheduleDetailLoad();
    }

    private void ApplyWindowBounds(Rect bounds)
    {
        Width = bounds.Width;
        Height = bounds.Height;
        Left = bounds.Left;
        Top = bounds.Top;
    }

    private void ImageViewportBorder_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            _spreadController.Close(this);
        }
        else if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            StopPanning();
            CancelCropDrag();
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
        }
    }

    private void ImageViewportCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewport.HasImage || !CanEditCrop || _cropSelection.IsDragging)
        {
            return;
        }

        ImageViewportCanvas.Focus();
        _lastPanPosition = e.GetPosition(ImageViewportCanvas);
        if (BeginCropAdjustment(_lastPanPosition))
        {
            e.Handled = true;
            return;
        }
        // マウスキャプチャにより、表示領域の外へドラッグしても操作を継続する。
        _isPanning = ImageViewportCanvas.CaptureMouse();
        if (_isPanning) InvalidateViewport();
        ImageViewportCanvas.Cursor = _isPanning ? Cursors.SizeAll : Cursors.Hand;
        e.Handled = true;
    }

    private void ImageViewportCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(ImageViewportCanvas);
        if (_cropSelection.IsDragging)
        {
            bool pressed = _cropSelection.Mode == CropHit.NewSelection
                ? e.RightButton == MouseButtonState.Pressed : e.LeftButton == MouseButtonState.Pressed;
            if (pressed) UpdateCropDrag(position);
            // キャプチャ取得直後の移動通知では更新せず、解放・キャプチャ喪失で操作を終える。
            e.Handled = true;
            return;
        }
        if (!_isPanning)
        {
            UpdateCropCursor(position);
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopPanning();
            return;
        }

        PanImage(position - _lastPanPosition);
        _lastPanPosition = position;
        e.Handled = true;
    }

    internal void PanImage(Vector delta)
    {
        if (!_isPanning || _isLoading || _isClosed) return;
        InvalidateViewport();
        _viewport.Pan(delta);
        ApplyViewport();
    }

    private async void ImageViewportCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropSelection.IsDragging && _cropSelection.Mode != CropHit.NewSelection)
        {
            e.Handled = true;
            await CompleteCropAdjustmentAsync(e.GetPosition(ImageViewportCanvas));
            return;
        }
        if (_isPanning)
        {
            StopPanning();
            e.Handled = true;
        }
    }

    private void ImageViewportCanvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _cropSelection.CancelDrag();
        UpdateCropOverlay();
        bool wasPanning = _isPanning;
        _isPanning = false;
        ImageViewportCanvas.Cursor = _viewport.HasImage ? Cursors.Hand : Cursors.Arrow;
        if (wasPanning) ScheduleDetailLoad();
    }

    private void StopPanning()
    {
        bool wasPanning = _isPanning;
        _isPanning = false;
        if (wasPanning && ImageViewportCanvas.IsMouseCaptured)
        {
            ImageViewportCanvas.ReleaseMouseCapture();
        }
        ImageViewportCanvas.Cursor = _viewport.HasImage ? Cursors.Hand : Cursors.Arrow;
        if (wasPanning) ScheduleDetailLoad();
    }

    private void InvalidateViewport()
    {
        // 古い詳細画像の適用を防ぐ。パンでは補間品質を変更しない。
        _viewRevision++;
        _detailLoadTimer?.Stop();
    }

    private void BeginViewportInteraction()
    {
        // ズームとリサイズのときだけ、描画品質を速度優先に切り替える。
        _isInteracting = true;
        InvalidateViewport();
        UpdateBitmapScalingMode();
    }

    private void BeginZoomInteraction()
    {
        BeginViewportInteraction();
        _isZoomInteracting = true;
        // Python版と同じく、操作中は解像度の充足条件を外してプレビューを優先する。
        if (_interactionPreview is { } preview) SetDisplayedBitmap(preview);
        if (_zoomInteractionTimer is null)
        {
            _zoomInteractionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(ViewerSettings.ZoomInteractionIdleMilliseconds) };
            _zoomInteractionTimer.Tick += ZoomInteractionTimer_Tick;
        }
        _zoomInteractionTimer.Stop();
        _zoomInteractionTimer.Start();
    }

    private void ZoomInteractionTimer_Tick(object? sender, EventArgs e)
    {
        _zoomInteractionTimer?.Stop();
        if (_isClosed || _isLoading) return;
        _isZoomInteracting = false;
        _isInteracting = false;
        if (!_isPanning) RestoreCachedDetail();
        UpdateBitmapScalingMode();
        ScheduleDetailLoad(ViewerSettings.DetailAfterZoomIdleDelayMilliseconds);
    }

    private void RestoreCachedDetail()
    {
        if (_session is not { } session || _currentDetailBitmap is not { } source) return;
        // 段階画像が未生成ならプレビューを保ち、ワーカーで生成してから詳細へ戻す。
        if (session.Mipmaps.Get(source, GetMipmapLevel(source)) is { } bitmap) SetDisplayedBitmap(source, bitmap);
        else if (_interactionPreview is { } preview) SetDisplayedBitmap(preview);
    }

    private void ApplyViewport()
    {
        if (_displaySource is { } source && _session?.Mipmaps.Get(source, GetMipmapLevel(source)) is { } bitmap)
            DisplayedImage.Source = bitmap;
        // RenderTransformはレイアウトの再計算を避け、パン・ズーム時の負荷を抑える。
        ImageTransform.Matrix = new Matrix(_viewport.Scale, 0, 0, _viewport.Scale,
            _viewport.Offset.X, _viewport.Offset.Y);
        UpdateCropOverlay();
        UpdateBitmapScalingMode();
        ZoomStatusText.Text = _viewport.HasImage
            ? $"{_viewport.Scale:P0}{(_viewport.IsFitMode ? " (Fit)" : "")}" : "—";
        if (!_isPanning)
        {
            ImageViewportCanvas.Cursor = _viewport.HasImage ? Cursors.Hand : Cursors.Arrow;
        }
        _spreadController.ScheduleView(this);
    }

    private void SetDisplayedBitmap(BitmapSource source, BitmapSource? bitmap = null)
    {
        _displaySource = source;
        DisplayedImage.Source = bitmap ?? _session?.Mipmaps.Get(source, GetMipmapLevel(source)) ?? source;
        UpdateBitmapScalingMode();
    }

    private double GetDisplayScale()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return _viewport.Scale * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private int GetMipmapLevel(BitmapSource source)
    {
        if (_currentPage is not { } page) return 0;
        return GetMipmapLevel(source, page, GetDisplayScale());
    }

    private static int GetMipmapLevel(BitmapSource source, ImagePage page, double displayScale)
    {
        double scale = displayScale * Math.Min(page.SourceWidth / (double)source.PixelWidth,
            page.SourceHeight / (double)source.PixelHeight);
        return BitmapMipmapCache.RequiredLevel(scale);
    }

    private void UpdateInteractionPreview(BitmapSource source, BitmapSource bitmap)
    {
        // TIFF以外も、最初の全体表示で作った軽い段階画像を操作用プレビューとして使う。
        if (_viewport.IsFitMode && _currentPage?.Bitmap == source && _session is { } session
            && !ImageLoader.IsTiff(session.Path)) _interactionPreview = bitmap;
    }

    private static bool HasEnoughResolution(BitmapSource bitmap, Size size) =>
        bitmap.PixelWidth >= size.Width - 1 && bitmap.PixelHeight >= size.Height - 1;

    private void UpdateBitmapScalingMode()
    {
        if (DisplayedImage.Source is not BitmapSource bitmap) return;
        // WPFの表示寸法をDPIで画面の画素数に換算し、表示中のBitmapに対する倍率を求める。
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = DisplayedImage.Width * _viewport.Scale * dpi.DpiScaleX / bitmap.PixelWidth;
        double scaleY = DisplayedImage.Height * _viewport.Scale * dpi.DpiScaleY / bitmap.PixelHeight;
        // Python版と同じく、0.7未満の縮小または段階画像の描画で滑らか補間を使う。
        bool smooth = Math.Min(scaleX, scaleY) < ViewerSettings.BitmapSmoothScaleThreshold
            || (_displaySource is { } source && !ReferenceEquals(source, bitmap));
        RenderOptions.SetBitmapScalingMode(DisplayedImage, smooth
            ? (_isInteracting ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality)
            : BitmapScalingMode.NearestNeighbor);
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        RemoveSpreadWindowHook();
        _saveCancellation?.Cancel();
        _diffCancellation?.Cancel();
        _spreadController.Unregister(this);
        ClearCropSelection();
        _detailLoadTimer?.Stop();
        if (_detailLoadTimer is not null) _detailLoadTimer.Tick -= DetailLoadTimer_Tick;
        _detailLoadTimer = null;
        _zoomInteractionTimer?.Stop();
        if (_zoomInteractionTimer is not null) _zoomInteractionTimer.Tick -= ZoomInteractionTimer_Tick;
        _zoomInteractionTimer = null;
        _session?.Cancel();
        _openingSession?.Cancel();
        _directoryImageCache.Dispose();
        CompleteRenderMeasurement("WindowClosedBeforeRenderCallback");
        StopPanning();
        // 閉じた画面の画像・キャッシュへの参照を外し、常駐中も回収できるようにする。
        DisplayedImage.Source = null;
        _session = _openingSession = null;
        _currentPage = null;
        _displaySource = _interactionPreview = _currentDetailBitmap = null;
        _directoryImages = DirectoryImageList.Empty;
        if (Application.Current is App app) app.ReleaseClosedWindow(this);
        base.OnClosed(e);
    }
}
