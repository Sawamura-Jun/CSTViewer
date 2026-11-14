using System.Windows;

namespace CSTViewer.Tests;

public class SpreadWindowPlacementTests
{
    [Theory]
    [InlineData(0, 1400)]
    [InlineData(.5, 1000)]
    [InlineData(1, 600)]
    public void Drag_RestoresOriginalSizeUnderPointer(double anchorRatio, double expectedLeft)
    {
        var placement = new SpreadWindowPlacement();
        placement.Capture(new Size(800, 600), new Size(960, 1040));
        placement.BeginDrag(new Rect(960, 0, 960, 1040), new Point(960 + 960 * anchorRatio, 20), new DpiScale(1, 1));
        Assert.True(placement.TryMove(new Rect(1000, 80, 960, 1040), new Point(1400, 100), new DpiScale(1, 1), out var bounds));
        Assert.Equal(new Rect(expectedLeft, 80, 800, 600), bounds);
        Assert.False(placement.IsWaitingToRestore);

        // 同じ移動ループの次の通知でも、古い左右配置のサイズへ戻らない。
        Assert.True(placement.TryMove(new Rect(1050, 100, 960, 1040), new Point(1450, 120), new DpiScale(1, 1), out bounds));
        Assert.Equal(new Rect(expectedLeft + 50, 100, 800, 600), bounds);
        placement.EndDrag();
        Assert.False(placement.TryMove(bounds, new Point(1500, 150), new DpiScale(1, 1), out _));
    }

    [Fact]
    public void ClickAndSmallMovement_KeepLayoutAndAllowNextDrag()
    {
        var placement = new SpreadWindowPlacement();
        var bounds = new Rect(0, 0, 960, 1040);
        var pointer = new Point(480, 20);
        placement.Capture(new Size(800, 600), bounds.Size);
        placement.BeginDrag(bounds, pointer, new DpiScale(1, 1));
        Assert.False(placement.TryMove(bounds, pointer, new DpiScale(1, 1), out _));
        Assert.False(placement.TryMove(new Rect(1, 1, 960, 1040), new Point(481, 21), new DpiScale(1, 1), out _));
        placement.EndDrag();
        Assert.True(placement.IsWaitingToRestore);
        placement.BeginDrag(bounds, pointer, new DpiScale(1, 1));
        Assert.True(placement.TryMove(new Rect(20, 20, 960, 1040), new Point(500, 40), new DpiScale(1, 1), out _));
    }

    [Fact]
    public void DpiChangeAndNegativeCoordinates_PreserveLogicalSizeAndTitleBarAnchor()
    {
        var placement = new SpreadWindowPlacement();
        placement.Capture(new Size(800, 600), new Size(960, 1040));
        placement.BeginDrag(new Rect(-1920, -1080, 960, 1040), new Point(-1440, -1060), new DpiScale(1, 1));
        Assert.True(placement.TryMove(new Rect(-1900, -1060, 960, 1040), new Point(-1000, -500), new DpiScale(1.5, 1.5), out var bounds));
        Assert.Equal(new Rect(-1600, -530, 1200, 900), bounds);
        Assert.True(placement.TryMove(bounds, new Point(-800, -300), new DpiScale(2, 2), out bounds));
        Assert.Equal(new Rect(-1600, -340, 1600, 1200), bounds);
    }

    [Fact]
    public void RepeatedArrangement_DoesNotOverwriteOriginalSize()
    {
        var placement = new SpreadWindowPlacement();
        placement.Capture(new Size(800, 600), new Size(960, 1040));
        placement.Capture(new Size(960, 1040), new Size(1000, 1100));
        placement.SizeChanged(new Size(1000.5, 1100.5));
        placement.BeginDrag(new Rect(0, 0, 1000, 1100), new Point(500, 20), new DpiScale(1, 1));
        Assert.True(placement.TryMove(new Rect(30, 20, 1000, 1100), new Point(530, 40), new DpiScale(1, 1), out var bounds));
        Assert.Equal(new Size(800, 600), bounds.Size);
    }

    [Fact]
    public void ResizeOrCancel_DiscardsRestoreSizeUntilNextArrangement()
    {
        var placement = new SpreadWindowPlacement();
        placement.Capture(new Size(800, 600), new Size(960, 1040));
        placement.SizeChanged(new Size(900, 1040));
        placement.BeginDrag(new Rect(0, 0, 900, 1040), new Point(450, 20), new DpiScale(1, 1));
        Assert.False(placement.TryMove(new Rect(30, 20, 900, 1040), new Point(480, 40), new DpiScale(1, 1), out _));
        placement.Capture(new Size(900, 1040), new Size(960, 1040));
        placement.Cancel();
        Assert.False(placement.IsWaitingToRestore);
    }
}
