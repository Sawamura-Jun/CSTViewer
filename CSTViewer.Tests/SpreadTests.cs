using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
    public void ViewportSync_HandlesMissingImagesDifferentAspectRatiosAndZoomLimit()
    {
        Assert.Null(new ImageViewport().GetSyncState());
        Assert.False(new ImageViewport().ApplySyncState(new(false, 2, new Point(.5, .5))));
        var target = Viewport(new Size(1000, 4000), new Size(800, 600));
        target.ApplySyncState(new(false, 10000, new Point(1, 0)));
        Assert.Equal(ViewerSettings.MaximumZoomScale, target.Scale);
        Assert.True(double.IsFinite(target.Offset.X));
        Assert.True(target.Offset.X <= 0);
        Assert.Equal(0, target.Offset.Y);
        Assert.False(target.ApplySyncState(new(false, double.PositiveInfinity, new Point(1, 0))));
        Assert.Equal(ViewerSettings.MaximumZoomScale, target.Scale);
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
            AssertSpreadLayout(near, source);
            Assert.True(source.MinWidth <= source.Width);

            far.ToggleSpread();
            Assert.True(SpreadButton(far).IsChecked);
            Assert.False(SpreadButton(near).IsChecked);
        }
        finally { hidden.Close(); far.Close(); near.Close(); source.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Layout_ReenablingWithoutDraggingKeepsOriginalRestoreSizes() => RunOnStaAsync(() =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            source.Width = 800;
            source.Height = 600;
            target.Width = 900;
            target.Height = 700;
            source.UpdateLayout();
            target.UpdateLayout();
            source.ToggleSpread();
            source.ToggleSpread();
            source.ToggleSpread();
            DragSpreadWindow(source);
            DragSpreadWindow(target);
            Assert.Equal(800, source.ActualWidth, 3);
            Assert.Equal(600, source.ActualHeight, 3);
            Assert.Equal(900, target.ActualWidth, 3);
            Assert.Equal(700, target.ActualHeight, 3);
        }
        finally { source.Close(); target.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Layout_ReenablingArrangesSamePairAndAllowsSizeRestoration(bool enableFromPartner) => RunOnStaAsync(() =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        var third = Window(controller);
        try
        {
            Rect area = WindowScreen.WorkArea(source);
            ShowAt(source, area, .1);
            ShowAt(target, area, .55);
            var sourceSize = new Size(source.ActualWidth, source.ActualHeight);
            var targetSize = new Size(target.ActualWidth, target.ActualHeight);
            FitWheelZoomButton(source).IsChecked = true;
            FitWheelZoomButton(target).IsChecked = true;
            source.ToggleSpread();
            Assert.False(FitWheelZoomButton(source).IsChecked);
            Assert.False(FitWheelZoomButton(target).IsChecked);
            source.ToggleSpread();
            Assert.False(SpreadButton(source).IsChecked);
            Assert.False(SpreadButton(target).IsChecked);
            DragSpreadWindow(source);
            DragSpreadWindow(target);
            // OFF中に左右を入れ替え、近くに別の画面があっても元のペアを使う。
            source.Left = area.Left + area.Width * .6;
            target.Left = area.Left + area.Width * .1;
            source.Top = target.Top = area.Top + 50;
            ShowAt(third, area, enableFromPartner ? .11 : .61);
            FitWheelZoomButton(source).IsChecked = true;
            FitWheelZoomButton(target).IsChecked = true;
            FitWheelZoomButton(third).IsChecked = true;
            (enableFromPartner ? target : source).ToggleSpread();

            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(target).IsChecked);
            Assert.False(SpreadButton(third).IsChecked);
            Assert.False(FitWheelZoomButton(source).IsChecked);
            Assert.False(FitWheelZoomButton(target).IsChecked);
            Assert.True(FitWheelZoomButton(third).IsChecked);
            AssertSpreadLayout(source, target);

            DragSpreadWindow(source);
            DragSpreadWindow(target);
            Assert.Equal(sourceSize.Width, source.ActualWidth, 3);
            Assert.Equal(sourceSize.Height, source.ActualHeight, 3);
            Assert.Equal(targetSize.Width, target.ActualWidth, 3);
            Assert.Equal(targetSize.Height, target.ActualHeight, 3);
        }
        finally { source.Close(); target.Close(); third.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task LayoutDrag_RestoresEachOriginalSizeAndKeepsImageSync() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            await source.OpenImageAsync(SaveImage(directory, "left.tif", 1000, 600));
            await target.OpenImageAsync(SaveImage(directory, "right.tif", 1000, 600));
            source.Width = 800;
            source.Height = 600;
            target.Width = 900;
            target.Height = 700;
            source.UpdateLayout();
            target.UpdateLayout();
            var originalSourceSize = new Size(source.ActualWidth, source.ActualHeight);
            var originalTargetSize = new Size(target.ActualWidth, target.ActualHeight);
            double originalMinimumWidth = source.MinWidth;
            source.ToggleSpread();
            var targetBounds = new Rect(target.Left, target.Top, target.ActualWidth, target.ActualHeight);
            source.ToggleSpread();
            source.ToggleSpread();

            DragSpreadWindow(source);
            Assert.Equal(originalSourceSize.Width, source.ActualWidth, 3);
            Assert.Equal(originalSourceSize.Height, source.ActualHeight, 3);
            Assert.Equal(originalMinimumWidth, source.MinWidth);
            Assert.Equal(targetBounds, new Rect(target.Left, target.Top, target.ActualWidth, target.ActualHeight));
            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(target).IsChecked);
            source.HandleViewportWheel(Center(source), 120, ModifierKeys.None);
            await WaitForAsync(() => Matches(source, target));

            DragSpreadWindow(target);
            Assert.Equal(originalTargetSize.Width, target.ActualWidth, 3);
            Assert.Equal(originalTargetSize.Height, target.ActualHeight, 3);
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task LayoutFromMaximized_RestoresNormalSizeAndMinimizationKeepsSnapshot() => RunOnStaAsync(() =>
    {
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            source.Width = 800;
            source.Height = 600;
            source.UpdateLayout();
            var originalSize = new Size(source.ActualWidth, source.ActualHeight);
            source.WindowState = WindowState.Maximized;
            source.UpdateLayout();
            source.ToggleSpread();
            if (source.Left < target.Left) AssertSpreadLayout(source, target);
            else AssertSpreadLayout(target, source);
            source.WindowState = WindowState.Minimized;
            source.WindowState = WindowState.Normal;
            source.UpdateLayout();
            DragSpreadWindow(source);
            Assert.Equal(WindowState.Normal, source.WindowState);
            Assert.Equal(originalSize.Width, source.ActualWidth, 3);
            Assert.Equal(originalSize.Height, source.ActualHeight, 3);
        }
        finally { source.Close(); target.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("wheel")]
    [InlineData("fit")]
    [InlineData("border")]
    [InlineData("maximize")]
    public Task LayoutSizeChange_DiscardsOldRestoreSize(string operation) => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            await source.OpenImageAsync(SaveImage(directory, "left.tif", 1000, 600));
            source.ToggleSpread();
            switch (operation)
            {
                case "wheel": source.HandleViewportWheel(Center(source), -120, ModifierKeys.Control); break;
                case "fit": source.FitImageLocally(); break;
                case "border":
                    SendMessage(new WindowInteropHelper(source).Handle, 0x0214, IntPtr.Zero, IntPtr.Zero);
                    source.Width -= 20;
                    break;
                case "maximize":
                    source.WindowState = WindowState.Maximized;
                    source.WindowState = WindowState.Normal;
                    break;
            }
            source.UpdateLayout();
            var resized = new Size(source.ActualWidth, source.ActualHeight);
            DragSpreadWindow(source);
            Assert.Equal(resized.Width, source.ActualWidth, 3);
            Assert.Equal(resized.Height, source.ActualHeight, 3);
            Assert.True(SpreadButton(source).IsChecked);
            Assert.True(SpreadButton(target).IsChecked);
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
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
            Assert.Equal(area.Height, WindowScreen.FromPixels(source, VisibleFrame(source)).Height, 4);
            Assert.Equal(area.Height, WindowScreen.FromPixels(target, VisibleFrame(target)).Height, 4);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Rotation_SynchronizesButtonsAndShortcutsBothWaysAndKeepsEachPagesAngle(bool shortcut) => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        var unrelated = Window(controller);
        try
        {
            string leftPath = SaveImage(directory, "left.tif", 800, 400, 2);
            string rightPath = SaveImage(directory, "right.tif", 600, 900, 2);
            byte[] leftBytes = File.ReadAllBytes(leftPath), rightBytes = File.ReadAllBytes(rightPath);
            source.Show();
            target.Show();
            await source.OpenImageAsync(leftPath);
            await target.OpenImageAsync(rightPath);
            target.RotateCurrentPage(-1);
            source.ToggleSpread();
            unrelated.Show();
            await unrelated.OpenImageAsync(leftPath);
            Assert.True(SpreadButton(source).IsChecked);

            // 直前のズーム同期を取り消し、左右に一度ずつ同じ90度操作を適用する。
            target.HandleViewportWheel(Center(target), 480, ModifierKeys.None);
            Rotate(source, 1, shortcut);
            Assert.Equal(1, Rotation(source));
            Assert.Equal(0, Rotation(target));
            Assert.Equal(0, Rotation(unrelated));
            await Task.Delay(ViewerSettings.SpreadSyncIntervalMilliseconds * 4);
            Assert.True(source.SpreadViewState!.Value.IsFitMode);
            Assert.True(target.SpreadViewState!.Value.IsFitMode);
            Assert.Equal(1, Rotation(source));
            Assert.Equal(0, Rotation(target));
            Rotate(target, -1, shortcut);
            Assert.Equal(0, Rotation(source));
            Assert.Equal(3, Rotation(target));

            source.ToggleSpread();
            Rotate(source, 1, shortcut);
            Assert.Equal(1, Rotation(source));
            Assert.Equal(3, Rotation(target));
            source.ToggleSpread();
            Assert.Equal(1, Rotation(source));
            Assert.Equal(3, Rotation(target));
            await source.ChangePageAsync(1);
            Assert.Equal(0, Rotation(source));
            Assert.Equal(0, Rotation(target));
            Rotate(target, -1, shortcut);
            Assert.Equal(3, Rotation(source));
            Assert.Equal(3, Rotation(target));
            await target.ChangePageAsync(-1);
            Assert.Equal(1, Rotation(source));
            Assert.Equal(3, Rotation(target));
            Assert.Equal(leftBytes, File.ReadAllBytes(leftPath));
            Assert.Equal(rightBytes, File.ReadAllBytes(rightPath));
        }
        finally { source.Close(); target.Close(); unrelated.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Rotation_FromEmptyWindowRotatesAvailablePartnerAndBothEmptyWindowsDisableButtons() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            source.Show();
            target.Show();
            source.ToggleSpread();
            Assert.False(((Button)source.FindName("RotateRightButton")).IsEnabled);
            Assert.False(((Button)target.FindName("RotateLeftButton")).IsEnabled);
            await target.OpenImageAsync(SaveImage(directory, "right.tif", 800, 400));
            Assert.True(((Button)source.FindName("RotateRightButton")).IsEnabled);
            Rotate(source, 1, shortcut: false);
            Assert.Null(source.SpreadViewState);
            Assert.Equal(1, Rotation(target));
            Rotate(source, -1, shortcut: true);
            Assert.Equal(0, Rotation(target));
        }
        finally { source.Close(); target.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Rotation_WhilePartnerSavesDisablesBothSidesAndDoesNotPartiallyRotate() => RunOnStaAsync(async () =>
    {
        string directory = CreateDirectory();
        var controller = new SpreadController();
        var source = Window(controller);
        var target = Window(controller);
        try
        {
            string path = SaveImage(directory, "input.tif", 800, 400);
            source.Show();
            target.Show();
            await source.OpenImageAsync(path);
            await target.OpenImageAsync(path);
            source.ToggleSpread();
            Rect image = Rect.Transform(new Rect(0, 0, 800, 400), Matrix(target));
            Assert.True(target.BeginCropSelection(new Point(image.Left + image.Width / 4, image.Top + image.Height / 4)));
            target.CompleteCropDrag(new Point(image.Left + image.Width * 3 / 4, image.Top + image.Height * 3 / 4));
            Task<bool> saving = target.SaveCropAsync(Path.Combine(directory, "crop.tif"));
            foreach (var window in new[] { source, target })
            {
                Assert.False(((Button)window.FindName("RotateRightButton")).IsEnabled);
                Assert.False(((Button)window.FindName("RotateLeftButton")).IsEnabled);
            }
            source.RotateCurrentPage(1);
            Assert.Equal(0, Rotation(source));
            Assert.Equal(0, Rotation(target));
            Assert.True(await saving);
            Assert.True(((Button)source.FindName("RotateRightButton")).IsEnabled);
            source.RotateCurrentPage(1);
            Assert.Equal(1, Rotation(source));
            Assert.Equal(1, Rotation(target));
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
    public Task NarrowLayout_ContainsAllToolbarControls() => RunOnStaAsync(() =>
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
            foreach (string name in new[] { "SaveImageButton", "NewWindowButton", "SpreadLayoutButton", "DiffImageButton", "NextFileButton", "CropSaveDestinationButton" })
            {
                var control = (FrameworkElement)source.FindName(name);
                Rect bounds = control.TransformToAncestor(toolbar).TransformBounds(new Rect(control.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= toolbar.ActualWidth + 1);
                Assert.True(bounds.Top >= 0 && bounds.Bottom <= toolbar.ActualHeight + 1);
            }
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
    private static ToggleButton FitWheelZoomButton(MainWindow window) => (ToggleButton)window.FindName("FitWheelZoomButton");

    private static void AssertSpreadLayout(MainWindow left, MainWindow right)
    {
        Rect area = WindowScreen.WorkAreaPixels(left);
        Rect leftFrame = VisibleFrame(left);
        Rect rightFrame = VisibleFrame(right);
        double halfWidth = Math.Floor(area.Width / 2);
        // WPFの外寸ではなく、DWMが表示する枠が中央・画面端で一致することを確認する。
        Assert.Equal(new Rect(area.Left, area.Top, halfWidth, area.Height), leftFrame);
        Assert.Equal(new Rect(area.Left + halfWidth, area.Top, area.Width - halfWidth, area.Height), rightFrame);
        Assert.Equal(leftFrame.Right, rightFrame.Left);
    }

    private static Rect VisibleFrame(MainWindow window)
    {
        Assert.Equal(0, DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 9,
            out var frame, Marshal.SizeOf<NativeRect>()));
        return new Rect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    private static void DragSpreadWindow(MainWindow window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Assert.True(GetWindowRect(handle, out var bounds));
        Assert.True(GetCursorPos(out var cursor));
        bool handled = false;
        // OSの移動ループ自体は起動せず、タイトルバー押下だけをフックへ通知する。
        long packed = (ushort)cursor.X | ((long)(ushort)cursor.Y << 16);
        window.SpreadWindowHook(handle, 0x00A1, new IntPtr(2), new IntPtr(packed), ref handled);
        bounds.Left += 40;
        bounds.Right += 40;
        bounds.Top += 40;
        bounds.Bottom += 40;
        IntPtr rectangle = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
        try
        {
            Marshal.StructureToPtr(bounds, rectangle, false);
            // WM_MOVINGは実際のHWNDへ送り、登録したフックとWPFのサイズ更新を検証する。
            SendMessage(handle, 0x0216, IntPtr.Zero, rectangle);
            SendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            window.UpdateLayout();
        }
        finally { Marshal.FreeHGlobal(rectangle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out NativeRect bounds, int size);
    private static Matrix Matrix(MainWindow window) => ((MatrixTransform)((Image)window.FindName("DisplayedImage")).RenderTransform).Matrix;
    private static int Rotation(MainWindow window)
    {
        Matrix matrix = Matrix(window);
        return matrix.M12 > 0 ? 1 : matrix.M11 < 0 ? 2 : matrix.M12 < 0 ? 3 : 0;
    }

    private static void Rotate(MainWindow window, int step, bool shortcut)
    {
        if (shortcut)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount,
                step > 0 ? Key.R : Key.L) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(args);
            Assert.True(args.Handled);
        }
        else ((Button)window.FindName(step > 0 ? "RotateRightButton" : "RotateLeftButton"))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
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
