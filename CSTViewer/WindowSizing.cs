using System.Windows;

namespace CSTViewer;

/// <summary>画像の縦横比と画面の作業領域から、ウィンドウの寸法と位置を計算する。</summary>
internal static class WindowSizing
{
    public static Rect OuterBoundsForVisibleFrame(Rect visibleBounds, Rect outerBounds, Rect frameBounds)
    {
        // 見えない枠の分だけ外側へ広げ、表示枠を指定領域の四辺に合わせる。
        return new Rect(new Point(visibleBounds.Left - (frameBounds.Left - outerBounds.Left),
            visibleBounds.Top - (frameBounds.Top - outerBounds.Top)),
            new Point(visibleBounds.Right + (outerBounds.Right - frameBounds.Right),
                visibleBounds.Bottom + (outerBounds.Bottom - frameBounds.Bottom)));
    }

    public static Rect StartupBounds(Size imageSize, Size chromeSize, Size minimumSize,
        Rect monitorBounds, Rect workArea)
    {
        double maxWidth = Math.Max(minimumSize.Width, workArea.Width);
        double maxHeight = Math.Max(minimumSize.Height, workArea.Height);
        double width, height;
        // 指定する辺はウィンドウ全体の寸法。反対側は画像表示領域の縦横比から求める。
        if (imageSize.Width >= imageSize.Height)
        {
            width = Math.Clamp(monitorBounds.Width * ViewerSettings.StartupLandscapeWindowWidthRatio,
                minimumSize.Width, maxWidth);
            height = Math.Max(1, width - chromeSize.Width) * imageSize.Height / imageSize.Width + chromeSize.Height;
        }
        else
        {
            height = Math.Clamp(monitorBounds.Height * ViewerSettings.StartupPortraitWindowHeightRatio,
                minimumSize.Height, maxHeight);
            width = Math.Max(1, height - chromeSize.Height) * imageSize.Width / imageSize.Height + chromeSize.Width;
        }
        // 極端な縦横比でも、既存の最小寸法とタスクバーを除く画面内の制限を守る。
        width = Math.Clamp(width, minimumSize.Width, maxWidth);
        height = Math.Clamp(height, minimumSize.Height, maxHeight);
        return CenteredBounds(new Point(workArea.Left + workArea.Width / 2, workArea.Top + workArea.Height / 2),
            new Size(width, height), workArea);
    }

    public static Rect WheelBounds(Rect currentBounds, Size viewportSize, Size minimumSize, Rect workArea, int delta)
    {
        if (delta == 0 || viewportSize.Width <= 0 || viewportSize.Height <= 0) return currentBounds;

        double chromeWidth = Math.Max(0, currentBounds.Width - viewportSize.Width);
        double chromeHeight = Math.Max(0, currentBounds.Height - viewportSize.Height);
        double minWidth = Math.Max(1, minimumSize.Width - chromeWidth);
        double minHeight = Math.Max(1, minimumSize.Height - chromeHeight);
        double maxWidth = Math.Max(minWidth, workArea.Width - chromeWidth);
        double maxHeight = Math.Max(minHeight, workArea.Height - chromeHeight);
        double steps = Math.Max(1, Math.Floor(Math.Abs((double)delta) / ViewerSettings.MouseWheelDeltaPerStep));
        double factor = Math.Pow(ViewerSettings.CtrlWheelWindowScaleFactor, delta > 0 ? steps : -steps);

        if (delta < 0)
        {
            double minimumScale = Math.Max(minWidth / viewportSize.Width, minHeight / viewportSize.Height);
            if (minimumScale >= 1) return currentBounds;
            factor = Math.Max(factor, minimumScale);
        }

        // 上限に達した辺は止め、もう一方の辺は画面いっぱいまで拡げられるようにする。
        double width = Math.Clamp(Math.Round(viewportSize.Width * factor), minWidth, maxWidth) + chromeWidth;
        double height = Math.Clamp(Math.Round(viewportSize.Height * factor), minHeight, maxHeight) + chromeHeight;
        if (width == currentBounds.Width && height == currentBounds.Height) return currentBounds;
        return CenteredBounds(new Point(currentBounds.Left + currentBounds.Width / 2,
            currentBounds.Top + currentBounds.Height / 2), new Size(width, height), workArea);
    }

    public static Rect FitBounds(Point center, Size displayedImageSize, Size viewportSize,
        Size chromeSize, Size minimumSize, Rect workArea)
    {
        // Python版と同じく、両辺に共通する最小余白だけを残して縦横比による余白を詰める。
        double padding = Math.Max(0, Math.Min(viewportSize.Width - displayedImageSize.Width,
            viewportSize.Height - displayedImageSize.Height));
        if (padding < ViewerSettings.FitWindowPaddingTolerance) padding = 0;
        // 浮動小数点の微小誤差だけで、切り上げ寸法が1増えることを防ぐ。
        var size = new Size(
            Math.Max(minimumSize.Width, Math.Ceiling(Math.Round(displayedImageSize.Width + padding, 6)) + chromeSize.Width),
            Math.Max(minimumSize.Height, Math.Ceiling(Math.Round(displayedImageSize.Height + padding, 6)) + chromeSize.Height));
        return CenteredBounds(center, size, workArea);
    }

    private static Rect CenteredBounds(Point center, Size size, Rect workArea)
    {
        // 画面端では中心維持より、タイトルバーを含むウィンドウ全体の操作可能性を優先する。
        double left = Math.Clamp(center.X - size.Width / 2, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        double top = Math.Clamp(center.Y - size.Height / 2, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return new Rect(new Point(left, top), size);
    }
}
