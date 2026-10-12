using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class DropInteractionTests
{
    [Fact]
    public Task MultipagePreview_NavigatesWithOriginalCoordinatesAndRefinesAfterZoom()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-pages-{Guid.NewGuid()}.tif");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            var window = new MainWindow(logPath)
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            try
            {
                var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(2000, 1000, 31)));
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(600, 1200, 179)));
                using (var stream = File.Create(path)) encoder.Save(stream);
                // HWNDを作成して画面DPIを確定させ、実際の表示サイズで縮小する。
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                var firstPreview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                Assert.True(firstPreview.PixelWidth < 2000);
                Assert.Equal(2000, image.Width);
                Assert.Equal(1000, image.Height);
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                // 全体表示の解像度が足りていれば、余分な詳細デコードを開始しない。
                Assert.Same(firstPreview, image.Source);
                Assert.Equal($"{Path.GetFileName(path)} — Page 1 / 2 — {2000:N0} × {1000:N0} px — CSTViewer v1.0.0", window.Title);
                var next = (Button)window.FindName("NextPageButton");
                Assert.True(next.IsEnabled);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal($"{Path.GetFileName(path)} — Page 2 / 2 — {600:N0} × {1200:N0} px — CSTViewer v1.0.0", window.Title);
                Assert.Equal(600, image.Width);
                Assert.Equal(1200, image.Height);
                Assert.False(next.IsEnabled);
                ((Button)window.FindName("PreviousPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains("— Page 1 / 2", window.Title);
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, 120 * 12) { RoutedEvent = UIElement.MouseWheelEvent };
                canvas.RaiseEvent(wheel);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                var before = ((System.Windows.Media.MatrixTransform)image.RenderTransform).Matrix;
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (((BitmapSource)image.Source).PixelWidth <= firstPreview.PixelWidth && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                Assert.True(((BitmapSource)image.Source).PixelWidth > firstPreview.PixelWidth);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                // Bitmapを差し替えても、元画像の座標とパン・ズーム位置は変えない。
                Assert.Equal(before, ((System.Windows.Media.MatrixTransform)image.RenderTransform).Matrix);
                Assert.Equal(2000, image.Width);
                ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // 全体表示に戻っても、キャッシュ済みの詳細画像を適切な倍率で表示できる。
                Assert.True(((BitmapSource)image.Source).PixelWidth >= firstPreview.PixelWidth);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
            }
            finally
            {
                window.Close();
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task PreviewZoom_UpdatesScalingModeWhenDecodedOrCachedDetailReplacesPreview()
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
                BitmapSource? firstDetail = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var preview = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    canvas.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                        System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, 120)
                        { RoutedEvent = UIElement.MouseWheelEvent });
                    // 元画像に対して縮小中でも、プレビューの画素を拡大する間は輪郭を保つ。
                    Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (ReferenceEquals(preview, image.Source) && DateTime.UtcNow < deadline)
                        await Task.Delay(20);
                    var detail = Assert.IsAssignableFrom<BitmapSource>(image.Source);
                    Assert.True(detail.PixelWidth > preview.PixelWidth);
                    // 詳細画像の差し替え後は縮小描画になるため、補間方式も更新する。
                    Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                    if (attempt == 0)
                    {
                        firstDetail = detail;
                        ((Button)window.FindName("NextPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        ((Button)window.FindName("PreviousPageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else Assert.Same(firstDetail, detail);
                }
            }
            finally
            {
                window.Close();
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task RepeatedZoom_KeepsPreviewUntilLastWheelEventSettles()
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
                RaiseWheel(canvas, 1);
                await WaitForAsync(() => !ReferenceEquals(preview, image.Source));
                var detail = image.Source;
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));

                RaiseWheel(canvas, 1);
                Assert.Same(preview, image.Source);
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                await Task.Delay(ViewerSettings.ZoomInteractionIdleMilliseconds * 2 / 3);
                RaiseWheel(canvas, 1);
                await Task.Delay(ViewerSettings.ZoomInteractionIdleMilliseconds * 2 / 3);
                // 最初の入力から220msを過ぎても、連続操作中はプレビューを維持する。
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => ReferenceEquals(detail, image.Source));
                Assert.Equal(BitmapScalingMode.NearestNeighbor, RenderOptions.GetBitmapScalingMode(image));
                Assert.Same(detail, image.Source);
            }
            finally
            {
                window.Close();
                File.Delete(path);
                await DeleteLogAsync(logPath);
            }
        });
    }

    [Fact]
    public Task ZoomOut_ReducesIntermediateResolutionAndReusesCachedDetail()
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
                RaiseWheel(canvas, 8);
                await WaitForAsync(() => ((BitmapSource)image.Source).PixelWidth == 4000);
                var fullDetail = (BitmapSource)image.Source;

                RaiseWheel(canvas, -7);
                var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
                // 最初の縮小入力で、解像度が不足していてもプレビューへ戻す。
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => !ReferenceEquals(preview, image.Source)
                    && ((BitmapSource)image.Source).PixelWidth < fullDetail.PixelWidth);
                var reduced = (BitmapSource)image.Source;
                Assert.True(reduced.PixelWidth > preview.PixelWidth);
                Assert.True(reduced.IsFrozen);
                var dpi = VisualTreeHelper.GetDpi(window);
                double displayScale = matrix.M11 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
                int level = BitmapMipmapCache.RequiredLevel(displayScale);
                Assert.Equal(4000 / (1 << level), reduced.PixelWidth);
                Assert.Equal(matrix, ((MatrixTransform)image.RenderTransform).Matrix);
                Assert.Equal(4000, image.Width);
                Assert.Equal(2000, image.Height);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));

                RaiseWheel(canvas, 7);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => ReferenceEquals(fullDetail, image.Source));
                RaiseWheel(canvas, -7);
                Assert.Same(preview, image.Source);
                await WaitForAsync(() => ReferenceEquals(reduced, image.Source));
                ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(((BitmapSource)image.Source).PixelWidth <= reduced.PixelWidth);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
                // 再拡大はキャッシュを使い、縮小用Bitmapを作るためのTIFF再読込も行わない。
                await WaitForAsync(() => File.Exists(logPath)
                    && File.ReadAllLines(logPath).Any(line => line.Contains("PageDetail")));
                Assert.Single(File.ReadAllLines(logPath), line => line.Contains("PageDetail"));
            }
            finally
            {
                window.Close();
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
                window.Close();
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
    public Task ContinuousZoomOut_KeepsPreviewThroughAllWheelSteps()
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
                RaiseWheel(canvas, 8);
                await WaitForAsync(() => ((BitmapSource)image.Source).PixelWidth == 4000);
                // 合計220msを超える連続入力でも、途中で詳細画像へ戻さない。
                for (int step = 0; step < 8; step++)
                {
                    RaiseWheel(canvas, -1);
                    Assert.Same(preview, image.Source);
                    await Task.Delay(40);
                    Assert.Same(preview, image.Source);
                }
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
                await WaitForAsync(() => !ReferenceEquals(preview, image.Source));
                Assert.True(((BitmapSource)image.Source).PixelWidth <= 2000);
                Assert.Equal(4000, image.Width);
                Assert.Equal(2000, image.Height);
            }
            finally
            {
                window.Close();
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
                RaiseWheel(canvas, 1);
                await WaitForAsync(() => !ReferenceEquals(preview, image.Source));
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                canvas.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.True(canvas.IsMouseCaptured);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                // 表示領域外でキャプチャが解除されても、補間品質を維持する。
                canvas.ReleaseMouseCapture();
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
            }
            finally
            {
                window.Close();
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
                window.Close();
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
                Assert.Equal($"{Path.GetFileName(replacement)} — Page 1 / 1 — 80 × 60 px — CSTViewer v1.0.0", window.Title);
                Assert.False(((Button)window.FindName("NextPageButton")).IsEnabled);
            }
            finally
            {
                window.Close();
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
            finally { window.Close(); File.Delete(path); await DeleteLogAsync(logPath); }
        });
    }

    [Fact]
    public Task DropEvent_RecordsFirstRenderTimingAndDetachesMeasurement()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-render-{Guid.NewGuid()}.tiff");
            string logPath = Path.ChangeExtension(path, ".jsonl");
            // 画面外で表示し、ユーザーの作業を妨げずにWPFの実際の描画通知を検証する。
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

                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                string[] lines = [];
                while (DateTime.UtcNow < deadline && !lines.Any(IsFirstRenderEntry))
                {
                    await Task.Delay(20);
                    try
                    {
                        if (File.Exists(logPath))
                        {
                            lines = await File.ReadAllLinesAsync(logPath);
                        }
                    }
                    catch (IOException)
                    {
                        // 非同期の追記中なら、次の待機で読み直す。
                    }
                }

                using JsonDocument json = JsonDocument.Parse(Assert.Single(lines, IsFirstRenderEntry));
                JsonElement entry = json.RootElement;
                Assert.Equal("FirstRenderCallback", entry.GetProperty("Outcome").GetString());
                Assert.Equal(path, entry.GetProperty("FilePath").GetString());
                Assert.True(entry.GetProperty("TotalMs").GetDouble() >= entry.GetProperty("ImageReadyMs").GetDouble());
                Assert.True(entry.GetProperty("StagesMs").GetProperty("FirstRenderWait").GetDouble() >= 0);
                Assert.True(entry.GetProperty("StagesMs").GetProperty("UiApply").GetDouble() >= 0);

                // 閉じる時に二重記録せず、静的イベントとタイマーの参照も解除する。
                window.Close();
                Assert.Null(typeof(MainWindow).GetField("_pendingRenderMeasurement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Null(typeof(MainWindow).GetField("_renderMeasurementTimeout",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Single(await File.ReadAllLinesAsync(logPath), IsFirstRenderEntry);
            }
            finally
            {
                window.Close();
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
                window.Close();
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

    private static bool IsFirstRenderEntry(string line)
    {
        using var json = JsonDocument.Parse(line);
        return json.RootElement.GetProperty("Outcome").GetString() == "FirstRenderCallback";
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
