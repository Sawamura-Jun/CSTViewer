using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class SpreadTests
{
    [Fact]
    public void ViewportSync_UsesRelativeZoomAndCenterForDifferentImageSizes()
    {
        var source = Viewport(new Size(2000, 1000), new Size(800, 400));
        var target = Viewport(new Size(4000, 2000), new Size(600, 300));
        source.Zoom(new Point(400, 200), 480);
        source.Pan(new Vector(-100, -50));

        Assert.True(target.ApplySyncState(source.GetSyncState()!.Value));
        AssertState(source.GetSyncState()!.Value, target.GetSyncState()!.Value);
        Assert.NotEqual(source.Scale, target.Scale);
        source.Fit();
        target.ApplySyncState(source.GetSyncState()!.Value);
        Assert.True(target.IsFitMode);
        Assert.Equal(0.15, target.Scale, 8);
    }

    [Fact]
    public void ViewportSync_HandlesMissingImagesDifferentAspectRatiosAndScaleLimits()
    {
        Assert.Null(new ImageViewport().GetSyncState());
        Assert.False(new ImageViewport().ApplySyncState(new(false, 2, new Point(.5, .5))));
        var target = Viewport(new Size(1000, 4000), new Size(800, 600));
        target.ApplySyncState(new(false, 10000, new Point(1, 0)));
        Assert.Equal(ViewerSettings.MaximumZoomScale, target.Scale);
        Assert.True(double.IsFinite(target.Offset.X));
        Assert.True(target.Offset.X <= 0);
        Assert.Equal(0, target.Offset.Y);
        target.ApplySyncState(new(true, 10000, new Point(1, 0)));
        Assert.True(target.IsFitMode);
        Assert.Equal(new Point(325, 0), target.Offset);
    }

    [Fact]
    public Task SingleWindow_ShowsReasonAndNewWindowCanBePaired() => RunOnStaAsync(() =>
    {
        var source = Window(new SpreadController());
        MainWindow? other = null;
        try
        {
            source.Show();
            source.ToggleSpread();
            Assert.False(SpreadButton(source).IsChecked);
            Assert.Equal("Sync mode requires two windows on the same monitor",
                ((TextBlock)source.FindName("PreloadStatusText")).Text);
            other = source.CreateNewWindow();
            source.ToggleSpread();
            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(other).IsChecked);
            Assert.Null(other.SpreadViewState);
        }
        finally { other?.Close(); source.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Layout_SelectsNearestVisibleWindowAndKeepsLeftRightOrder() => RunOnStaAsync(() =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var near = Window(controller);
        var far = Window(controller);
        var hidden = Window(controller);
        try
        {
            Rect area = WindowScreen.WorkArea(source);
            ShowAt(near, area, .05);
            ShowAt(source, area, .25);
            ShowAt(far, area, .65);
            ShowAt(hidden, area, .26);
            hidden.WindowState = WindowState.Minimized;
            source.ToggleSpread();
            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(near).IsChecked);
            Assert.False(SpreadButton(far).IsChecked);
            Assert.False(SpreadButton(hidden).IsChecked);
            Assert.Equal(area.Left, near.Left, 4);
            Assert.Equal(near.Left + near.Width, source.Left, 4);
            Assert.Equal(area.Right, source.Left + source.Width, 4);
            Assert.Equal(area.Height, near.Height, 4);
            Assert.Equal(area.Height, source.Height, 4);
            Assert.True(source.MinWidth <= source.Width);

            far.ToggleSpread();
            Assert.True(SpreadButton(far).IsChecked);
            Assert.False(SpreadButton(near).IsChecked);
        }
        finally { hidden.Close(); far.Close(); near.Close(); source.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ZoomPanFitAndToggle_SynchronizeBothWaysWithoutChangingPanQuality() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            await source.OpenImageAsync(SaveImage(directory, "left.tif", 2000, 1000));
            await target.OpenImageAsync(SaveImage(directory, "right.tif", 4000, 2000));
            source.ToggleSpread();
            source.HandleViewportWheel(Center(source), 480, ModifierKeys.None);
            await WaitForAsync(() => Matches(source, target));
            Assert.NotEqual(Matrix(source).M11, Matrix(target).M11);

            // ズーム停止後のパン同期では、相手側のBitmapと補間品質を保つ。
            await Task.Delay(ViewerSettings.ZoomInteractionIdleMilliseconds + ViewerSettings.DetailLoadDelayMilliseconds + 200);
            var image = (Image)target.FindName("DisplayedImage");
            var bitmap = image.Source;
            var quality = RenderOptions.GetBitmapScalingMode(image);
            var canvas = (UIElement)source.FindName("ImageViewportCanvas");
            canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            source.PanImage(new Vector(-30, -20));
            await WaitForAsync(() => Matches(source, target));
            Assert.Same(bitmap, image.Source);
            Assert.Equal(quality, RenderOptions.GetBitmapScalingMode(image));
            canvas.ReleaseMouseCapture();

            target.HandleViewportWheel(Center(target), 120, ModifierKeys.None);
            await WaitForAsync(() => Matches(source, target));
            source.ToggleSpread();
            Assert.False(SpreadButton(target).IsChecked);
            var before = target.SpreadViewState;
            source.HandleViewportWheel(Center(source), 120, ModifierKeys.None);
            await Task.Delay(ViewerSettings.SpreadSyncIntervalMilliseconds * 4);
            Assert.Equal(before, target.SpreadViewState);
            source.ToggleSpread();
            AssertState(source.SpreadViewState!.Value, target.SpreadViewState!.Value);
            ((Button)target.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(source.SpreadViewState!.Value.IsFitMode);
            Assert.True(target.SpreadViewState!.Value.IsFitMode);

            source.HandleViewportWheel(Center(source), 120, ModifierKeys.None);
            source.ToggleSpread();
            await Task.Delay(ViewerSettings.SpreadSyncIntervalMilliseconds * 4);
            Assert.True(target.SpreadViewState!.Value.IsFitMode);
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task FitInFitMode_ResizesBothWindowsAndKeepsEachImageScale() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            await source.OpenImageAsync(SaveImage(directory, "left.tif", 2000, 1000));
            await target.OpenImageAsync(SaveImage(directory, "right.tif", 1000, 4000));
            source.ToggleSpread();
            Rect area = WindowScreen.WorkArea(source);
            // 同期の初回配置ではFitコマンドの余白調整を実行せず、左右半分の高さを保つ。
            Assert.Equal(area.Height, source.ActualHeight, 4);
            Assert.Equal(area.Height, target.ActualHeight, 4);
            double sourceScale = Matrix(source).M11;
            double targetScale = Matrix(target).M11;
            double sourceHeight = source.ActualHeight;

            ((Button)source.FindName("FitImageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await source.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(target).IsChecked);
            Assert.True(source.SpreadViewState!.Value.IsFitMode);
            Assert.True(target.SpreadViewState!.Value.IsFitMode);
            Assert.Equal(sourceScale, Matrix(source).M11, 6);
            Assert.Equal(targetScale, Matrix(target).M11, 6);
            Assert.True(source.ActualHeight < sourceHeight);
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task PageAndFileNavigation_AdvancesAvailableSideAndPreventsRepeatDuringLoading() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            string first = SaveImage(directory, "01.tif", 100, 50, 3);
            SaveImage(directory, "02.tif", 100, 50, 3);
            string last = SaveImage(directory, "03.tif", 100, 50);
            source.Show();
            target.Show();
            await source.OpenImageAsync(last);
            await target.OpenImageAsync(first);
            await WaitForAsync(() => ((Button)target.FindName("NextFileButton")).IsEnabled);
            source.ToggleSpread();
            Assert.True(((Button)source.FindName("NextPageButton")).IsEnabled);
            await source.ChangePageAsync(1);
            Assert.Contains("— Page 1 / 1", source.Title);
            Assert.Contains("— Page 2 / 3", target.Title);
            await source.ChangePageAsync(1);
            Assert.Contains("— Page 3 / 3", target.Title);
            Assert.False(((Button)source.FindName("NextPageButton")).IsEnabled);
            Assert.True(((Button)source.FindName("NextFileButton")).IsEnabled);

            Task changing = source.ChangeFileAsync(1);
            Assert.False(((Button)source.FindName("NextFileButton")).IsEnabled);
            Assert.False(((Button)target.FindName("PreviousPageButton")).IsEnabled);
            await target.ChangeFileAsync(1);
            await changing;
            Assert.StartsWith("03.tif", source.Title);
            Assert.StartsWith("02.tif", target.Title);
            Assert.Contains("— Page 1 / 3", target.Title);
            await source.ChangeFileAsync(-1);
            Assert.StartsWith("02.tif", source.Title);
            Assert.StartsWith("01.tif", target.Title);
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Close_ClearsPendingSyncAndMiddleClickClosesEnabledPair() => RunOnStaAsync(async () =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        var replacement = Window(controller);
        try
        {
            source.Show();
            target.Show();
            source.ToggleSpread();
            target.Close();
            Assert.True(source.IsVisible);
            Assert.False(SpreadButton(source).IsChecked);
            replacement.Show();
            source.ToggleSpread();
            var canvas = (UIElement)source.FindName("ImageViewportCanvas");
            canvas.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Assert.False(source.IsVisible);
            Assert.False(replacement.IsVisible);
            await Task.Delay(ViewerSettings.SpreadSyncIntervalMilliseconds * 2);
        }
        finally { source.Close(); target.Close(); replacement.Close(); }
    });

    [Fact]
    public Task NarrowLayout_ContainsAllToolbarControlsAndTextboxDoesNotTriggerSpread() => RunOnStaAsync(() =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            source.ToggleSpread();
            source.Width = ViewerSettings.MinimumWindowWidth;
            source.UpdateLayout();
            var normalToolbar = (WrapPanel)source.FindName("MainToolbar");
            foreach (FrameworkElement control in normalToolbar.Children)
            {
                // Save追加後も、通常の最小幅では必要な分だけ折り返して全操作を表示する。
                Rect bounds = control.TransformToAncestor(normalToolbar).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= normalToolbar.ActualWidth + 1);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= normalToolbar.ActualHeight + 1);
            }
            source.Width = ViewerSettings.SpreadMinimumWindowWidth;
            source.UpdateLayout();
            var toolbar = (FrameworkElement)source.FindName("MainToolbar");
            foreach (string name in new[] { "SaveImageButton", "NewWindowButton", "SpreadLayoutButton", "DiffImageButton", "NextFileButton", "CropSavePathTextBox" })
            {
                var control = (FrameworkElement)source.FindName(name);
                Rect bounds = control.TransformToAncestor(toolbar).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= toolbar.ActualHeight + 1);
            }
            var textbox = (TextBox)source.FindName("CropSavePathTextBox");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, Key.S)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = textbox };
            source.RaiseEvent(args);
            Assert.False(args.Handled);
            Assert.True(SpreadButton(source).IsChecked);
        }
        finally { source.Close(); target.Close(); }
        return Task.CompletedTask;
    });

    private static ImageViewport Viewport(Size image, Size canvas)
    {
        var viewport = new ImageViewport();
        viewport.Resize(canvas);
        viewport.SetImage(image);
        return viewport;
    }

    private static MainWindow Window(SpreadController controller) => new(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-spread-{Guid.NewGuid():N}.jsonl"), spreadController: controller)
    { ShowActivated = false };

    private static void ShowAt(MainWindow window, Rect area, double position)
    {
        window.MinWidth = 300;
        window.Width = 300;
        window.Height = 400;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = area.Left + area.Width * position;
        window.Top = area.Top;
        window.Show();
        window.UpdateLayout();
    }

    private static ToggleButton SpreadButton(MainWindow window) => (ToggleButton)window.FindName("SpreadLayoutButton");
    private static Matrix Matrix(MainWindow window) => ((MatrixTransform)((Image)window.FindName("DisplayedImage")).RenderTransform).Matrix;
    private static Point Center(MainWindow window)
    {
        var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
        return new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
    }

    private static bool Matches(MainWindow source, MainWindow target)
    {
        var left = source.SpreadViewState!.Value;
        var right = target.SpreadViewState!.Value;
        return Math.Abs(left.ZoomRatio - right.ZoomRatio) < 1e-6
            && (left.Center - right.Center).Length < 1e-6;
    }

    private static void AssertState(ViewportSyncState expected, ViewportSyncState actual)
    {
        Assert.Equal(expected.IsFitMode, actual.IsFitMode);
        Assert.Equal(expected.ZoomRatio, actual.ZoomRatio, 6);
        Assert.Equal(expected.Center.X, actual.Center.X, 6);
        Assert.Equal(expected.Center.Y, actual.Center.Y, 6);
    }

    private static string CreateDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-spread-{Guid.NewGuid():N}")).FullName;

    private static string SaveImage(string directory, string name, int width, int height, int pages = 1)
    {
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        for (int index = 0; index < pages; index++)
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(width, height, (byte)(31 + index))));
        string path = Path.Combine(directory, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(predicate());
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // WPFの入力・タイマー・非同期読み込みを同じSTA Dispatcher上で検証する。
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
