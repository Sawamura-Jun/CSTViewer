using System.Windows;

namespace CSTViewer.Tests;

public class ImageCropSelectionTests
{
    [Theory]
    [InlineData(100, 125, 300, 225)]
    [InlineData(300, 225, 100, 125)]
    [InlineData(300, 125, 100, 225)]
    [InlineData(100, 225, 300, 125)]
    public void DragInEveryDirection_MapsToSourcePixels(double x1, double y1, double x2, double y2)
    {
        var viewport = CreateViewport();
        var selection = new ImageCropSelection();
        Assert.True(selection.BeginSelection(new Point(x1, y1), viewport));
        Assert.False(selection.End(new Point(x2, y2), viewport));
        Assert.Equal(new Rect(200, 100, 400, 200), selection.ImageRectangle);
        Assert.Equal(new Int32Rect(200, 100, 400, 200), selection.PixelRectangle(new Size(1000, 500)));
    }

    [Fact]
    public void DragOutsideCanvas_ClampsToImageAndExcludesLetterbox()
    {
        var viewport = CreateViewport();
        var selection = new ImageCropSelection();
        selection.BeginSelection(new Point(-50, -100), viewport);
        selection.End(new Point(700, 600), viewport);
        Assert.Equal(new Rect(0, 0, 1000, 500), selection.ImageRectangle);
        Assert.Equal(CropHit.None, selection.HitTest(new Point(100, 20), viewport));
    }

    [Fact]
    public void ClickAndThinDrag_ClearSelection()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        selection.BeginSelection(new Point(100, 125), viewport);
        selection.End(new Point(101, 126), viewport);
        Assert.False(selection.HasSelection);
        selection.BeginSelection(new Point(100, 125), viewport);
        selection.End(new Point(101, 200), viewport);
        Assert.False(selection.HasSelection);
    }

    [Fact]
    public void Move_ClampsWholeRectangleAndPreservesSize()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        Assert.True(selection.BeginAdjustment(new Point(200, 175), viewport));
        Assert.False(selection.End(new Point(900, -100), viewport));
        Assert.Equal(new Rect(600, 0, 400, 200), selection.ImageRectangle);
    }

    [Theory]
    [InlineData(100, 125, (int)(CropHit.Left | CropHit.Top))]
    [InlineData(300, 225, (int)(CropHit.Right | CropHit.Bottom))]
    [InlineData(200, 125, (int)CropHit.Top)]
    [InlineData(300, 175, (int)CropHit.Right)]
    public void Handles_HitTestAtConstantViewSize(double x, double y, int expected)
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        Assert.Equal((CropHit)expected, selection.HitTest(new Point(x, y), viewport));
    }

    [Fact]
    public void ResizeCorner_CanCrossOppositeCorner()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        selection.BeginAdjustment(new Point(100, 125), viewport);
        selection.End(new Point(350, 275), viewport);
        Assert.Equal(new Rect(600, 300, 100, 100), selection.ImageRectangle);
    }

    [Fact]
    public void InsideClick_RequestsSaveButDragReturningToStartDoesNot()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        selection.BeginAdjustment(new Point(200, 175), viewport);
        Assert.True(selection.End(new Point(201, 176), viewport));
        selection.BeginAdjustment(new Point(200, 175), viewport);
        selection.Update(new Point(220, 195), viewport);
        Assert.False(selection.End(new Point(200, 175), viewport));
    }

    [Fact]
    public void CaptureLoss_RestoresAdjustmentAndDiscardsNewDrag()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        Rect before = selection.ImageRectangle;
        selection.BeginAdjustment(new Point(200, 175), viewport);
        selection.Update(new Point(250, 200), viewport);
        selection.CancelDrag();
        Assert.Equal(before, selection.ImageRectangle);
        selection.BeginSelection(new Point(100, 125), viewport);
        selection.Update(new Point(250, 200), viewport);
        selection.CancelDrag();
        Assert.False(selection.HasSelection);
        Assert.False(selection.IsDragging);
    }

    [Fact]
    public void ZoomPanAndResize_PreserveSelectedPixels()
    {
        var viewport = CreateViewport();
        var selection = Select(viewport);
        Int32Rect pixels = selection.PixelRectangle(new Size(1000, 500));
        viewport.Zoom(new Point(250, 200), 480);
        viewport.Pan(new Vector(-60, 30));
        viewport.Resize(new Size(700, 600));
        Assert.Equal(pixels, selection.PixelRectangle(new Size(1000, 500)));
        Rect view = viewport.ImageToView(selection.ImageRectangle);
        Assert.Equal(selection.ImageRectangle.TopLeft, viewport.ViewToImage(view.TopLeft));
    }

    [Fact]
    public void NoImage_DoesNotStartSelection()
    {
        var selection = new ImageCropSelection();
        Assert.False(selection.BeginSelection(new Point(), new ImageViewport()));
        Assert.Equal(Int32Rect.Empty, selection.PixelRectangle(new Size()));
    }

    private static ImageViewport CreateViewport()
    {
        var viewport = new ImageViewport();
        viewport.Resize(new Size(500, 400));
        viewport.SetImage(new Size(1000, 500));
        return viewport;
    }

    private static ImageCropSelection Select(ImageViewport viewport)
    {
        var selection = new ImageCropSelection();
        selection.BeginSelection(new Point(100, 125), viewport);
        selection.End(new Point(300, 225), viewport);
        return selection;
    }
}
