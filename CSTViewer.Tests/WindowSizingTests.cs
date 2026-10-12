using System.Windows;

namespace CSTViewer.Tests;

public class WindowSizingTests
{
    [Theory]
    [InlineData(2000, 1000, 1000, 500)]
    // 縦長画像は現行の高さ805DIPに合わせる。幅402.5は偶数丸めで402になる。
    [InlineData(1000, 2000, 402, 805)]
    [InlineData(1000, 1000, 1000, 1000)]
    [InlineData(1, 10000, 1, 805)]
    public void StartupViewport_UsesPythonAspectRatio(double width, double height, double expectedWidth, double expectedHeight)
    {
        Assert.Equal(new Size(expectedWidth, expectedHeight), WindowSizing.StartupViewportSize(new Size(width, height)));
    }

    [Fact]
    public void StartupBounds_IncludesChromeAndCentersWithinWorkArea()
    {
        var area = new Rect(-1920, 40, 1920, 1040);
        var bounds = WindowSizing.StartupBounds(new Size(2000, 1000), new Size(40, 100), new Size(600, 400), area);
        Assert.Equal(new Rect(-1480, 260, 1040, 600), bounds);
        var square = WindowSizing.StartupBounds(new Size(1000, 1000), new Size(40, 100), new Size(600, 400), area);
        Assert.Equal(1040, square.Height);
        Assert.True(area.Contains(square));
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
