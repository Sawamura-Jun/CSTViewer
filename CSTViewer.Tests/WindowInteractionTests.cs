using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class WindowInteractionTests
{
    [Fact]
    public Task ClosingLoadedWindow_ReleasesDisplayedImageAndSessionReferences() => RunOnStaAsync(async () =>
    {
        string path = SaveImage(2000, 1000, false);
        var window = new MainWindow(LogPath()) { ShowActivated = false };
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            var sessionField = typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var session = Assert.IsType<ImageLoadSession>(sessionField.GetValue(window));
            Assert.NotNull(((Image)window.FindName("DisplayedImage")).Source);

            window.Close();

            Assert.True(session.Token.IsCancellationRequested);
            Assert.Null(((Image)window.FindName("DisplayedImage")).Source);
            Assert.Null(sessionField.GetValue(window));
            foreach (string name in new[] { "_openingSession", "_currentPage", "_displaySource", "_interactionPreview", "_currentDetailBitmap" })
                Assert.Null(typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(1, BitmapScalingMode.HighQuality)]
    [InlineData(10, BitmapScalingMode.NearestNeighbor)]
    public Task Panning_PreservesInterpolationAndBitmapThroughPressDragAndRelease(int zoomSteps, BitmapScalingMode expectedMode)
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(4000, 2000, false);
            var window = new MainWindow(LogPath()) { ShowActivated = false, Width = 750, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var image = (Image)window.FindName("DisplayedImage");
                window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2),
                    zoomSteps * 120, ModifierKeys.None);
                var dpi = VisualTreeHelper.GetDpi(window);
                double displayScale = ((MatrixTransform)image.RenderTransform).Matrix.M11 * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
                int bitmapWidth = 4000 / (1 << BitmapMipmapCache.RequiredLevel(displayScale));
                // ズーム後の段階画像が表示されるまで待ち、パン前後の同じBitmapを比較する。
                await WaitForAsync(() => !IsZoomInteracting(window)
                    && ((BitmapSource)image.Source).PixelWidth == bitmapWidth
                    && RenderOptions.GetBitmapScalingMode(image) == expectedMode);
                var bitmap = image.Source;
                var before = ((MatrixTransform)image.RenderTransform).Matrix;
                canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                Assert.True(canvas.IsMouseCaptured);
                Assert.Equal(expectedMode, RenderOptions.GetBitmapScalingMode(image));
                Assert.Same(bitmap, image.Source);

                window.PanImage(new Vector(10, 10));
                var after = ((MatrixTransform)image.RenderTransform).Matrix;
                // ツールバーの折り返しで片方の軸が中央固定でも、拡大された軸は移動する。
                Assert.True(before.OffsetX != after.OffsetX || before.OffsetY != after.OffsetY);
                Assert.Equal(before.M11, after.M11);
                Assert.Equal(expectedMode, RenderOptions.GetBitmapScalingMode(image));
                Assert.Same(bitmap, image.Source);
                // 押したまま停止しても、通常の品質復帰タイマーを超えて同じ補間を保つ。
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Equal(expectedMode, RenderOptions.GetBitmapScalingMode(image));
                Assert.Same(bitmap, image.Source);
                canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                Assert.False(canvas.IsMouseCaptured);
                Assert.Equal(expectedMode, RenderOptions.GetBitmapScalingMode(image));
                await Task.Delay(ViewerSettings.DetailLoadDelayMilliseconds + 100);
                Assert.Equal(expectedMode, RenderOptions.GetBitmapScalingMode(image));
                Assert.Same(bitmap, image.Source);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public Task ZoomIdleDuringPanning_DoesNotKeepFastInterpolationBecauseMouseIsPressed()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(4000, 2000, false);
            var window = new MainWindow(LogPath()) { ShowActivated = false, Width = 750, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var image = (Image)window.FindName("DisplayedImage");
                // ズーム後の詳細表示に戻してから、押下中の次のズームを検証する。
                var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
                window.HandleViewportWheel(center, 120, ModifierKeys.None);
                await WaitForAsync(() => !IsZoomInteracting(window)
                    && RenderOptions.GetBitmapScalingMode(image) == BitmapScalingMode.HighQuality);
                await Task.Delay(ViewerSettings.DetailAfterZoomIdleDelayMilliseconds + 100);
                canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                window.HandleViewportWheel(center, 120, ModifierKeys.None);
                Assert.True(canvas.IsMouseCaptured);
                Assert.True(IsZoomInteracting(window));
                await WaitForAsync(() => !IsZoomInteracting(window));
                // ズームの終了後も左ボタンが押されているという理由で、低品質を継続しない。
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                window.PanImage(new Vector(10, 10));
                Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
                canvas.ReleaseMouseCapture();
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    private static bool IsZoomInteracting(MainWindow window) =>
        (bool)typeof(MainWindow).GetField("_isZoomInteracting", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task FitWhileZoomed_ResetsViewWithoutResizingWindow(bool shortcut) => RunOnStaAsync(async () =>
    {
        string path = SaveImage(2000, 1000, false);
        var window = new MainWindow(LogPath()) { ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            await window.OpenImageAsync(path);
            var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
            var original = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 480, ModifierKeys.None);
            window.PanImage(new Vector(-50, -25));

            RaiseFit(window, shortcut);

            Assert.True(window.SpreadViewState!.Value.IsFitMode);
            Assert.Equal(original, new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight));
            var image = (Image)window.FindName("DisplayedImage");
            var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
            Assert.Equal(Math.Min(canvas.ActualWidth / 2000, canvas.ActualHeight / 1000), matrix.M11, 6);
            Assert.Equal((canvas.ActualWidth - image.Width * matrix.M11) / 2, matrix.OffsetX, 6);
            Assert.Equal((canvas.ActualHeight - image.Height * matrix.M11) / 2, matrix.OffsetY, 6);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(2000, 1000, false)]
    [InlineData(1000, 2000, true)]
    public Task FitWhileAlreadyFitted_ShrinksWindowAndKeepsScaleAndCenter(int width, int height, bool shortcut)
        => RunOnStaAsync(async () =>
    {
        string path = SaveImage(width, height, false);
        var window = new MainWindow(LogPath())
        {
            ShowActivated = false, MinWidth = ViewerSettings.SpreadMinimumWindowWidth,
            MinHeight = ViewerSettings.MinimumWindowHeight, Width = 1200, Height = 800
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            await window.OpenImageAsync(path);
            var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
            var image = (Image)window.FindName("DisplayedImage");
            double scale = ((MatrixTransform)image.RenderTransform).Matrix.M11;
            Point center = WindowScreen.Center(window);
            Rect workArea = WindowScreen.WorkArea(window);
            var original = new Size(window.ActualWidth, window.ActualHeight);
            Assert.Equal("Fit(F)", ((Button)window.FindName("FitImageButton")).Content);

            RaiseFit(window, shortcut);
            window.UpdateLayout();
            // Dispatcherの次のレイアウトでも、サイズ変更による再フィットが起きないことを確認する。
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(window.SpreadViewState!.Value.IsFitMode);
            Assert.True(window.ActualWidth < original.Width || window.ActualHeight < original.Height);
            var matrix = ((MatrixTransform)image.RenderTransform).Matrix;
            Assert.Equal(scale, matrix.M11, 6);
            Assert.Equal((canvas.ActualWidth - width * scale) / 2, matrix.OffsetX, 6);
            Assert.Equal((canvas.ActualHeight - height * scale) / 2, matrix.OffsetY, 6);
            double expectedLeft = Math.Clamp(center.X - window.ActualWidth / 2, workArea.Left,
                Math.Max(workArea.Left, workArea.Right - window.ActualWidth));
            double expectedTop = Math.Clamp(center.Y - window.ActualHeight / 2, workArea.Top,
                Math.Max(workArea.Top, workArea.Bottom - window.ActualHeight));
            // 高DPI環境ではウィンドウの実座標が物理ピクセルへ丸められる。
            Assert.InRange(Math.Abs(expectedLeft - window.Left), 0, 1);
            Assert.InRange(Math.Abs(expectedTop - window.Top), 0, 1);

            var fitted = new Size(window.ActualWidth, window.ActualHeight);
            RaiseFit(window, shortcut);
            Assert.Equal(fitted, new Size(window.ActualWidth, window.ActualHeight));
            Assert.Equal(scale, ((MatrixTransform)image.RenderTransform).Matrix.M11, 6);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task FitWhileMaximized_RestoresNormalWindowAndKeepsDisplayedScale() => RunOnStaAsync(async () =>
    {
        string path = SaveImage(2000, 1000, false);
        var window = new MainWindow(LogPath()) { ShowActivated = false };
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();
            var image = (Image)window.FindName("DisplayedImage");
            double scale = ((MatrixTransform)image.RenderTransform).Matrix.M11;

            RaiseFit(window, false);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.True(window.SpreadViewState!.Value.IsFitMode);
            Assert.Equal(scale, ((MatrixTransform)image.RenderTransform).Matrix.M11, 6);
            var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
            Assert.True(canvas.ActualWidth >= image.Width * scale - 1);
            Assert.True(canvas.ActualHeight >= image.Height * scale - 1);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task FitWithoutImage_LeavesWindowUnchanged() => RunOnStaAsync(() =>
    {
        var window = new MainWindow(LogPath()) { ShowActivated = false };
        try
        {
            window.Show();
            var original = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            RaiseFit(window, true);
            Assert.Equal(original, new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight));
            Assert.Null(window.SpreadViewState);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static void RaiseFit(MainWindow window, bool shortcut)
    {
        if (!shortcut)
        {
            ((Button)window.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return;
        }
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
            Environment.TickCount, ViewerSettings.FitShortcutKey) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(e);
        Assert.True(e.Handled);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(predicate());
    }

    [Theory]
    [InlineData(2000, 1000, false)]
    [InlineData(1000, 2000, true)]
    public Task StartupImage_ResizesBeforeShowingAndFitsFirstPage(int width, int height, bool tiff)
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(width, height, tiff);
            var window = new MainWindow(LogPath());
            try
            {
                Assert.Equal(new Size(width, height), await Task.Run(() => ImageLoader.ReadSourceSize(path)));
                await window.PrepareStartupImageAsync(path);
                Assert.False(window.IsVisible);
                Rect workArea = WindowScreen.WorkArea(window);
                Assert.True(workArea.Contains(new Rect(window.Left, window.Top, window.Width, window.Height)));
                window.ShowActivated = false;
                window.Show();
                window.UpdateLayout();
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var target = WindowSizing.StartupViewportSize(new Size(width, height));
                // 最小サイズや画面上限に当たらない辺では、表示領域がPython版の目標寸法になる。
                double chromeWidth = window.Width - canvas.ActualWidth;
                double expectedWidth = Math.Max(target.Width, window.MinWidth - chromeWidth);
                if (width >= height && window.Width < workArea.Width) Assert.InRange(canvas.ActualWidth, expectedWidth - 2, expectedWidth + 2);
                if (height > width && window.Height < workArea.Height) Assert.InRange(canvas.ActualHeight, target.Height - 2, target.Height + 2);
                await window.OpenImageAsync(path);
                var image = (Image)window.FindName("DisplayedImage");
                Assert.NotNull(image.Source);
                Assert.Equal(width, image.Width);
                Assert.Equal(height, image.Height);
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
    public Task StartupProbeFailure_PreservesDefaultSize()
    {
        return RunOnStaAsync(async () =>
        {
            var window = new MainWindow(LogPath());
            try
            {
                await window.PrepareStartupImageAsync(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.tif"));
                Assert.Equal(ViewerSettings.WindowWidth, window.Width);
                Assert.Equal(ViewerSettings.WindowHeight, window.Height);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public Task CtrlWheel_ResizesFitImageAndKeepsZoomedImageCenter()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(2000, 1000, false);
            var window = new MainWindow(LogPath()) { ShowActivated = false, Width = 750, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await window.OpenImageAsync(path);
                var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
                var image = (Image)window.FindName("DisplayedImage");
                double oldWidth = window.Width;
                window.HandleViewportWheel(new Point(), 120, ModifierKeys.Control);
                window.UpdateLayout();
                Assert.True(window.Width > oldWidth);
                Assert.Contains("(Fit)", ((TextBlock)window.FindName("ZoomStatusText")).Text);
                double fitScale = ((MatrixTransform)image.RenderTransform).Matrix.M11;
                Assert.Equal(Math.Min(canvas.ActualWidth / 2000, canvas.ActualHeight / 1000), fitScale, 6);

                var center = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
                window.HandleViewportWheel(center, 480, ModifierKeys.None);
                var before = ((MatrixTransform)image.RenderTransform).Matrix;
                var inverse = before;
                inverse.Invert();
                Point imageCenter = inverse.Transform(center);
                oldWidth = window.Width;
                window.HandleViewportWheel(center, -120, ModifierKeys.Control | ModifierKeys.Shift);
                window.UpdateLayout();
                Assert.True(window.Width < oldWidth);
                var after = ((MatrixTransform)image.RenderTransform).Matrix;
                Assert.Equal(before.M11, after.M11, 6);
                after.Invert();
                var newImageCenter = after.Transform(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2));
                Assert.Equal(imageCenter.X, newImageCenter.X, 6);
                Assert.Equal(imageCenter.Y, newImageCenter.Y, 6);
            }
            finally
            {
                window.Close();
                File.Delete(path);
            }
        });
    }

    [Fact]
    public Task DoubleClick_TogglesMaximizeAndCtrlWheelDoesNotAlterRestoreSize()
    {
        return RunOnStaAsync(() =>
        {
            var window = new MainWindow(LogPath()) { ShowActivated = false };
            try
            {
                window.Show();
                window.UpdateLayout();
                var original = new Rect(window.Left, window.Top, window.Width, window.Height);
                var canvas = (UIElement)window.FindName("ImageViewportCanvas");
                RaiseMouseDown(canvas, MouseButton.Left, 1);
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.True(RaiseMouseDown(canvas, MouseButton.Left, 2).Handled);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                window.HandleViewportWheel(new Point(), -120, ModifierKeys.Control);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                RaiseMouseDown(canvas, MouseButton.Left, 2);
                window.UpdateLayout();
                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Equal(original, new Rect(window.Left, window.Top, window.Width, window.Height));
                Assert.False(canvas.IsMouseCaptured);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task MiddleClick_ClosesDuringLoadingAndCancelsOpeningSession()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(2000, 1000, true);
            var window = new MainWindow(LogPath()) { ShowActivated = false };
            try
            {
                window.Show();
                Task opening = window.OpenImageAsync(path);
                var session = (ImageLoadSession)typeof(MainWindow).GetField("_openingSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.NotNull(session);
                Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("LoadingOverlay")).Visibility);
                Assert.Empty(((TextBlock)window.FindName("ImageLoadingText")).Text);
                var border = (FrameworkElement)window.FindName("ImageViewportBorder");
                Assert.Equal(Cursors.Wait, border.Cursor);
                Assert.True(RaiseMouseDown(border, MouseButton.Middle, 1).Handled);
                Assert.False(window.IsVisible);
                Assert.True(session.Token.IsCancellationRequested);
                await opening;
            }
            finally
            {
                if (window.IsVisible) window.Close();
                File.Delete(path);
            }
        });
    }

    private static MouseButtonEventArgs RaiseMouseDown(UIElement target, MouseButton button, int clicks)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        { RoutedEvent = UIElement.PreviewMouseDownEvent };
        // 実際の入力でWPFが設定するクリック回数を、テスト内でだけ再現する。
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e, clicks);
        target.RaiseEvent(e);
        return e;
    }

    private static string LogPath() => Path.Combine(Path.GetTempPath(), $"CSTViewer-window-{Guid.NewGuid()}.jsonl");

    private static string SaveImage(int width, int height, bool tiff)
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-window-{Guid.NewGuid()}.{(tiff ? "tif" : "png")}");
        var pixels = new byte[width * height];
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        BitmapEncoder encoder = tiff ? new TiffBitmapEncoder { Compression = TiffCompressOption.Zip } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // WPFのイベントとレイアウトをSTAスレッドのDispatcher上で検証する。
            var dispatcher = Dispatcher.CurrentDispatcher;
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
