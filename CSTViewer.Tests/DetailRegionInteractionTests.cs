using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class DetailRegionInteractionTests
{
    [Fact]
    public Task Preloading_PrioritizesNextPreviewAndRefinesCurrentPageAfterCompletion() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false, pausedFirstPreview = false;
        ImageLoadSession? session = null;
        Task opening = Task.CompletedTask;
        var titleProperty = DependencyPropertyDescriptor.FromProperty(Window.TitleProperty, typeof(MainWindow));
        EventHandler pauseAfterFirstPreview = (_, _) =>
        {
            if (pausedFirstPreview || !window.Title.Contains(Path.GetFileName(path))) return;
            pausedFirstPreview = true;
            // 先頭ページの表示直後に枠を押さえ、画像の大きさに依存せず先読み途中を再現する。
            held = gate.Wait(0);
            Assert.True(held);
        };
        titleProperty.AddValueChanged(window, pauseAfterFirstPreview);

        async Task RunNextDecodeAndPauseAsync(Task decoding)
        {
            // 既に順番待ちの1件だけを通し、その解放時に再び枠を押さえる。
            Task paused = gate.WaitAsync();
            gate.Release();
            held = false;
            await paused.WaitAsync(TimeSpan.FromSeconds(8));
            held = true;
            await decoding.WaitAsync(TimeSpan.FromSeconds(8));
        }

        try
        {
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
            var bitmap = BitmapSource.Create(4096, 3072, 96, 96, PixelFormats.Gray8, null, new byte[4096 * 3072], 4096);
            for (int index = 0; index < 3; index++) encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            window.Show();
            opening = window.OpenImageAsync(path);
            await WaitForAsync(() => pausedFirstPreview);
            session = Session(window);
            Assert.True(session.IsPreloading);
            Assert.Equal(1, session.LoadedPages);

            await RunNextDecodeAndPauseAsync(StartZoomDetailLoad(window));
            Assert.NotNull(CurrentRegion(window));
            Assert.False(IsFullPage(CurrentRegion(window), 4096, 3072));
            StartFullPageDetails(window);
            Assert.False(session.IsFullPageLoading(0));

            await RunNextDecodeAndPauseAsync(WaitForAsync(() => session.LoadedPages == 2));
            Assert.True(((Button)window.FindName("NextPageButton")).IsEnabled);
            await window.ChangeImageLocallyAsync(1, file: false);
            Assert.Equal(1, Page(window).Index);
            Assert.Same(Page(window).Bitmap, ((Image)window.FindName("DisplayedImage")).Source);
            await RunNextDecodeAndPauseAsync(StartZoomDetailLoad(window));
            Assert.Equal(1, CurrentRegion(window)!.PageIndex);
            StartFullPageDetails(window);
            Assert.True(session.IsPreloading);
            Assert.False(session.IsFullPageLoading(1));

            gate.Release();
            held = false;
            await opening.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(session.IsPreloading);
            Assert.Equal(3, session.LoadedPages);
            // 先読み終了後は、先頭ページではなく移動先のページを自動で高精細化する。
            await WaitForAsync(() => CurrentRegion(window)?.PageIndex == 1 && IsFullPage(CurrentRegion(window), 4096, 3072));
            Assert.Null(session.Details.Get(0, new Rect(0, 0, 4096, 3072), 1));
        }
        finally
        {
            titleProperty.RemoveValueChanged(window, pauseAfterFirstPreview);
            if (held) gate.Release();
            await CloseAsync(window, session);
            await opening.WaitAsync(TimeSpan.FromSeconds(8));
            File.Delete(path);
        }
    });

    [Fact]
    public Task TransparentPage_KeepsAppearanceWhenReplacingRegionWithFullPage() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        ImageLoadSession? session = null;
        bool previousSetting = ViewerSettings.KeepZoomStartImage;
        try
        {
            ViewerSettings.KeepZoomStartImage = true;
            var pixels = new byte[2048 * 1536 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 90;
                pixels[offset + 1] = 120;
                pixels[offset + 2] = 160;
                pixels[offset + 3] = 128;
            }
            ImageDetailRegionTests.Save(path, BitmapSource.Create(2048, 1536, 96, 96, PixelFormats.Bgra32, null, pixels, 2048 * 4));
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window);
            byte[] before = RenderCenter(Canvas(window));
            StartFullPageDetails(window);
            await WaitForAsync(() => IsFullPage(CurrentRegion(window), 2048, 1536));
            Assert.Equal(PixelFormats.Bgra32, CurrentRegion(window)!.Bitmap.Format);
            // 高精細画像の下でプレビューを重ねると、半透明画素の色が変わってしまう。
            Assert.Equal(before, RenderCenter(Canvas(window)));
            Assert.NotNull(((Image)window.FindName("DisplayedImage")).Clip);
            var canvas = Canvas(window);
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), -120, ModifierKeys.None);
            Assert.Equal(Visibility.Visible, ((Image)window.FindName("DetailRegionImage")).Visibility);
            // ズーム中もくり抜きを維持し、半透明の画素を重ねて描画しない。
            Assert.NotNull(((Image)window.FindName("DisplayedImage")).Clip);
            Assert.Equal(before, RenderCenter(canvas));
            Method(window, "ZoomInteractionTimer_Tick", null, EventArgs.Empty);
            Assert.Equal(Visibility.Visible, ((Image)window.FindName("DetailRegionImage")).Visibility);
            Assert.NotNull(((Image)window.FindName("DisplayedImage")).Clip);
            Assert.Equal(before, RenderCenter(canvas));
        }
        finally
        {
            ViewerSettings.KeepZoomStartImage = previousSetting;
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Theory]
    [InlineData(120, true)]
    [InlineData(-120, true)]
    [InlineData(120, false)]
    [InlineData(-120, false)]
    public Task ContinuousZoom_UsesSelectedImageThenRestoresCachedDetail(int delta, bool keepDetail) => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        ImageLoadSession? session = null;
        bool previousSetting = ViewerSettings.KeepZoomStartImage;
        try
        {
            ViewerSettings.KeepZoomStartImage = keepDetail;
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window);
            StartFullPageDetails(window);
            await WaitForAsync(() => IsFullPage(CurrentRegion(window), 4096, 3072));
            var full = CurrentRegion(window);
            var canvas = Canvas(window);
            var image = (Image)window.FindName("DisplayedImage");
            var detail = (Image)window.FindName("DetailRegionImage");
            var detailSource = detail.Source;
            var clip = image.Clip;
            var anchor = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            // 入力が続く間は、開始時に選んだ描画元とくり抜きを使い続ける。
            for (int step = 0; step < 4; step++)
            {
                window.HandleViewportWheel(anchor, delta, ModifierKeys.None);
                await Task.Delay(70);
                Assert.True(Field<bool>(window, "_isZoomInteracting"));
                Assert.Same(Page(window).Bitmap, image.Source);
                Assert.Equal(keepDetail ? Visibility.Visible : Visibility.Collapsed, detail.Visibility);
                Assert.Same(keepDetail ? detailSource : null, detail.Source);
                Assert.Same(keepDetail ? clip : null, image.Clip);
                Assert.Same(full, CurrentRegion(window));
            }
            await WaitForAsync(() => !Field<bool>(window, "_isZoomInteracting") && detail.Visibility == Visibility.Visible);
            Assert.Same(full, CurrentRegion(window));
            Assert.Same(full, session.Details.Get(0, Viewport(window).ImageBounds, 1));
            Assert.NotNull(image.Clip);
            Assert.False(IsDetailLoading(window));
        }
        finally
        {
            ViewerSettings.KeepZoomStartImage = previousSetting;
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Fact]
    public Task LowerResolutionFullPageCompletingDuringZoom_IsCachedAndUpgradedAfterZoomEnds() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window, zoomSteps: 4);
            var first = CurrentRegion(window);
            double initialScale = DisplayScale(window);
            StartFullPageDetails(window);
            Task<ImageDetailRegion> loading = session.LoadFullPageAsync(Page(window), DisplayScale(window), null);
            var canvas = Canvas(window);
            var image = (Image)window.FindName("DisplayedImage");
            var detail = (Image)window.FindName("DetailRegionImage");
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120, ModifierKeys.None);
            // デコード完了までズーム状態を保ち、完了通知で描画元が変わらないことを確認する。
            Field<DispatcherTimer>(window, "_zoomInteractionTimer").Stop();
            var full = await loading;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(full, session.Details.Get(0, Viewport(window).ImageBounds, initialScale));
            Assert.Same(first, CurrentRegion(window));
            Assert.Equal(Visibility.Collapsed, detail.Visibility);
            Assert.Null(detail.Source);
            Assert.Null(image.Clip);
            Assert.Same(Page(window).Bitmap, image.Source);
            Method(window, "ZoomInteractionTimer_Tick", null, EventArgs.Empty);
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.True(CurrentRegion(window)!.CoversForDisplay(0, Viewport(window).VisibleSourceBounds, DisplayScale(window)));
            Assert.False(CurrentRegion(window)!.Covers(0, Viewport(window).VisibleSourceBounds, DisplayScale(window)));
            Assert.NotNull(image.Clip);
            // 完成済みの②を暫定表示しながら、拡大後の1.8倍に必要な画素数まで更新する。
            await WaitForAsync(() => IsWholePageAtCurrentResolution(window) && !session.IsFullPageLoading(0));
            Assert.True(CurrentRegion(window)!.Bitmap.PixelWidth > full.Bitmap.PixelWidth);
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.NotNull(image.Clip);
        }
        finally { await CloseAsync(window, session); File.Delete(path); }
    });

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    public Task ZoomIdle_RestoresCachedDetailBeforeDecodeThenRefines(int zoomSteps, bool enoughForDisplay) => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window, zoomSteps: 2);
            StartFullPageDetails(window);
            await WaitForAsync(() => IsWholePageAtCurrentResolution(window) && !session.IsFullPageLoading(0));
            var previousDetail = CurrentRegion(window)!;
            await gate.WaitAsync();
            held = true;
            var canvas = Canvas(window);
            var detail = (Image)window.FindName("DetailRegionImage");
            var image = (Image)window.FindName("DisplayedImage");
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120 * zoomSteps, ModifierKeys.None);
            Assert.Equal(Visibility.Collapsed, detail.Visibility);
            Assert.Null(detail.Source);
            Assert.Null(image.Clip);
            Assert.Equal(enoughForDisplay, previousDetail.CoversForDisplay(0, Viewport(window).VisibleSourceBounds, DisplayScale(window)));
            Assert.False(previousDetail.Covers(0, Viewport(window).VisibleSourceBounds, DisplayScale(window)));
            // 新規デコード枠を塞いだままでも、220msの操作終了判定で保存済みの②が復帰する。
            await WaitForAsync(() => !Field<bool>(window, "_isZoomInteracting")
                && detail.Visibility == Visibility.Visible && IsDetailLoading(window));
            Assert.Same(previousDetail, CurrentRegion(window));
            Assert.Same(previousDetail.Bitmap, detail.Source);
            Assert.NotNull(image.Clip);
            Assert.Null(session.Details.Get(0, Viewport(window).VisibleSourceBounds, DisplayScale(window)));
            gate.Release();
            held = false;
            await WaitForAsync(() => IsWholePageAtCurrentResolution(window)
                && !IsDetailLoading(window) && !session.IsFullPageLoading(0));
            Assert.True(CurrentRegion(window)!.Bitmap.PixelWidth > previousDetail.Bitmap.PixelWidth);
            Assert.Equal(Visibility.Visible, detail.Visibility);
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Fact]
    public Task DisplaySelection_PrefersWholePageForRoundingButKeepsSharperRegion() => RunOnStaAsync(() =>
    {
        var window = CreateWindow();
        var session = new ImageLoadSession("not-needed.tif", new Size(100, 50)) { IsPreloading = false, PageCount = 1 };
        var page = new ImagePage(0, 1, 1000, 500, PreviewLoadingTests.CreateBitmap(100, 50, 71));
        try
        {
            typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            typeof(MainWindow).GetField("_currentPage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, page);
            var viewport = Viewport(window);
            viewport.Resize(new Size(100, 50));
            viewport.SetImage(new Size(1000, 500));
            viewport.Zoom(new Point(50, 25), 120 * 12);
            var full = new ImageDetailRegion(0, new Int32Rect(0, 0, 1000, 500), PreviewLoadingTests.CreateBitmap(360, 180, 71));
            session.Details.Add(full);
            var rounded = new ImageDetailRegion(0, new Int32Rect(450, 225, 100, 50), PreviewLoadingTests.CreateBitmap(37, 19, 71));
            Method(window, "SetDisplayedDetailRegion", rounded);
            Method(window, "UpdateDetailRegionViewport");
            Assert.Same(full, CurrentRegion(window));
            // より細かい領域を表示中に低倍率の全体画像が戻っても、表示品質を下げない。
            var sharper = rounded with { Bitmap = PreviewLoadingTests.CreateBitmap(72, 36, 71) };
            Method(window, "SetDisplayedDetailRegion", sharper);
            Method(window, "UpdateDetailRegionViewport");
            Assert.Same(sharper, CurrentRegion(window));
            // 表示全体を含まなくなった領域は使わず、ページ全体のキャッシュへ戻す。
            viewport.Pan(new Vector(-100, 0));
            Method(window, "UpdateDetailRegionViewport");
            Assert.Same(full, CurrentRegion(window));
            Assert.Equal(Visibility.Visible, ((Image)window.FindName("DetailRegionImage")).Visibility);
        }
        finally { window.Close(); session.Cancel(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task VisibleRegion_PrecedesPanningMarginAndFullPageAtSameResolution() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window, zoomSteps: 4);
            var first = CurrentRegion(window)!;
            var viewport = Viewport(window);
            double scale = DisplayScale(window);
            var fullRequest = ImageDetailRequest.CreateFullImage(viewport, scale);
            var marginRequest = ImageDetailRequest.Create(viewport, scale, includeMargin: true);
            Assert.Equal(ImageDetailRequest.Create(viewport, scale).Rectangle, first.Rectangle);
            Assert.NotEqual(marginRequest.Rectangle, first.Rectangle);
            Assert.Equal(PixelFormats.Gray8, first.Bitmap.Format);
            Matrix before = ((MatrixTransform)((Image)window.FindName("DisplayedImage")).RenderTransform).Matrix;
            // 余白が順番待ちの間にも、画面内の高精細画像が表示される。
            await gate.WaitAsync();
            held = true;
            Task marginLoad = (Task)Method(window, "LoadDetailMarginAsync", session, Page(window))!;
            Assert.False(marginLoad.IsCompleted);
            Assert.Same(first, CurrentRegion(window));
            Assert.Equal(Visibility.Visible, ((Image)window.FindName("DetailRegionImage")).Visibility);
            Assert.False(session.IsFullPageLoading(0));
            gate.Release();
            held = false;
            await marginLoad.WaitAsync(TimeSpan.FromSeconds(8));
            var withMargin = CurrentRegion(window)!;
            Assert.Equal(marginRequest.Rectangle, withMargin.Rectangle);
            Assert.True(withMargin.Covers(0, viewport.VisibleSourceBounds, scale));
            // 全体読み込みを待機させても、余白まで高精細化した画像を使用できる。
            await gate.WaitAsync();
            held = true;
            StartFullPageDetails(window);
            Assert.True(session.IsFullPageLoading(0));
            Assert.Same(withMargin, CurrentRegion(window));
            Assert.Equal("Loading image details…", ((TextBlock)window.FindName("PreloadStatusText")).Text);
            gate.Release();
            held = false;
            await WaitForAsync(() => IsWholePageAtCurrentResolution(window) && !session.IsFullPageLoading(0));
            Assert.InRange(CurrentRegion(window)!.Bitmap.PixelWidth,
                (int)Math.Floor(4096 * fullRequest.DecodeScale) - 1, (int)Math.Ceiling(4096 * fullRequest.DecodeScale) + 1);
            Assert.InRange(CurrentRegion(window)!.Bitmap.PixelHeight,
                (int)Math.Floor(3072 * fullRequest.DecodeScale) - 1, (int)Math.Ceiling(3072 * fullRequest.DecodeScale) + 1);
            Assert.Equal(before, ((MatrixTransform)((Image)window.FindName("DisplayedImage")).RenderTransform).Matrix);
            Assert.Equal(1, session.Details.Count);
            Assert.Equal(PixelFormats.Gray8, CurrentRegion(window)!.Bitmap.Format);
            var whole = CurrentRegion(window);
            await gate.WaitAsync();
            held = true;
            BeginPan(Canvas(window));
            window.PanImage(new Vector(-20, -10));
            Canvas(window).ReleaseMouseCapture();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(whole, CurrentRegion(window));
            Assert.False(IsDetailLoading(window));
            Assert.False(session.IsFullPageLoading(0));
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Fact]
    public Task Pan_CancelsQueuedMarginAndRefinesNewVisibleRegion() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window, zoomSteps: 4);
            var first = CurrentRegion(window)!;
            await gate.WaitAsync();
            held = true;
            Task marginLoad = (Task)Method(window, "LoadDetailMarginAsync", session, Page(window))!;
            var token = Field<CancellationTokenSource>(window, "_detailMarginCancellation").Token;

            var canvas = Canvas(window);
            BeginPan(canvas);
            window.PanImage(new Vector(-80, 0));
            Assert.True(token.IsCancellationRequested);
            await marginLoad.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Same(first, CurrentRegion(window));
            var viewport = Viewport(window);
            Assert.False(first.ContainsView(0, viewport.VisibleSourceBounds));

            canvas.ReleaseMouseCapture();
            await WaitForAsync(() => IsDetailLoading(window));
            gate.Release();
            held = false;
            await WaitForAsync(() => CurrentRegion(window)?.Covers(0, viewport.VisibleSourceBounds, DisplayScale(window)) == true);
            Assert.NotSame(first, CurrentRegion(window));
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Fact]
    public Task Fit_DisplaysRequiredResolutionImmediatelyAndUpgradesWhenZooming() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            window.Show();
            await window.OpenImageAsync(path);
            session = Session(window);
            var page = Page(window);
            var image = (Image)window.FindName("DisplayedImage");
            var fitSize = page.DetailDecodeSize(DisplayScale(window));
            Assert.True(Viewport(window).IsFitMode);
            Assert.InRange(page.Bitmap.PixelWidth, (int)fitSize.Width - 1, (int)fitSize.Width + 1);
            Assert.InRange(page.Bitmap.PixelHeight, (int)fitSize.Height - 1, (int)fitSize.Height + 1);
            Assert.Same(page.Bitmap, image.Source);
            // デコード枠を塞いだ状態で詳細更新を実行し、初回表示だけで必要な画素数がそろうことを確認する。
            await gate.WaitAsync();
            held = true;
            Method(window, "DetailLoadTimer_Tick", null, EventArgs.Empty);
            StartFullPageDetails(window);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Null(CurrentRegion(window));
            Assert.Same(page.Bitmap, image.Source);
            Assert.False(session.IsFullPageLoading(0));
            Assert.False(IsDetailLoading(window));
            gate.Release();
            held = false;
            var canvas = Canvas(window);
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120 * 8, ModifierKeys.None);
            await WaitForAsync(() => !Field<bool>(window, "_isZoomInteracting")
                && IsFullPage(CurrentRegion(window), 4096, 3072) && !session.IsFullPageLoading(0));
            var full = CurrentRegion(window)!;
            Assert.NotSame(page.Bitmap, full.Bitmap);
            await gate.WaitAsync();
            held = true;
            BeginPan(canvas);
            window.PanImage(new Vector(-900, -600));
            canvas.ReleaseMouseCapture();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(full, CurrentRegion(window));
            Assert.False(IsDetailLoading(window));
            Assert.False(session.IsFullPageLoading(0));
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task PanDuringVisibleLoad_CachesResultAndAppliesImmediatelyOnRelease(bool releaseButton) => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            Task loading = StartZoomDetailLoad(window);
            var token = Field<CancellationTokenSource>(window, "_detailRegionCancellation").Token;
            var before = CurrentRegion(window);
            BeginPan(Canvas(window));
            window.PanImage(new Vector(-20, -10));
            Assert.False(token.IsCancellationRequested);
            // 画面内だけの結果を再利用できる位置へ戻してから読み込みを完了させる。
            window.PanImage(new Vector(20, 10));
            await loading;
            var cached = session.Details.Get(0, Viewport(window).VisibleSourceBounds, DisplayScale(window));
            Assert.NotNull(cached);
            Assert.Same(before, CurrentRegion(window));
            await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 50);
            Assert.Same(before, CurrentRegion(window));
            Assert.False(IsDetailLoading(window));
            Assert.False(session.IsFullPageLoading(0));
            await gate.WaitAsync();
            held = true;
            if (releaseButton)
                Canvas(window).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            else Canvas(window).ReleaseMouseCapture();
            Assert.Same(cached, CurrentRegion(window));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(IsDetailLoading(window));
            Assert.NotNull(Field<CancellationTokenSource?>(window, "_detailMarginCancellation"));
            Assert.False(session.IsFullPageLoading(0));
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
        }
    });

    [Fact]
    public Task FullPageCompletingDuringPan_IsCachedAndAppliedOnRelease() => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath();
        var window = CreateWindow();
        ImageLoadSession? session = null;
        try
        {
            SaveLargeImage(path);
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window);
            var first = CurrentRegion(window);
            StartFullPageDetails(window);
            // 同一ページの要求は共有し、全体デコードを二重実行しない。
            Task<ImageDetailRegion> loading = session.LoadFullPageAsync(Page(window), DisplayScale(window), null);
            Assert.Same(loading, session.LoadFullPageAsync(Page(window), DisplayScale(window), null));
            BeginPan(Canvas(window));
            window.PanImage(new Vector(-20, -10));
            var full = await loading;
            Assert.Same(first, CurrentRegion(window));
            window.PanImage(new Vector(-5, 0));
            Assert.Same(first, CurrentRegion(window));
            Canvas(window).ReleaseMouseCapture();
            Assert.Same(full, CurrentRegion(window));
        }
        finally { await CloseAsync(window, session); File.Delete(path); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Navigation_CancelsQueuedFullPageAndDoesNotApplyOldImage(bool switchFile) => RunOnStaAsync(async () =>
    {
        string path = TemporaryPath(), replacement = TemporaryPath();
        var window = CreateWindow();
        var gate = DecodeGate();
        bool held = false;
        ImageLoadSession? session = null;
        try
        {
            var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(4096, 3072, 31)));
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(1800, 1200, 179)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            ImageDetailRegionTests.Save(replacement, PreviewLoadingTests.CreateBitmap(80, 60, 179));
            await OpenWithoutAutomaticDetails(window, path);
            session = Session(window);
            await StartZoomDetailLoad(window);
            await gate.WaitAsync();
            held = true;
            StartFullPageDetails(window);
            Assert.True(session.IsFullPageLoading(0));
            Task? opening = null;
            if (switchFile) opening = window.OpenImageAsync(replacement);
            else ((Button)window.FindName("NextPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !session.IsFullPageLoading(0));
            gate.Release();
            held = false;
            if (opening is not null)
            {
                await opening;
                Assert.True(session.Token.IsCancellationRequested);
                Assert.Null(CurrentRegion(window));
                Assert.Null(((Image)window.FindName("DetailRegionImage")).Source);
                Assert.Equal(80, ((BitmapSource)((Image)window.FindName("DisplayedImage")).Source).PixelWidth);
            }
            else
            {
                await WaitForAsync(() => Page(window).Index == 1 && !IsDetailLoading(window));
                Assert.Same(Page(window).Bitmap, ((Image)window.FindName("DisplayedImage")).Source);
                Assert.True(CurrentRegion(window) is null or { PageIndex: 1 });
            }
        }
        finally
        {
            if (held) gate.Release();
            await CloseAsync(window, session);
            File.Delete(path);
            File.Delete(replacement);
        }
    });

    private static async Task OpenWithoutAutomaticDetails(MainWindow window, string path)
    {
        window.Show();
        await window.OpenImageAsync(path);
        Field<DispatcherTimer?>(window, "_detailLoadTimer")?.Stop();
    }

    private static Task StartZoomDetailLoad(MainWindow window, int zoomSteps = 8)
    {
        var canvas = Canvas(window);
        window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 120 * zoomSteps, ModifierKeys.None);
        Method(window, "ZoomInteractionTimer_Tick", null, EventArgs.Empty);
        Field<DispatcherTimer?>(window, "_detailLoadTimer")?.Stop();
        return (Task)Method(window, "LoadVisibleDetailsAsync", Session(window), Page(window))!;
    }

    private static void StartFullPageDetails(MainWindow window) => Method(window, "StartFullPageDetails", Session(window), Page(window));
    private static object? Method(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static ImageLoadSession Session(MainWindow window) => Field<ImageLoadSession>(window, "_session");
    private static ImagePage Page(MainWindow window) => Field<ImagePage>(window, "_currentPage");
    private static ImageViewport Viewport(MainWindow window) => Field<ImageViewport>(window, "_viewport");
    private static Canvas Canvas(MainWindow window) => (Canvas)window.FindName("ImageViewportCanvas");
    private static ImageDetailRegion? CurrentRegion(MainWindow window) => Field<ImageDetailRegion?>(window, "_currentDetailRegion");
    private static bool IsDetailLoading(MainWindow window) => Field<bool>(window, "_isDetailLoading");
    private static byte[] RenderCenter(Canvas canvas)
    {
        canvas.UpdateLayout();
        var render = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth),
            (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        render.Render(canvas);
        var pixel = new byte[4];
        render.CopyPixels(new Int32Rect((int)canvas.ActualWidth / 2, (int)canvas.ActualHeight / 2, 1, 1), pixel, 4, 0);
        return pixel;
    }
    private static SemaphoreSlim DecodeGate() => (SemaphoreSlim)typeof(ImageLoadSession)
        .GetField("DecodeGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static double DisplayScale(MainWindow window) => Viewport(window).Scale * VisualTreeHelper.GetDpi(window).DpiScaleX;
    private static bool IsWholePageAtCurrentResolution(MainWindow window) => CurrentRegion(window) is { } region
        && region.Bounds == Viewport(window).ImageBounds
        && region.Covers(Page(window).Index, Viewport(window).ImageBounds, DisplayScale(window));
    private static bool IsFullPage(ImageDetailRegion? region, int width, int height) => region is not null
        && region.Rectangle == new Int32Rect(0, 0, width, height)
        && region.Bitmap.PixelWidth == width && region.Bitmap.PixelHeight == height;
    private static void BeginPan(Canvas canvas)
    {
        canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Assert.True(canvas.IsMouseCaptured);
    }
    private static async Task CloseAsync(MainWindow window, ImageLoadSession? session)
    {
        window.Close();
        await WaitForAsync(() => !IsDetailLoading(window) && Field<CancellationTokenSource?>(window, "_detailMarginCancellation") is null
            && (session is null || !session.IsFullPageLoading(0) && !session.IsFullPageLoading(1)));
    }
    private static MainWindow CreateWindow() => new()
    {
        Width = 750, Height = 500, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
    };
    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"CSTViewer-staged-{Guid.NewGuid():N}.tif");
    private static void SaveLargeImage(string path) => ImageDetailRegionTests.Save(path,
        BitmapSource.Create(4096, 3072, 96, 96, PixelFormats.Gray8, null, new byte[4096 * 3072], 4096));
    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "制限時間内に高精細画像の更新が完了しませんでした。");
    }
    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
