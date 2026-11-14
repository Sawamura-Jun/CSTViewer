using System.Windows;

namespace CSTViewer.Tests;

public class WindowSizingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void SpreadFrame_CompensatesInvisibleBordersAtDifferentDpi(double scale)
    {
        double border = Math.Round(7 * scale);
        var outer = new Rect(100, 200, 1000, 800);
        var frame = new Rect(100 + border, 200, 1000 - 2 * border, 800 - border);
        // 負の画面座標と奇数ピクセル幅でも、左右が同じ境界を共有する。
        var left = new Rect(-1921, -1080, 960, 1041);
        var right = new Rect(-961, -1080, 961, 1041);
        Rect leftOuter = WindowSizing.OuterBoundsForVisibleFrame(left, outer, frame);
        Rect rightOuter = WindowSizing.OuterBoundsForVisibleFrame(right, outer, frame);
        Assert.Equal(left.Left - border, leftOuter.Left);
        Assert.Equal(left.Top, leftOuter.Top);
        Assert.Equal(left.Height + border, leftOuter.Height);
        Assert.Equal(left.Right, leftOuter.Right - border);
        Assert.Equal(right.Left, rightOuter.Left + border);
        Assert.Equal(leftOuter.Right - border, rightOuter.Left + border);
        Assert.Equal(right.Right, rightOuter.Right - border);
    }

    [Fact]
    public void SpreadFrame_WithoutInvisibleBordersKeepsRequestedBounds()
    {
        var frame = new Rect(100, 200, 1000, 800);
        var requested = new Rect(0, 0, 960, 1040);
        Assert.Equal(requested, WindowSizing.OuterBoundsForVisibleFrame(requested, frame, frame));
    }

    [Theory]
    [InlineData(2000, 1000, 1280, 720)]
    [InlineData(1000, 2000, 492.2, 1004.4)]
    public void StartupBounds_UsesWholeMonitorRatioAndFitsOtherAxis(double width, double height,
        double expectedWidth, double expectedHeight)
    {
        var monitor = new Rect(-1920, 0, 1920, 1080);
        var area = new Rect(-1920, 40, 1920, 1040);
        var bounds = WindowSizing.StartupBounds(new Size(width, height), new Size(40, 100),
            new Size(200, 200), monitor, area);
        // 高さ93%では寸法が小数になるため、浮動小数点の計算誤差を許容する。
        Assert.Equal(expectedWidth, bounds.Width, 8);
        Assert.Equal(expectedHeight, bounds.Height, 8);
        Assert.Equal(width / height, (bounds.Width - 40) / (bounds.Height - 100), 8);
        Assert.Equal(-960, bounds.Left + bounds.Width / 2, 8);
        Assert.Equal(560, bounds.Top + bounds.Height / 2, 8);
        Assert.True(area.Contains(bounds));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void StartupBounds_PreservesMonitorRatioAtDifferentDpi(double dpiScale)
    {
        // 物理ピクセルからDIPへ変換済みのモニター寸法で計算する。
        var monitor = new Rect(-2560 / dpiScale, -1440 / dpiScale, 2560 / dpiScale, 1440 / dpiScale);
        var area = new Rect(monitor.Left, monitor.Top, monitor.Width, 1400 / dpiScale);
        var chrome = new Size(40, 100);
        var landscape = WindowSizing.StartupBounds(new Size(2000, 1000), chrome, new Size(200, 200), monitor, area);
        var portrait = WindowSizing.StartupBounds(new Size(1000, 2000), chrome, new Size(200, 200), monitor, area);
        Assert.Equal(2560 * 2.0 / 3.0, landscape.Width * dpiScale, 8);
        Assert.Equal(1440 * 0.93, portrait.Height * dpiScale, 8);
        Assert.Equal(2, (landscape.Width - chrome.Width) / (landscape.Height - chrome.Height), 8);
        Assert.Equal(0.5, (portrait.Width - chrome.Width) / (portrait.Height - chrome.Height), 8);
        Assert.True(area.Contains(landscape));
        Assert.True(area.Contains(portrait));
    }

    [Theory]
    [InlineData(1000, 1000, 1280, 1040)]
    [InlineData(1, 10000, 600, 1004.4)]
    [InlineData(10000, 1, 1280, 400)]
    public void StartupBounds_RespectsMinimumSizeAndWorkArea(double width, double height,
        double expectedWidth, double expectedHeight)
    {
        var monitor = new Rect(0, 0, 1920, 1080);
        var area = new Rect(40, 0, 1880, 1040);
        var bounds = WindowSizing.StartupBounds(new Size(width, height), new Size(40, 100),
            new Size(600, 400), monitor, area);
        Assert.Equal(expectedWidth, bounds.Width, 8);
        Assert.Equal(expectedHeight, bounds.Height, 8);
        Assert.True(area.Contains(bounds));
    }

    [Fact]
    public void StartupBounds_ClampsRatioAxisBeforeFittingOtherAxis()
    {
        var monitor = new Rect(0, 0, 800, 600);
        var area = new Rect(0, 0, 800, 500);
        var landscape = WindowSizing.StartupBounds(new Size(2000, 1000), new Size(40, 100),
            new Size(600, 200), monitor, area);
        var portrait = WindowSizing.StartupBounds(new Size(1000, 2000), new Size(40, 100),
            new Size(200, 200), monitor, area);
        Assert.Equal(new Size(600, 380), landscape.Size);
        Assert.Equal(new Size(240, 500), portrait.Size);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 24, 24)]
    [InlineData(2, 48, 40)]
    [InlineData(5, 100, 40)]
    [InlineData(6, 0, 0)]
    public void StartupPlacement_CascadesWithinTheWorkAreaAndReturnsToCenter(int index, double x, double y)
    {
        var area = new Rect(-2000, -900, 600, 480);
        var size = new Size(400, 400);
        Rect bounds = WindowSizing.StartupPlacement(size, area, index);
        Assert.Equal(new Rect(-1900 + x, -860 + y, size.Width, size.Height), bounds);
        Assert.True(area.Contains(bounds));
    }

    [Theory]
    [InlineData(1000, 500, 1000, 750, 1040, 600)]
    [InlineData(375, 750, 1000, 750, 415, 850)]
    [InlineData(750, 750, 1000, 750, 790, 850)]
    [InlineData(996, 498, 1000, 750, 1040, 602)]
    [InlineData(1000, 500, 1000.6666666667, 750, 1040, 600)]
    [InlineData(1000.0000000001, 500.0000000001, 1000.6666666667, 750, 1040, 600)]
    public void FitBounds_RemovesAspectRatioMarginAndPreservesCommonPadding(double imageWidth,
        double imageHeight, double viewWidth, double viewHeight, double expectedWidth, double expectedHeight)
    {
        var center = new Point(960, 540);
        var bounds = WindowSizing.FitBounds(center, new Size(imageWidth, imageHeight),
            new Size(viewWidth, viewHeight), new Size(40, 100), new Size(200, 200), new Rect(0, 0, 1920, 1080));
        Assert.Equal(new Size(expectedWidth, expectedHeight), bounds.Size);
        Assert.Equal(center, new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
    }

    [Fact]
    public void FitBounds_RespectsMinimumSizeAndSecondaryMonitorEdges()
    {
        var area = new Rect(-1920, -1040, 1920, 1040);
        var bounds = WindowSizing.FitBounds(area.TopLeft, new Size(100.2, 200.4), new Size(800, 200.4),
            new Size(40, 100), new Size(480, 400), area);
        Assert.Equal(new Rect(area.TopLeft, new Size(480, 400)), bounds);
        bounds = WindowSizing.FitBounds(area.BottomRight, new Size(100.2, 200.4), new Size(800, 200.4),
            new Size(40, 100), new Size(50, 50), area);
        Assert.Equal(new Size(141, 301), bounds.Size);
        Assert.Equal(area.BottomRight, bounds.BottomRight);
    }

    [Theory]
    [InlineData(2000, 500, 1600, 550)]
    [InlineData(800, 2000, 760, 900)]
    public void ImageZoomBounds_GrowsRemainingAxisAfterOtherAxisReachesWorkArea(double imageWidth,
        double imageHeight, double expectedWidth, double expectedHeight)
    {
        var area = new Rect(0, 0, 1600, 900);
        var bounds = WindowSizing.ImageZoomBounds(new Point(800, 450), new Size(imageWidth, imageHeight),
            0.9, new Size(40, 100), new Size(600, 400), area);

        Assert.Equal(new Size(expectedWidth, expectedHeight), bounds.Size);
        Assert.True(area.Contains(bounds));
    }

    [Fact]
    public void WheelResize_ScalesViewportAndPreservesWindowCenter()
    {
        var original = new Rect(300, 200, 1000, 700);
        var resized = WindowSizing.WheelBounds(original, new Size(960, 600), new Size(600, 400), new Rect(0, 0, 1920, 1080), 120);
        Assert.Equal(1115, resized.Width);
        Assert.Equal(772, resized.Height);
        Assert.Equal(original.Left + original.Width / 2, resized.Left + resized.Width / 2);
        Assert.Equal(original.Top + original.Height / 2, resized.Top + resized.Height / 2);
    }

    [Theory]
    [InlineData(12000)]
    [InlineData(int.MaxValue)]
    public void WheelResize_ContinuesOtherAxisAfterOneAxisReachesScreenLimit(int delta)
    {
        var area = new Rect(100, 50, 1400, 1000);
        var resized = WindowSizing.WheelBounds(new Rect(100, 200, 1400, 600), new Size(1360, 500),
            new Size(600, 400), area, delta);
        Assert.Equal(area, resized);
    }

    [Theory]
    [InlineData(-12000)]
    [InlineData(int.MinValue)]
    public void WheelResize_StopsAtMinimumWithoutChangingAspectRatio(int delta)
    {
        var resized = WindowSizing.WheelBounds(new Rect(500, 300, 1000, 700), new Size(960, 600),
            new Size(600, 400), new Rect(0, 0, 1920, 1080), delta);
        Assert.Equal(600, resized.Width);
        Assert.Equal(450, resized.Height);
        Assert.Equal(960.0 / 600, (resized.Width - 40) / (resized.Height - 100));
        Assert.Equal(resized, WindowSizing.WheelBounds(resized, new Size(560, 350), new Size(600, 400),
            new Rect(0, 0, 1920, 1080), -120));
    }

    [Fact]
    public void WheelResize_ClampsPositionOnSecondaryMonitorAndIgnoresZeroDelta()
    {
        var area = new Rect(-1600, -1000, 1600, 900);
        var original = new Rect(-1600, -1000, 800, 600);
        Assert.Equal(original, WindowSizing.WheelBounds(original, new Size(760, 500), new Size(600, 400), area, 0));
        var resized = WindowSizing.WheelBounds(original, new Size(760, 500), new Size(600, 400), area, 120);
        Assert.Equal(area.Left, resized.Left);
        Assert.Equal(area.Top, resized.Top);
        Assert.True(area.Contains(resized));
    }
}
