using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class PageOrientationSizingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PageTurns_ResizeOnlyWhenOrientationChangesAndKeepAutomaticSizing(bool portraitFirst) => RunOnStaAsync(async () =>
    {
        var first = portraitFirst ? (560, 800) : (800, 400);
        var sameOrientation = portraitFirst ? (600, 900) : (600, 400);
        var opposite = portraitFirst ? (800, 400) : (560, 800);
        string path = SavePages(first, sameOrientation, opposite);
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, path);
            Rect initial = Bounds(window);
            AssertAutomaticSize(window, first.Item1, first.Item2);

            await window.ChangePageAsync(1);
            Assert.Equal(initial, Bounds(window));
            await window.ChangePageAsync(1);
            AssertAutomaticSize(window, opposite.Item1, opposite.Item2);
            Assert.NotEqual(initial.Size, Bounds(window).Size);

            // 自動調整を手動変更と誤認せず、何度往復しても同じルールを適用する。
            for (int pass = 0; pass < 2; pass++)
            {
                await window.ChangePageAsync(-1);
                AssertAutomaticSize(window, sameOrientation.Item1, sameOrientation.Item2);
                await window.ChangePageAsync(1);
                AssertAutomaticSize(window, opposite.Item1, opposite.Item2);
            }
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("restore-size")]
    [InlineData("border")]
    [InlineData("wheel")]
    [InlineData("fit")]
    [InlineData("maximize")]
    public Task UserResize_PermanentlyKeepsChosenBoundsOnPageTurnsAndRotation(string operation) => RunOnStaAsync(async () =>
    {
        string path = SavePages((800, 400), (600, 400), (560, 800));
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, path);
            await window.ChangePageAsync(1);
            Rect original = Bounds(window);
            switch (operation)
            {
                case "width": window.Width -= 40; break;
                case "height": window.Height -= 20; break;
                case "restore-size":
                    window.Width -= 40;
                    window.UpdateLayout();
                    window.Width = original.Width;
                    break;
                case "border":
                    // 枠をドラッグしたときのWin32通知を、実ウィンドウのフックへ渡す。
                    bool handled = false;
                    window.SpreadWindowHook(new WindowInteropHelper(window).Handle, 0x0214,
                        IntPtr.Zero, IntPtr.Zero, ref handled);
                    window.Width -= 40;
                    break;
                case "wheel": window.HandleViewportWheel(new Point(), -120, ModifierKeys.Control); break;
                case "fit": window.FitImageLocally(); break;
                case "maximize":
                    window.WindowState = WindowState.Maximized;
                    window.WindowState = WindowState.Normal;
                    break;
            }
            window.UpdateLayout();
            Rect chosen = Bounds(window);
            if (operation is not ("restore-size" or "maximize")) Assert.NotEqual(original.Size, chosen.Size);
            window.RotateCurrentPage(1);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(chosen, Bounds(window));
            window.RotateCurrentPage(-1);
            Assert.Equal(chosen, Bounds(window));
            await window.ChangePageAsync(1);
            Assert.Equal(chosen, Bounds(window));
            AssertDisplayedPage(window, 560, 800);
            await window.ChangePageAsync(-1);
            Assert.Equal(chosen, Bounds(window));
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData("move")]
    [InlineData("minimize")]
    [InlineData("zoom")]
    public Task ActionsWithoutWindowResize_KeepAutomaticSizing(string operation) => RunOnStaAsync(async () =>
    {
        string path = SavePages((800, 400), (560, 800));
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, path);
            switch (operation)
            {
                case "move": window.Left += 20; window.Top += 10; break;
                case "minimize":
                    window.WindowState = WindowState.Minimized;
                    window.WindowState = WindowState.Normal;
                    break;
                case "zoom": window.HandleViewportWheel(new Point(), 120, ModifierKeys.None); break;
            }
            window.UpdateLayout();
            window.RotateCurrentPage(1);
            AssertAutomaticDisplaySize(window, 400, 800);
            window.RotateCurrentPage(-1);
            AssertAutomaticSize(window, 800, 400);
            await window.ChangePageAsync(1);
            AssertAutomaticSize(window, 560, 800);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EmptyStartup_KeepsAutomaticSizingUnlessUserResizes(bool userResizes) => RunOnStaAsync(async () =>
    {
        string path = SavePages((800, 400), (560, 800));
        var window = CreateWindow();
        try
        {
            window.PrepareEmptyStartup();
            window.Show();
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (userResizes) window.Width -= 40;
            window.UpdateLayout();
            Rect initial = Bounds(window);
            await window.OpenImageAsync(path);
            window.RotateCurrentPage(1);
            if (userResizes) Assert.Equal(initial, Bounds(window));
            else AssertAutomaticDisplaySize(window, 400, 800);
            window.RotateCurrentPage(-1);
            await window.ChangePageAsync(1);
            if (userResizes) Assert.Equal(initial, Bounds(window));
            else AssertAutomaticSize(window, 560, 800);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task OpeningAnotherFile_DoesNotResizeOrResetManualResizeHistory() => RunOnStaAsync(async () =>
    {
        string firstPath = SavePages((800, 400), (560, 800));
        string secondPath = SavePages((560, 800), (800, 400));
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, firstPath);
            Rect initial = Bounds(window);
            await window.OpenImageAsync(secondPath);
            Assert.Equal(initial, Bounds(window));
            await window.ChangePageAsync(1);
            AssertAutomaticSize(window, 800, 400);

            window.Width -= 40;
            window.UpdateLayout();
            Rect chosen = Bounds(window);
            await window.OpenImageAsync(firstPath);
            await window.ChangePageAsync(1);
            AssertDisplayedPage(window, 560, 800);
            Assert.Equal(chosen, Bounds(window));
        }
        finally { window.Close(); File.Delete(firstPath); File.Delete(secondPath); }
    });

    [Fact]
    public Task SyncLayout_PreservesBothWindowsAndDoesNotCountAsManualResize() => RunOnStaAsync(async () =>
    {
        string path = SavePages((800, 400), (560, 800));
        var controller = new SpreadController();
        var source = CreateWindow(controller);
        var partner = CreateWindow(controller);
        try
        {
            await OpenStartupAsync(source, path);
            await OpenStartupAsync(partner, path);
            source.ToggleSpread();
            Rect sourceBounds = Bounds(source);
            Rect partnerBounds = Bounds(partner);
            source.RotateCurrentPage(1);
            Assert.Equal(sourceBounds, Bounds(source));
            Assert.Equal(partnerBounds, Bounds(partner));
            partner.RotateCurrentPage(-1);
            Assert.Equal(sourceBounds, Bounds(source));
            Assert.Equal(partnerBounds, Bounds(partner));
            await source.ChangePageAsync(1);
            AssertDisplayedPage(source, 560, 800);
            AssertDisplayedPage(partner, 560, 800);
            Assert.Equal(sourceBounds, Bounds(source));
            Assert.Equal(partnerBounds, Bounds(partner));

            source.ToggleSpread();
            await source.ChangePageAsync(-1);
            AssertAutomaticSize(source, 800, 400);
            source.RotateCurrentPage(1);
            AssertAutomaticDisplaySize(source, 400, 800);
        }
        finally { source.Close(); partner.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task RotationBeforeManualResize_ReusesStartupSizingAcrossRepeatedTurns(bool portraitFirst, bool shortcut) => RunOnStaAsync(async () =>
    {
        int width = portraitFirst ? 560 : 800;
        int height = portraitFirst ? 800 : 400;
        string path = SavePages((width, height));
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, path);
            Rect initial = Bounds(window);
            int rotation = 0;
            foreach (int step in new[] { 1, 1, -1, -1 })
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
                rotation = (rotation + step + 4) % 4;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                AssertDisplayedPage(window, width, height);
                AssertAutomaticDisplaySize(window, rotation % 2 == 0 ? width : height, rotation % 2 == 0 ? height : width);
            }
            // 自動調整自体を手動リサイズと誤認せず、元の向きでは元の起動寸法へ戻す。
            Assert.Equal(initial.Width, window.ActualWidth, 1);
            Assert.Equal(initial.Height, window.ActualHeight, 1);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task RotatedPageNavigation_ResizesUsingTheRetainedViewingAngle() => RunOnStaAsync(async () =>
    {
        string path = SavePages((800, 400), (800, 400));
        var window = CreateWindow();
        try
        {
            await OpenStartupAsync(window, path);
            window.RotateCurrentPage(1);
            AssertAutomaticDisplaySize(window, 400, 800);
            await window.ChangePageAsync(1);
            AssertAutomaticSize(window, 800, 400);
            await window.ChangePageAsync(-1);
            AssertDisplayedPage(window, 800, 400);
            AssertAutomaticDisplaySize(window, 400, 800);
        }
        finally { window.Close(); File.Delete(path); }
    });

    private static MainWindow CreateWindow(SpreadController? controller = null) => new(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-page-sizing-{Guid.NewGuid()}.jsonl"), spreadController: controller)
    { ShowActivated = false };

    private static async Task OpenStartupAsync(MainWindow window, string path)
    {
        await window.PrepareStartupImageAsync(path);
        window.Show();
        window.UpdateLayout();
        await window.OpenImageAsync(path);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static Rect Bounds(MainWindow window) => new(window.Left, window.Top, window.ActualWidth, window.ActualHeight);

    private static void AssertDisplayedPage(MainWindow window, int width, int height)
    {
        var image = (Image)window.FindName("DisplayedImage");
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.True(window.SpreadViewState!.Value.IsFitMode);
    }

    private static void AssertAutomaticSize(MainWindow window, int width, int height)
    {
        window.UpdateLayout();
        AssertDisplayedPage(window, width, height);
        AssertAutomaticDisplaySize(window, width, height);
    }

    private static void AssertAutomaticDisplaySize(MainWindow window, int width, int height)
    {
        window.UpdateLayout();
        Rect monitor = WindowScreen.MonitorBounds(window);
        Rect area = WindowScreen.WorkArea(window);
        var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
        if (width >= height)
        {
            double expected = Math.Clamp(monitor.Width * ViewerSettings.StartupLandscapeWindowWidthRatio,
                window.MinWidth, Math.Max(window.MinWidth, area.Width));
            Assert.InRange(window.ActualWidth, expected - 1, expected + 1);
            if (window.ActualHeight > window.MinHeight + 2 && window.ActualHeight < area.Height - 2)
                Assert.InRange(canvas.ActualHeight, canvas.ActualWidth * height / width - 2,
                    canvas.ActualWidth * height / width + 2);
        }
        else
        {
            double expected = Math.Clamp(monitor.Height * ViewerSettings.StartupPortraitWindowHeightRatio,
                window.MinHeight, Math.Max(window.MinHeight, area.Height));
            Assert.InRange(window.ActualHeight, expected - 1, expected + 1);
            if (window.ActualWidth > window.MinWidth + 2 && window.ActualWidth < area.Width - 2)
                Assert.InRange(canvas.ActualWidth, canvas.ActualHeight * width / height - 2,
                    canvas.ActualHeight * width / height + 2);
        }
    }

    private static string SavePages(params (int Width, int Height)[] pages)
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-page-sizing-{Guid.NewGuid()}.tif");
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        foreach (var page in pages)
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(page.Width, page.Height, 137)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // UIスレッドでページ切り替えと実ウィンドウのレイアウト通知を処理する。
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
