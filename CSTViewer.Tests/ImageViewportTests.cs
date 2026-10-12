using System.Windows;

namespace CSTViewer.Tests;

public class ImageViewportTests
{
    [Theory]
    [InlineData(2000, 1000, 0.5, 0, 125)]
    [InlineData(1000, 2000, 0.375, 312.5, 0)]
    public void Fit_PreservesAspectRatioAndCentersImage(double width, double height,
        double scale, double offsetX, double offsetY)
    {
        var viewport = CreateViewport(new Size(width, height), new Size(1000, 750));

        Assert.Equal(scale, viewport.Scale, 8);
        AssertPoint(new Point(offsetX, offsetY), viewport.Offset);
        Assert.True(viewport.IsFitMode);
    }

    [Fact]
    public void Zoom_KeepsImagePointUnderMouse()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        var mousePosition = new Point(700, 350);
        Point imagePoint = ImagePointAt(viewport, mousePosition);

        viewport.Zoom(mousePosition, 120);

        Assert.Equal(0.625, viewport.Scale, 8);
        AssertPoint(imagePoint, ImagePointAt(viewport, mousePosition));
        Assert.False(viewport.IsFitMode);
    }

    [Fact]
    public void ZoomOut_StopsAtFitAndRecenters()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 750));
        viewport.Zoom(new Point(500, 375), 480);
        viewport.Pan(new Vector(-100, -100));

        viewport.Zoom(new Point(100, 100), -1200);
        viewport.Zoom(new Point(100, 100), -120);

        Assert.Equal(0.5, viewport.Scale, 8);
        AssertPoint(new Point(0, 125), viewport.Offset);
        Assert.True(viewport.IsFitMode);
    }

    [Fact]
    public void Zoom_RespectsMaximumScaleAndRemainsFinite()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        viewport.Zoom(new Point(500, 250), int.MaxValue);
        viewport.Zoom(new Point(500, 250), 120);

        Assert.Equal(ViewerSettings.MaximumZoomScale, viewport.Scale);
        Assert.True(double.IsFinite(viewport.Offset.X));
        Assert.True(double.IsFinite(viewport.Offset.Y));
    }

    [Fact]
    public void Pan_ClampsImageAtBothEdges()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        viewport.Zoom(new Point(500, 250), 120);

        viewport.Pan(new Vector(-10000, -10000));
        AssertPoint(new Point(-250, -125), viewport.Offset);

        viewport.Pan(new Vector(10000, 10000));
        AssertPoint(new Point(0, 0), viewport.Offset);
    }

    [Fact]
    public void Pan_KeepsSmallerAxisCentered()
    {
        var viewport = CreateViewport(new Size(2000, 500), new Size(1000, 750));
        viewport.Zoom(new Point(500, 375), 120);
        viewport.Pan(new Vector(-100, -100));

        AssertPoint(new Point(-225, 218.75), viewport.Offset);
    }

    [Fact]
    public void Resize_RefitsImageInFitMode()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        viewport.Resize(new Size(500, 500));

        Assert.Equal(0.25, viewport.Scale, 8);
        AssertPoint(new Point(0, 125), viewport.Offset);
    }

    [Fact]
    public void Resize_PreservesFitScaleWhenWindowMinimumLeavesExtraSpace()
    {
        var viewport = CreateViewport(new Size(1000, 2000), new Size(1000, 500));
        viewport.Resize(new Size(600, 600), preserveScale: true);

        Assert.True(viewport.IsFitMode);
        Assert.Equal(0.25, viewport.Scale, 8);
        AssertPoint(new Point(175, 50), viewport.Offset);
        // 次の通常リサイズでは、従来どおり表示領域に再フィットする。
        viewport.Resize(new Size(600, 600));
        Assert.Equal(0.3, viewport.Scale, 8);
        AssertPoint(new Point(150, 0), viewport.Offset);
    }

    [Fact]
    public void Resize_KeepsCenterImagePointWhenZoomed()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        viewport.Zoom(new Point(500, 250), 480);
        Point previousCenter = ImagePointAt(viewport, new Point(500, 250));
        double previousScale = viewport.Scale;

        viewport.Resize(new Size(800, 400));

        Assert.Equal(previousScale, viewport.Scale);
        AssertPoint(previousCenter, ImagePointAt(viewport, new Point(400, 200)));
    }

    [Fact]
    public void SetImage_ResetsPreviousPanAndZoom()
    {
        var viewport = CreateViewport(new Size(2000, 1000), new Size(1000, 500));
        viewport.Zoom(new Point(500, 250), 480);
        viewport.Pan(new Vector(-100, -100));

        viewport.SetImage(new Size(1000, 1000));

        Assert.Equal(0.5, viewport.Scale);
        AssertPoint(new Point(250, 0), viewport.Offset);
        Assert.True(viewport.IsFitMode);
    }

    [Fact]
    public void EmptyViewport_IgnoresOperationsUntilImageAndSizeAreAvailable()
    {
        var viewport = new ImageViewport();
        viewport.Zoom(new Point(), 120);
        viewport.Pan(new Vector(100, 100));
        viewport.SetImage(new Size(2000, 1000));
        viewport.Resize(new Size(0, 0));
        viewport.Resize(new Size(1000, 500));

        Assert.Equal(0.5, viewport.Scale);
        AssertPoint(new Point(), viewport.Offset);
    }

    private static ImageViewport CreateViewport(Size imageSize, Size viewportSize)
    {
        var viewport = new ImageViewport();
        viewport.Resize(viewportSize);
        viewport.SetImage(imageSize);
        return viewport;
    }

    private static Point ImagePointAt(ImageViewport viewport, Point screenPoint)
    {
        return new Point((screenPoint.X - viewport.Offset.X) / viewport.Scale,
            (screenPoint.Y - viewport.Offset.Y) / viewport.Scale);
    }

    private static void AssertPoint(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
