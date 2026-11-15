using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class DropInteractionTests
{
    [Fact]
    public Task MultipageTiff_LoadsInitialAndPreloadedPagesAtDetailResolution()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-pages-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = new MainWindow(logPath)
            {
                Width = 750, Height = 650,
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            try
            {
                var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(4000, 2000, 31)));
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(2000, 1000, 179)));
                using (var stream = File.Create(path)) encoder.Save(stream);
                // HWNDを作成して画面DPIを確定させ、実際の表示サイズで縮小する。
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var firstPreview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var dpi = VisualTreeHelper.GetDpi(window);
                double fitWidth = Math.Min(canvas.ActualWidth, canvas.ActualHeight * 2);
                int expectedWidth = (int)Math.Round(fitWidth * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY) * 1.8);
                Assert.InRange(firstPreview.PixelWidth, expectedWidth - 1, expectedWidth + 1);
                Assert.Equal(4000, image.Width);
                Assert.Equal(2000, image.Height);
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                // 全体表示の解像度が足りていれば、余分な詳細デコードを開始しない。
                Assert.Same(firstPreview, image.Source);
                Assert.Null(((Image)window.FindName("DetailRegionImage")).Source);
                Assert.Equal($"{Path.GetFileName(path)} — Page 1 / 2 — {4000:N0} × {2000:N0} px — {ViewerSettings.ApplicationTitle}", window.Title);
                var next = (Button)window.FindName("NextPageButton");
                Assert.True(next.IsEnabled);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal($"{Path.GetFileName(path)} — Page 2 / 2 — {2000:N0} × {1000:N0} px — {ViewerSettings.ApplicationTitle}", window.Title);
                Assert.Equal(2000, image.Width);
                Assert.Equal(1000, image.Height);
                Assert.Equal(Math.Min(2000, firstPreview.PixelWidth), ((BitmapSource)image.Source).PixelWidth);
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Null(((Image)window.FindName("DetailRegionImage")).Source);
                Assert.False(next.IsEnabled);
                ((Button)window.FindName("PreviousPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("— Page 1 / 2", window.Title);
                var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, 120 * 12) { RoutedEvent = UIElement.MouseWheelEvent };
                canvas.RaiseEvent(wheel);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                var before = ((System.Windows.Media.MatrixTransform)image.RenderTransform).Matrix;
                var regionImage = (Image)window.FindName("DetailRegionImage");
                await WaitForAsync(() => regionImage.Source is not null);
                Assert.Same(firstPreview, image.Source);
                Assert.Equal(regionImage.Width, ((BitmapSource)regionImage.Source).PixelWidth);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(regionImage));
                // 領域画像を重ねても、元画像の座標とパン・ズーム位置は変えない。
                Assert.Equal(before, ((System.Windows.Media.MatrixTransform)image.RenderTransform).Matrix);
                Assert.Equal(4000, image.Width);
                ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // 全体表示に戻ると、先に読み込んだページ全体画像を使う。
                Assert.Same(firstPreview, image.Source);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task PreviewZoom_UpdatesScalingModeForDecodedOrCachedDetailRegion()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-scaling-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = new MainWindow(logPath)
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            try
            {
                var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                var frame = BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(4000, 2000, 31));
                encoder.Frames.Add(frame);
                encoder.Frames.Add(frame);
                using (var stream = File.Create(path)) encoder.Save(stream);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var regionImage = (Image)window.FindName("DetailRegionImage");
                var detailField = typeof(MainWindow).GetField("_currentDetailRegion", BindingFlags.Instance | BindingFlags.NonPublic)!;
                BitmapSource? firstDetail = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var preview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    canvas.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        { RoutedEvent = UIElement.MouseWheelEvent });
                    // 操作中は全体画像を軽い縮小補間で表示する。
                    Assert.Equal(BitmapScalingMode.LowQuality, RenderOptions.GetBitmapScalingMode(image));
                    await WaitForAsync(() => regionImage.Source is not null
                        && detailField.GetValue(window) is ImageDetailRegion { Rectangle: { X: 0, Y: 0, Width: 4000, Height: 2000 } }
                        && RenderOptions.GetBitmapScalingMode(regionImage) == BitmapScalingMode.HighQuality);
                    var detail = Assert.IsAssignableFrom<BitmapSource>(regionImage.Source);
                    Assert.Same(preview, image.Source);
                    Assert.True(detail.PixelWidth / regionImage.Width > preview.PixelWidth / image.Width);
                    // 高精細領域は縮小描画になるため、領域側の補間方式を更新する。
                    Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));
                    // 領域画像から全体画像への更新を待ってから、再訪時のキャッシュ再利用を比較する。
                    if (attempt == 0)
                    {
                        firstDetail = detail;
                        ((Button)window.FindName("NextPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.Null(regionImage.Source);
                        ((Button)window.FindName("PreviousPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else Assert.Same(firstDetail, detail);
                }
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task RepeatedZoom_UsesInitialImageByDefaultAndRefinesAfterInteraction()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-repeat-zoom-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            try
            {
                SaveZoomTestImage(path);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var preview = (BitmapSource)image.Source;
                var regionImage = (Image)window.FindName("DetailRegionImage");
                RaiseWheel(canvas, 1);
                await WaitForAsync(() => regionImage.Source is not null);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));

                RaiseWheel(canvas, 1);
                Assert.Same(preview, image.Source);
                Assert.Equal(BitmapScalingMode.LowQuality, RenderOptions.GetBitmapScalingMode(image));
                await Task.Delay(ViewerSettings.ZoomInteractionIdleMilliseconds * 2 / 3);
                RaiseWheel(canvas, 1);
                await Task.Delay(ViewerSettings.ZoomInteractionIdleMilliseconds * 2 / 3);
                // 既定では連続操作中にページ全体画像を表示し、高精細領域は停止後に戻す。
                Assert.Same(preview, image.Source);
                Assert.Null(regionImage.Source);
                Assert.Equal(Visibility.Collapsed, regionImage.Visibility);
                Assert.Null(image.Clip);
                // 停止後は新しい倍率に合う高精細画像を表示し、縮小補間を維持する。
                await WaitForAsync(() => regionImage.Source is not null
                    && regionImage.Visibility == Visibility.Visible
                    && RenderOptions.GetBitmapScalingMode(regionImage) == BitmapScalingMode.HighQuality);
                Assert.Same(preview, image.Source);
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task ZoomOut_ReusesCompletedFullPageAcrossZoomLevels()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-reduce-zoom-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            // ツールバーの最小幅に依存せず、1段拡大後も段階画像が必要な倍率にする。
            window.Height = ViewerSettings.MinimumWindowHeight;
            try
            {
                SaveZoomTestImage(path);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var preview = (BitmapSource)image.Source;
                var regionImage = (Image)window.FindName("DetailRegionImage");
                RaiseWheel(canvas, 8);
                var detailField = typeof(MainWindow).GetField("_currentDetailRegion", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var zoomField = typeof(MainWindow).GetField("_isZoomInteracting", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await WaitForAsync(() => detailField.GetValue(window) is ImageDetailRegion detail
                    && detail.Bitmap.PixelWidth == 4000 && detail.Bitmap.PixelHeight == 2000);
                var fullDetail = (ImageDetailRegion)detailField.GetValue(window)!;

                RaiseWheel(canvas, -7);
                var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
                // 完成した全体画像を保ち、表示倍率に合う段階画像だけを切り替える。
                Assert.Same(preview, image.Source);
                var dpi = VisualTreeHelper.GetDpi(window);
                double displayScale = matrix.M11 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
                int level = BitmapMipmapCache.RequiredLevel(displayScale);
                var session = (ImageLoadSession)typeof(MainWindow).GetField("_session",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                // 停止判定の直後は全体画像が残るため、対象倍率の段階画像に切り替わるまで待つ。
                await WaitForAsync(() => !(bool)zoomField.GetValue(window)!
                    && session.Mipmaps.Get(fullDetail.Bitmap, level, fullDetail.PageIndex) is { } cached
                    && ReferenceEquals(cached, regionImage.Source)
                    && RenderOptions.GetBitmapScalingMode(regionImage) == BitmapScalingMode.HighQuality);
                Assert.Same(fullDetail, detailField.GetValue(window));
                var reduced = (BitmapSource)regionImage.Source;
                Assert.True(reduced.IsFrozen);
                Assert.True(reduced.PixelWidth >= regionImage.Width * Math.Min(1, displayScale) - 1);
                Assert.Equal(matrix, ((MatrixTransform)image.RenderTransform).Matrix);
                Assert.Equal(4000, image.Width);
                Assert.Equal(2000, image.Height);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));

                RaiseWheel(canvas, 7);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => !(bool)zoomField.GetValue(window)!);
                Assert.Same(fullDetail, detailField.GetValue(window));
                RaiseWheel(canvas, -7);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => !(bool)zoomField.GetValue(window)! && ReferenceEquals(reduced, regionImage.Source));
                Assert.Same(fullDetail, detailField.GetValue(window));
                ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => regionImage.Source is null
                    && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
                // 詳細画像の読み込みやキャッシュ再利用でも、ログを作成しない。
                Assert.False(File.Exists(logPath));
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task PngZoom_RestoresQualityAndOriginalPixelsAfterDisplayReduction()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-png-zoom-{Guid.NewGuid()}.png");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            try
            {
                SaveZoomTestImage(path);
                // 元Bitmapへの復帰と縮小を確認できるよう、全体表示の倍率を十分小さくする。
                window.Height = ViewerSettings.MinimumWindowHeight;
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var original = (BitmapSource)image.Source;
                await WaitForAsync(() => ((BitmapSource)image.Source).PixelWidth < original.PixelWidth);
                var preview = image.Source;
                RaiseWheel(canvas, 8);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => ReferenceEquals(original, image.Source));
                RaiseWheel(canvas, -7);
                Assert.Same(preview, image.Source);
                // 同じ段階で足りる時は、プレビューと停止後の表示Bitmapが同じでもよい。
                await WaitForAsync(() => RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality
                    && !ReferenceEquals(original, image.Source));
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                RaiseWheel(canvas, 7);
                await WaitForAsync(() => ReferenceEquals(original, image.Source));
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    private static MainWindow CreateOffscreenWindow(string logPath) => new(logPath)
    {
        // 横方向でフィットさせ、最小プレビュー寸法の余裕で拡大が吸収されない画面比率にする。
        Width = ViewerSettings.MinimumWindowWidth, Height = 600,
        ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000, Top = -10000
    };

    [Fact]
    public Task ContinuousZoomOut_RestoresInitialDetailAtFit()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-continuous-zoom-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            try
            {
                SaveZoomTestImage(path);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var preview = image.Source;
                var regionImage = (Image)window.FindName("DetailRegionImage");
                RaiseWheel(canvas, 8);
                await WaitForAsync(() => regionImage.Source is not null);
                // 合計220msを超える連続入力でも、全体プレビューを差し替えない。
                for (int step = 0; step < 8; step++)
                {
                    RaiseWheel(canvas, -1);
                    Assert.Same(preview, image.Source);
                    await Task.Delay(40);
                    Assert.Same(preview, image.Source);
                }
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => regionImage.Source is null
                    && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality);
                Assert.Equal(4000, image.Width);
                Assert.Equal(2000, image.Height);
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task Panning_KeepsScalingQualityUntilMouseCaptureEnds()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-pan-quality-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            try
            {
                SaveZoomTestImage(path);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var preview = image.Source;
                var regionImage = (Image)window.FindName("DetailRegionImage");
                RaiseWheel(canvas, 1);
                await WaitForAsync(() => regionImage.Source is not null);
                Assert.Same(preview, image.Source);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));
                canvas.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.True(canvas.IsMouseCaptured);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));
                // 表示領域外でキャプチャが解除されても、補間品質を維持する。
                canvas.ReleaseMouseCapture();
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(regionImage));
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task FitDuringDetailDecode_DoesNotApplyStaleViewport()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-stale-zoom-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = CreateOffscreenWindow(logPath);
            var gate = (SemaphoreSlim)typeof(ImageLoadSession).GetField("DecodeGate",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            bool held = false;
            try
            {
                SaveZoomTestImage(path);
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var preview = image.Source;
                // デコード待ちを意図的に作り、その間に倍率を戻す競合を再現する。
                await gate.WaitAsync();
                held = true;
                RaiseWheel(canvas, 1);
                var busy = typeof(MainWindow).GetField("_isDetailLoading", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await WaitForAsync(() => (bool)busy.GetValue(window)!);
                ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
                gate.Release();
                held = false;
                await WaitForAsync(() => !(bool)busy.GetValue(window)!);
                Assert.Same(preview, image.Source);
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Equal(matrix, ((MatrixTransform)image.RenderTransform).Matrix);
            }
            finally
            {
                if (held) gate.Release();
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    private static void SaveZoomTestImage(string path)
    {
        BitmapEncoder encoder = ImageLoader.IsTiff(path)
            ? new TiffBitmapEncoder { Compression = TiffCompressOption.Zip } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(4000, 2000, 71)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RaiseWheel(FrameworkElement canvas, int steps) =>
        canvas.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120 * steps)
            { RoutedEvent = UIElement.MouseWheelEvent });

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "制限時間内に画像の更新が完了しませんでした。");
    }

    [Fact]
    public Task SwitchingFileDuringPreload_DiscardsOldSessionResults()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-switch-{Guid.NewGuid()}.tif");
            string replacement = Path.ChangeExtension(path, ".png");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = new MainWindow(logPath);
            try
            {
                var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                var frame = BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(320, 240, 31));
                for (int i = 0; i < 80; i++) encoder.Frames.Add(frame);
                using (var stream = File.Create(path)) encoder.Save(stream);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(80, 60, 179)));
                using (var stream = File.Create(replacement)) png.Save(stream);
                Task loading = window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (image.Source is null && DateTime.UtcNow < deadline) await Task.Delay(1);
                var oldSession = (ImageLoadSession)typeof(MainWindow).GetField("_session",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.True(oldSession.IsPreloading);
                await window.OpenImageAsync(replacement);
                await loading;
                Assert.True(oldSession.Token.IsCancellationRequested);
                Assert.Equal(80, image.Width);
                Assert.Equal($"{Path.GetFileName(replacement)} — Page 1 / 1 — 80 × 60 px — {ViewerSettings.ApplicationTitle}", window.Title);
                Assert.False(((Button)window.FindName("NextPageButton")).IsEnabled);
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                File.Delete(replacement);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task ClosingDuringFirstPageLoad_DoesNotApplyImageToClosedWindow()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-close-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = new MainWindow(logPath);
            try
            {
                var encoder = new TiffBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(80, 60, 0)));
                using (var stream = File.Create(path)) encoder.Save(stream);
                Task loading = window.OpenImageAsync(path);
                window.Close();
                await loading;
                Assert.Null(((Image)window.FindName("DisplayedImage")).Source);
            }
            finally { await CloseAndWaitForLoadsAsync(window); File.Delete(path); await DeleteLogAsync(logPath); }
        });
    }

    [Fact]
    public Task DropEvent_LoadsImageWithoutPerformanceLogging()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-render-{Guid.NewGuid()}.tiff");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            // 画面外で表示し、実際のドロップ操作でも計測とログが始まらないことを確認する。
            var window = new MainWindow(logPath)
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            try
            {
                var image = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Rgb24,
                    null, new byte[80 * 60 * 3], 80 * 3);
                var encoder = new TiffBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }
                window.Show();
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                window.RaiseEvent(CreateDragEvent(window, data, DragDrop.PreviewDropEvent));

                var displayedImage = (Image)window.FindName("DisplayedImage");
                await WaitForAsync(() => displayedImage.Source is not null
                    && ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility == Visibility.Collapsed);
                var loadedImage = Assert.IsAssignableFrom<BitmapSource>(displayedImage.Source);
                Assert.Equal(80, loadedImage.PixelWidth);
                Assert.Equal(60, loadedImage.PixelHeight);
                Assert.Null(typeof(MainWindow).GetField("_pendingRenderMeasurement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Null(typeof(MainWindow).GetField("_renderMeasurementTimeout",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.False(File.Exists(logPath));

                // ウィンドウを閉じる際にもログを作成しない。
                window.Close();
                Assert.Null(typeof(MainWindow).GetField("_pendingRenderMeasurement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Null(typeof(MainWindow).GetField("_renderMeasurementTimeout",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.False(File.Exists(logPath));
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task DropEvent_LoadsImageAndEnablesFitButton()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-drop-{Guid.NewGuid()}.tiff");
            var window = new MainWindow();
            try
            {
                var image = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Rgb24,
                    null, new byte[80 * 60 * 3], 80 * 3);
                var encoder = new TiffBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                window.Measure(new Size(1000, 780));
                window.Arrange(new Rect(0, 0, 1000, 780));
                window.UpdateLayout();
                var data = new DataObject(DataFormats.FileDrop, new[] { path });
                DragEventArgs dragOver = CreateDragEvent(window, data, DragDrop.PreviewDragOverEvent);
                window.RaiseEvent(dragOver);
                Assert.True(dragOver.Handled);
                Assert.Equal(DragDropEffects.Copy, dragOver.Effects);

                DragEventArgs drop = CreateDragEvent(window, data, DragDrop.PreviewDropEvent);
                window.RaiseEvent(drop);
                Assert.True(drop.Handled);
                Assert.Equal(DragDropEffects.Copy, drop.Effects);

                var displayedImage = (Image)window.FindName("DisplayedImage");
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                // Dispatcherを動かしたまま待ち、非同期読み込み後のUI更新まで検証する。
                while (displayedImage.Source is null && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(10);
                }

                var loadedImage = Assert.IsAssignableFrom<BitmapSource>(displayedImage.Source);
                Assert.Equal(80, loadedImage.PixelWidth);
                Assert.Equal(60, loadedImage.PixelHeight);
                Assert.Equal(80, displayedImage.Width);
                Assert.Equal(60, displayedImage.Height);
                Assert.True(((Button)window.FindName("FitImageButton")).IsEnabled);
                Assert.True(((Button)window.FindName("OpenImageButton")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("EmptyImageHint")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
            }
            finally
            {
                await CloseAndWaitForLoadsAsync(window);
                File.Delete(path);
            }
        });
    }

    [Fact]
    public Task DragOverEvent_RejectsUnsupportedFiles()
    {
        return RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { "notes.txt" });
                DragEventArgs dragOver = CreateDragEvent(window, data, DragDrop.PreviewDragOverEvent);

                window.RaiseEvent(dragOver);

                Assert.True(dragOver.Handled);
                Assert.Equal(DragDropEffects.None, dragOver.Effects);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("DropImageOverlay")).Visibility);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }

    private static DragEventArgs CreateDragEvent(MainWindow window, IDataObject data, RoutedEvent routedEvent)
    {
        // WPFが内部生成するイベントを再現するため、非公開コンストラクターをテスト内だけで使う。
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.None, DragDropEffects.Copy, window, new Point()], null)!;
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static async Task CloseAndWaitForLoadsAsync(MainWindow window)
    {
        var sessions = new[] { "_session", "_openingSession" }
            .Select(name => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window))
            .OfType<ImageLoadSession>().Distinct().ToArray();
        window.Close();
        // Dispatcherを終了する前に、キャンセルされた読み込みがデコード枠を解放するまで待つ。
        await WaitForAsync(() => !(bool)typeof(MainWindow).GetField("_isDetailLoading", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!
            && typeof(MainWindow).GetField("_detailMarginCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null
            && sessions.All(session => !session.IsPreloading
                && Enumerable.Range(0, session.PageCount).All(index => !session.IsFullPageLoading(index))));
    }

    private static async Task DeleteLogAsync(string logPath)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                File.Delete(logPath);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // 非同期ログの追記中は、ファイルが閉じられるまで待ってから削除する。
                await Task.Delay(20);
            }
        }
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // WPFの画面とイベントはSTAスレッドとDispatcher上で扱う必要がある。
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
