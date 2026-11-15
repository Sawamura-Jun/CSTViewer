using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class ImageSamplingQualityTests
{
    [Theory]
    [InlineData(0.75)]
    [InlineData(0.85)]
    [InlineData(0.95)]
    public Task ReducedDetail_PreservesThinLinesAcrossPixelPositions(double scale) => RunOnStaAsync(() =>
    {
        const int width = 512, height = 64, lineCount = 24, spacing = 19, firstLine = 20;
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int line = 0; line < lineCount; line++)
            for (int y = 0; y < height; y++) pixels[y * width + firstLine + line * spacing] = 0;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        var window = CreateWindow();
        try
        {
            var viewport = Field<ImageViewport>(window, "_viewport");
            double dpiScale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            viewport.Resize(new Size(width * scale / dpiScale, height * scale / dpiScale));
            viewport.SetImage(new Size(width, height));
            typeof(MainWindow).GetMethod("SetDisplayedDetailRegion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new ImageDetailRegion(0, new Int32Rect(0, 0, width, height), bitmap)]);
            var overlay = (Image)window.FindName("DetailRegionImage");
            Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(overlay));
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, RenderOptions.GetBitmapScalingMode(overlay));
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                drawing.DrawImage(bitmap, new Rect(0, 0, width * scale, height * scale));
            }
            var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var row = new byte[width * 4];
            rendered.CopyPixels(new Int32Rect(0, (int)(height * scale / 2), width, 1), row, row.Length, 0);

            // 同じ1px線を異なる小数位置へ縮小し、線の消失と濃さの周期的な変動を検出する。
            var darkness = new List<int>();
            for (int line = 0; line < lineCount; line++)
            {
                int center = (int)((firstLine + line * spacing) * scale);
                darkness.Add(Enumerable.Range(center - 2, 6).Sum(x => 255 - row[x * 4]));
            }
            Assert.All(darkness, value => Assert.True(value > 100, $"縮小率{scale}で細線が消失しました: {value}"));
            Assert.True(darkness.Max() - darkness.Min() < 85,
                $"縮小率{scale}で線の濃さが不均一です: {darkness.Min()}〜{darkness.Max()}");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void DetailCache_RequiresOversamplingWhenZoomIncreases()
    {
        var region = new ImageDetailRegion(0, new Int32Rect(0, 0, 2000, 1000),
            PreviewLoadingTests.CreateBitmap(720, 360, 71));
        var cache = new ImageDetailRegionCache(8, 8 * 1024 * 1024);
        cache.Add(region);
        Assert.Same(region, cache.Get(0, region.Bounds, 0.2));
        // 表示への復帰は1倍を満たせば可能だが、追加生成は1.8倍に不足していれば実施する。
        Assert.True(region.CoversForDisplay(0, region.Bounds, 0.25));
        Assert.Null(cache.Get(0, region.Bounds, 0.25));
        Assert.Same(region, cache.GetForDisplay(0, region.Bounds));
        // さらに拡大しても暫定表示には使える。違うページや範囲外の画素には流用しない。
        Assert.False(region.CoversForDisplay(0, region.Bounds, 0.5));
        Assert.Same(region, cache.GetForDisplay(0, region.Bounds));
        Assert.Null(cache.GetForDisplay(1, region.Bounds));
        Assert.Null(cache.GetForDisplay(0, new Rect(1990, 0, 100, 100)));
        var full = region with { Bitmap = PreviewLoadingTests.CreateBitmap(2000, 1000, 71) };
        cache.Add(full);
        Assert.Same(full, cache.Get(0, full.Bounds, 4));
    }

    [Fact]
    public Task Fit_UsesInitialDetailAndKeepsItWhenReturningFromZoom() => RunOnStaAsync(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"CSTViewer-quality-{Guid.NewGuid():N}.tif");
        var window = CreateWindow();
        try
        {
            ImageDetailRegionTests.Save(path, PreviewLoadingTests.CreateBitmap(4096, 3072, 71));
            window.Show();
            await window.OpenImageAsync(path);
            var viewport = Field<ImageViewport>(window, "_viewport");
            var image = (Image)window.FindName("DisplayedImage");
            var fit = image.Source;
            Assert.NotNull(fit);
            Assert.True(viewport.IsFitMode);
            Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
            var canvas = (FrameworkElement)window.FindName("ImageViewportCanvas");
            window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2),
                120 * 2, System.Windows.Input.ModifierKeys.None);
            await WaitForAsync(() => HasDetailedResolution(window));
            window.FitImageLocally(resizeWindow: false);
            Assert.True(viewport.IsFitMode);
            Assert.Same(fit, image.Source);
            Assert.Null(image.Clip);
            Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(image));
        }
        finally
        {
            var session = Field<ImageLoadSession?>(window, "_session");
            window.Close();
            // Dispatcherを終了する前に、全体読み込みの後始末と共有デコード枠の解放を待つ。
            await WaitForAsync(() => !Field<bool>(window, "_isDetailLoading")
                && Field<CancellationTokenSource?>(window, "_detailMarginCancellation") is null
                && (session is null || !session.IsFullPageLoading(0)));
            File.Delete(path);
        }
    });

    private static bool HasDetailedResolution(MainWindow window)
    {
        if (Field<bool>(window, "_isDetailLoading") || Field<bool>(window, "_isZoomInteracting")) return false;
        var detail = Field<ImageDetailRegion?>(window, "_currentDetailRegion");
        if (detail is null) return false;
        var viewport = Field<ImageViewport>(window, "_viewport");
        double scale = Math.Min(1, viewport.Scale * VisualTreeHelper.GetDpi(window).DpiScaleX * 1.8);
        return detail.Bounds.Contains(viewport.VisibleSourceBounds)
            && detail.Bitmap.PixelWidth >= Math.Floor(detail.Rectangle.Width * scale) - 1
            && detail.Bitmap.PixelHeight >= Math.Floor(detail.Rectangle.Height * scale) - 1;
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static MainWindow CreateWindow() => new()
    {
        Width = 750, Height = 500, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "高精細表示への更新が完了しませんでした。");
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
