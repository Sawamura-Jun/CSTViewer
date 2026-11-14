using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class RotationViewportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Rotation_FitsSwappedDimensionsAndInvertsZoomPanAndSelection(int rotation)
    {
        var viewport = new ImageViewport();
        viewport.Resize(new Size(800, 600));
        viewport.SetImage(new Size(2000, 1000), rotation);
        Assert.Equal(rotation % 2 == 0 ? new Size(2000, 1000) : new Size(1000, 2000), viewport.DisplayImageSize);
        Assert.Equal(rotation % 2 == 0 ? 0.4 : 0.3, viewport.Scale, 8);
        AssertRect(viewport.ImageBounds, viewport.VisibleSourceBounds);
        viewport.Zoom(new Point(400, 300), 480);
        Point anchor = new(440, 330);
        Point source = viewport.ViewToImage(anchor);
        viewport.Zoom(anchor, 120);
        AssertPoint(source, viewport.ViewToImage(anchor));
        viewport.Pan(new Vector(-20, 15));
        Assert.True(viewport.ImageBounds.Contains(viewport.VisibleSourceBounds));
        var request = ImageDetailRequest.Create(viewport, viewport.Scale);
        Assert.True(new Rect(request.Rectangle.X, request.Rectangle.Y, request.Rectangle.Width, request.Rectangle.Height)
            .Contains(viewport.VisibleSourceBounds));
        var selection = new ImageCropSelection();
        Rect visible = viewport.VisibleImageBounds;
        Point start = new(visible.Left + visible.Width * 0.3, visible.Top + visible.Height * 0.3);
        Point end = new(visible.Left + visible.Width * 0.7, visible.Top + visible.Height * 0.7);
        Assert.True(selection.BeginSelection(start, viewport));
        selection.End(end, viewport);
        Rect selected = viewport.ImageToView(selection.ImageRectangle);
        AssertRect(new Rect(start, end), selected);
        Point leftEdge = new(selected.Left, selected.Top + selected.Height / 2);
        Assert.True(selection.BeginAdjustment(leftEdge, viewport));
        selection.End(leftEdge + new Vector(15, 0), viewport);
        // 回転後も、画面上でつかんだ左辺だけが動く。
        Rect adjusted = viewport.ImageToView(selection.ImageRectangle);
        Assert.Equal(selected.Left + 15, adjusted.Left, 7);
        Assert.Equal(selected.Right, adjusted.Right, 7);
        Assert.Equal(selected.Top, adjusted.Top, 7);
        Assert.Equal(selected.Bottom, adjusted.Bottom, 7);
        var target = new ImageViewport();
        target.Resize(new Size(800, 600));
        target.SetImage(new Size(2000, 1000), rotation);
        Assert.True(target.ApplySyncState(viewport.GetSyncState()!.Value));
        AssertPoint(viewport.GetSyncState()!.Value.Center, target.GetSyncState()!.Value.Center);
    }

    [Fact]
    public void SessionRotation_IsIndependentOfPreviewEvictionAndOtherPagesAndSessions()
    {
        var first = new ImageLoadSession("first.tif", new Size(512, 512));
        var second = new ImageLoadSession("second.tif", new Size(512, 512));
        try
        {
            Assert.Equal(1, first.RotatePage(0, 1));
            Assert.Equal(3, first.RotatePage(1, -1));
            Assert.Equal(0, first.GetPageRotation(2));
            Assert.Equal(0, second.GetPageRotation(0));
            for (int index = 0; index < ViewerSettings.PreviewCachePageLimit + 2; index++)
                first.Previews.Add(new ImagePage(index, 100, 1, 1, PreviewLoadingTests.CreateBitmap(1, 1, 32)));
            Assert.Null(first.Previews.Get(0));
            Assert.Equal(1, first.GetPageRotation(0));
            Assert.Equal(0, first.RotatePage(0, -1));
            for (int count = 0; count < 4; count++) first.RotatePage(1, 1);
            Assert.Equal(3, first.GetPageRotation(1));
        }
        finally { first.Cancel(); second.Cancel(); }
    }

    private static void AssertRect(Rect expected, Rect actual)
    {
        AssertPoint(expected.TopLeft, actual.TopLeft);
        AssertPoint(expected.BottomRight, actual.BottomRight);
    }
    private static void AssertPoint(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 7);
        Assert.Equal(expected.Y, actual.Y, 7);
    }
}

public class RotationInteractionTests
{
    [Fact]
    public Task Buttons_PreserveEachTiffPagesRotationAndLeaveOriginalBytesAndTimestampUntouched() => RunOnStaAsync(async () =>
    {
        string path = SaveImage(".tif", 3);
        byte[] original = File.ReadAllBytes(path);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        var window = CreateWindow();
        try
        {
            Assert.False(GetButton(window, "RotateRightButton").IsEnabled);
            window.RotateCurrentPage(1);
            window.Show();
            await window.OpenImageAsync(path);
            var bitmap = ((Image)window.FindName("DisplayedImage")).Source;
            GetButton(window, "RotateRightButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, Viewport(window).RotationQuarterTurns);
            Assert.Same(bitmap, ((Image)window.FindName("DisplayedImage")).Source);
            AssertQuadrants(window, Colors.Blue, Colors.Red, Colors.Yellow, Colors.Lime);
            await window.ChangePageAsync(1);
            Assert.Equal(0, Viewport(window).RotationQuarterTurns);
            AssertQuadrants(window, Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow);
            GetButton(window, "RotateLeftButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, Viewport(window).RotationQuarterTurns);
            AssertQuadrants(window, Colors.Lime, Colors.Yellow, Colors.Red, Colors.Blue);
            await window.ChangePageAsync(1);
            Assert.Equal(0, Viewport(window).RotationQuarterTurns);
            await window.ChangePageAsync(-1);
            Assert.Equal(3, Viewport(window).RotationQuarterTurns);
            await window.ChangePageAsync(-1);
            Assert.Equal(1, Viewport(window).RotationQuarterTurns);
            for (int turn = 0; turn < 3; turn++) window.RotateCurrentPage(1);
            Assert.Equal(0, Viewport(window).RotationQuarterTurns);
            AssertQuadrants(window, Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
            // 開き直したファイルには、前の表示セッションの回転を引き継がない。
            await window.OpenImageAsync(path);
            Assert.Equal(0, Viewport(window).RotationQuarterTurns);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".bmp")]
    public Task Shortcuts_RotateSingleFrameImages(string extension) => RunOnStaAsync(async () =>
    {
        string path = SaveImage(extension);
        var window = CreateWindow();
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            RaiseKey(window, Key.R, window);
            Assert.Equal(1, Viewport(window).RotationQuarterTurns);
            RaiseKey(window, Key.L, window);
            Assert.Equal(0, Viewport(window).RotationQuarterTurns);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task BusyOperations_DisableRotationAndKeepSelectionAndAngleStable() => RunOnStaAsync(async () =>
    {
        string path = SaveImage(".tif");
        string output = path + ".crop.tif";
        var window = CreateWindow();
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            window.RotateCurrentPage(1);
            Rect image = Viewport(window).VisibleImageBounds;
            Assert.True(window.BeginCropSelection(new Point(image.Left + image.Width / 4, image.Top + image.Height / 4)));
            window.CompleteCropDrag(new Point(image.Left + image.Width * 3 / 4, image.Top + image.Height * 3 / 4));
            var selection = Field<ImageCropSelection>(window, "_cropSelection");
            Rect before = selection.ImageRectangle;
            Task<bool> saving = window.SaveCropAsync(output);
            Assert.False(GetButton(window, "RotateRightButton").IsEnabled);
            Assert.False(GetButton(window, "RotateLeftButton").IsEnabled);
            window.RotateCurrentPage(1);
            Assert.Equal(1, Viewport(window).RotationQuarterTurns);
            Assert.Equal(before, selection.ImageRectangle);
            Assert.True(await saving);
            Assert.True(GetButton(window, "RotateRightButton").IsEnabled);
            window.RotateCurrentPage(-1);
            Assert.False(selection.HasSelection);
        }
        finally { window.Close(); File.Delete(path); File.Delete(output); }
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task DetailRegion_UsesSameRotationAndSourceOffsetAsPreview(int rotation) => RunOnStaAsync(async () =>
    {
        string path = SaveImage(".tif", width: 2000, height: 1000);
        var window = CreateWindow();
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            window.RotateCurrentPage(rotation);
            var canvas = (Canvas)window.FindName("ImageViewportCanvas");
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 240, ModifierKeys.None);
            Method(window, "ZoomInteractionTimer_Tick", null, EventArgs.Empty);
            Field<DispatcherTimer?>(window, "_detailLoadTimer")?.Stop();
            var session = Field<ImageLoadSession>(window, "_session");
            var page = Field<ImagePage>(window, "_currentPage");
            await (Task)Method(window, "LoadVisibleDetailsAsync", session, page)!;
            var region = Field<ImageDetailRegion>(window, "_currentDetailRegion");
            Assert.True(region.CoversForDisplay(0, Viewport(window).VisibleSourceBounds, Viewport(window).Scale));
            var detail = (Image)window.FindName("DetailRegionImage");
            Assert.Equal(Visibility.Visible, detail.Visibility);
            AssertRegionTransform(window, region);
            // 画像の中心から離れた領域でも、原点の移動を回転より先に適用する。
            var offsetRegion = new ImageDetailRegion(0, new Int32Rect(200, 100, 800, 400), page.Bitmap);
            Method(window, "SetDisplayedDetailRegion", offsetRegion);
            AssertRegionTransform(window, offsetRegion);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task Toolbar_UsesCompactIconsAndAccessibleNamesAtNarrowWidth() => RunOnStaAsync(async () =>
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            window.Width = ViewerSettings.MinimumWindowWidth;
            window.UpdateLayout();
            foreach (var button in ((WrapPanel)window.FindName("MainToolbar")).Children.OfType<ButtonBase>())
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.NotNull(button.ToolTip);
                Assert.InRange(button.ActualWidth, 25, 45);
                if (button.Name == "HelpButton")
                {
                    Assert.Equal("H", Assert.IsType<string>(button.Content));
                    continue;
                }

                var icon = Assert.IsType<ToolbarIcon>(button.Content);
                Assert.InRange(icon.ActualHeight, 1, ViewerSettings.ToolbarButtonHeight);
                Assert.Equal(button.Foreground, icon.Foreground);
            }
            var rotateLeft = (ToolbarIcon)GetButton(window, "RotateLeftButton").Content;
            Assert.True(rotateLeft.IsMirrored);
            foreach (string name in new[] { "PreviousPageButton", "NextPageButton" })
            {
                var button = GetButton(window, name);
                Assert.False(button.IsEnabled);
                Assert.True(ToolTipService.GetShowOnDisabled(button));
                var tooltip = Assert.IsType<ToolTip>(button.ToolTip);
                Assert.Contains(name == "PreviousPageButton" ? "Previous page (Page Up or 1)" : "Next page (Page Down or 2)",
                    Assert.IsType<string>(tooltip.Content));
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).ActualHeight > 200);
        }
        finally { window.Close(); }
    });

    private static void AssertRegionTransform(MainWindow window, ImageDetailRegion region)
    {
        var matrix = ((MatrixTransform)((Image)window.FindName("DetailRegionImage")).RenderTransform).Matrix;
        var local = new Point(17, 23);
        Point expected = Viewport(window).ImageToViewMatrix.Transform(new Point(local.X + region.Rectangle.X, local.Y + region.Rectangle.Y));
        Point actual = matrix.Transform(local);
        Assert.Equal(expected.X, actual.X, 7);
        Assert.Equal(expected.Y, actual.Y, 7);
    }
    private static void AssertQuadrants(MainWindow window, params Color[] expected)
    {
        var canvas = (Canvas)window.FindName("ImageViewportCanvas");
        canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(canvas.ActualWidth), (int)Math.Ceiling(canvas.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        Rect bounds = Viewport(window).ImageToView(Viewport(window).ImageBounds);
        for (int index = 0; index < 4; index++)
        {
            var pixels = new byte[4];
            int x = (int)(bounds.Left + bounds.Width * (index % 2 == 0 ? 0.25 : 0.75));
            int y = (int)(bounds.Top + bounds.Height * (index < 2 ? 0.25 : 0.75));
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
            Assert.Equal(expected[index], Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]));
        }
    }
    private static string SaveImage(string extension, int pageCount = 1, int width = 320, int height = 160)
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-rotation-{Guid.NewGuid():N}{extension}");
        var pixels = new byte[width * height * 4];
        Color[] colors = [Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Color color = colors[(y < height / 2 ? 0 : 2) + (x < width / 2 ? 0 : 1)];
            int offset = (y * width + x) * 4;
            pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder = extension switch
        {
            ".tif" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            ".jpg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        for (int page = 0; page < pageCount; page++) encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
    private static MainWindow CreateWindow() => new()
    {
        Width = 750, Height = 500, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
    };
    private static Button GetButton(MainWindow window, string name) => (Button)window.FindName(name);
    private static ImageViewport Viewport(MainWindow window) => Field<ImageViewport>(window, "_viewport");
    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Method(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static KeyEventArgs RaiseKey(MainWindow window, Key key, object source)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = source };
        window.RaiseEvent(args);
        return args;
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
