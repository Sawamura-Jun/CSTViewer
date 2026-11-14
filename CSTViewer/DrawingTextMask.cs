using OpenCvSharp;

namespace CSTViewer;

internal static class DrawingTextMask
{
    public static byte[] Create(Mat ink, int stride, double scale, CancellationToken cancellationToken)
    {
        int width = ink.Cols;
        int height = ink.Rows;
        int maxGlyphSize = ScaleLength(OcrSearchSettings.LineRemovalMaxGlyphSize, scale);
        using var labels = new Mat();
        Component[] components = FindComponents(ink, labels);
        // ラベルは1画素4バイトのため、大判図面では行単位で参照して全体のバイト数の桁あふれを避ける。
        var originalRows = labels.AsRows<int>();

        var graphics = components.Where(component => component.MaxSize > maxGlyphSize &&
            component.Area > ScaleArea(1600, scale) &&
            component.Width < width * 0.86 && component.Height < height * 0.86).ToArray();
        var candidates = new List<(Component Component, Shape Shape)>();
        foreach (Component component in components.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (component.Area >= ScaleArea(4, scale) && component.MaxSize <= maxGlyphSize)
            {
                bool insideDrawing = graphics.Any(component.IsInside);
                candidates.Add((component, Classify(originalRows, component, scale, insideDrawing)));
            }
        }

        // 小数点や公差記号の近くに、同じ行または列に並ぶ文字があるかを調べる。
        var anchors = candidates.Where(item => item.Shape == Shape.Text &&
            item.Component.MinSize >= ScaleLength(9, scale) && item.Component.Area >= ScaleArea(65, scale))
            .Select(item => item.Component).ToList();
        Component[] nearbyRings = candidates.Where(item => item.Shape == Shape.Ring && HasTextNeighbor(item.Component, anchors))
            .Select(item => item.Component).ToArray();
        anchors.AddRange(nearbyRings);

        var pixels = new byte[checked(stride * height)];
        Array.Fill(pixels, byte.MaxValue);
        foreach (var (component, shape) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shape == Shape.Text || (shape != Shape.Frame && HasTextNeighbor(component, anchors)))
            {
                CopyComponent(pixels, stride, originalRows, component);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        RecoverMarginLabels(ink, pixels, stride, originalRows, components, scale, maxGlyphSize, cancellationToken);
        return pixels;
    }

    private static Component[] FindComponents(Mat ink, Mat labels)
    {
        using var stats = new Mat();
        using var centroids = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(ink, labels, stats, centroids);
        var rows = stats.AsRows<int>();
        var components = new Component[count];
        for (int index = 1; index < count; index++)
        {
            // OpenCVの統計表は、左、上、幅、高さ、面積の順に1成分5列で並ぶ。
            ReadOnlySpan<int> row = rows[index];
            components[index] = new Component(index, row[0], row[1], row[2], row[3], row[4]);
        }
        return components;
    }

    private static Shape Classify(MatRowAccessor<int> labels, Component component, double scale, bool insideDrawing)
    {
        if (component.MinSize <= ScaleLength(4, scale) && component.MaxSize >= ScaleLength(8, scale))
        {
            return Shape.Line;
        }
        if (component.MinSize < ScaleLength(10, scale))
        {
            return Shape.Text;
        }

        int width = component.Width;
        int height = component.Height;
        using var mask = new Mat(height, width, MatType.CV_8UC1);
        Span<byte> points = mask.AsSpan<byte>();
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<int> source = labels[component.Top + y].Slice(component.Left, width);
            for (int x = 0; x < width; x++)
            {
                points[y * width + x] = source[x] == component.Label ? byte.MaxValue : (byte)0;
            }
        }

        if (component.MinSize >= ScaleLength(55, scale) && component.Density < 0.31 &&
            HasFrameEdges(points, width, height, ScaleLength(4, scale)))
        {
            return Shape.Frame;
        }
        if (IsCross(points, component, scale))
        {
            return Shape.Line;
        }
        if (insideDrawing && component.MinSize >= ScaleLength(13, scale) && component.Density <= 0.50)
        {
            Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy,
                RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
            for (int index = 0; index < contours.Length; index++)
            {
                if (hierarchy[index].Parent >= 0 && Cv2.ContourArea(contours[index]) >= 0.30 * width * height)
                {
                    return Shape.Ring;
                }
            }
        }
        return Shape.Text;
    }

    private static bool HasFrameEdges(ReadOnlySpan<byte> points, int width, int height, int strip)
    {
        int top = 0;
        int bottom = 0;
        for (int x = 0; x < width; x++)
        {
            bool hasTop = false;
            bool hasBottom = false;
            for (int inset = 0; inset < strip; inset++)
            {
                hasTop |= points[inset * width + x] != 0;
                hasBottom |= points[(height - 1 - inset) * width + x] != 0;
            }
            top += hasTop ? 1 : 0;
            bottom += hasBottom ? 1 : 0;
        }
        if (top < width * 0.91 || bottom < width * 0.91)
        {
            return false;
        }

        int left = 0;
        int right = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = points.Slice(y * width, width);
            left += row[..strip].Contains(byte.MaxValue) ? 1 : 0;
            right += row[^strip..].Contains(byte.MaxValue) ? 1 : 0;
        }
        return left >= height * 0.91 && right >= height * 0.91;
    }

    private static bool IsCross(ReadOnlySpan<byte> points, Component component, double scale)
    {
        int width = component.Width;
        int height = component.Height;
        double centerX = (width - 1) / 2.0;
        double centerY = (height - 1) / 2.0;
        int strip = ScaleLength(3, scale);
        double axisTolerance = component.MinSize < ScaleLength(42, scale)
            ? Math.Max(strip, component.MinSize * 0.17) : strip;
        double edgeToleranceX = Math.Max(ScaleLength(4, scale), width * 0.045);
        double edgeToleranceY = Math.Max(ScaleLength(4, scale), height * 0.045);
        int onAxes = 0;
        bool offAxisAtEdge = false;
        for (int y = 0; y < height; y++)
        {
            double dy = Math.Abs(y - centerY);
            for (int x = 0; x < width; x++)
            {
                if (points[y * width + x] == 0)
                {
                    continue;
                }
                double dx = Math.Abs(x - centerX);
                onAxes += dx <= axisTolerance || dy <= axisTolerance ? 1 : 0;
                offAxisAtEdge |= ((y < strip || y >= height - strip) && dx > edgeToleranceX)
                    || ((x < strip || x >= width - strip) && dy > edgeToleranceY);
            }
        }
        return onAxes > 0.97 * component.Area || (component.Density <= 0.28 && !offAxisAtEdge);
    }

    private static bool HasTextNeighbor(Component component, List<Component> anchors)
    {
        foreach (Component other in anchors)
        {
            if (other.Label == component.Label)
            {
                continue;
            }
            double gapX = Math.Max(other.Left - component.Right, component.Left - other.Right);
            double gapY = Math.Max(other.Top - component.Bottom, component.Top - other.Bottom);
            bool sameRow = Math.Abs(component.CenterY - other.CenterY) <= Math.Max(component.Height, other.Height) * 0.52;
            bool sameColumn = Math.Abs(component.CenterX - other.CenterX) <= Math.Max(component.Width, other.Width) * 0.52;
            if ((sameRow && gapX <= other.Height) || (sameColumn && gapY <= other.Width))
            {
                return true;
            }
        }
        return false;
    }

    private static void RecoverMarginLabels(Mat ink, byte[] pixels, int stride, MatRowAccessor<int> originalLabels,
        Component[] originalComponents, double scale, int maxGlyphSize, CancellationToken cancellationToken)
    {
        int width = ink.Cols;
        int height = ink.Rows;
        int longLine = ScaleLength(OcrSearchSettings.LineRemovalLongLineLength, scale) | 1;
        // 横長・縦長の構造要素で、外枠の長い線を抽出してグリッド番号を切り離す。
        using var detachedLabels = new Mat();
        using (var lineMask = new Mat())
        {
            using (var horizontal = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(longLine, 1)))
                Cv2.MorphologyEx(ink, lineMask, MorphTypes.Open, horizontal);
            using (var vertical = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, longLine)))
            using (var verticalMask = new Mat())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.MorphologyEx(ink, verticalMask, MorphTypes.Open, vertical);
                Cv2.BitwiseOr(lineMask, verticalMask, lineMask);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Cv2.BitwiseNot(lineMask, lineMask);
            // 元の画素は以降使わないため、線を除いた結果で上書きする。
            Cv2.BitwiseAnd(ink, lineMask, ink);
        }
        Component[] detached = FindComponents(ink, detachedLabels);
        var detachedRows = detachedLabels.AsRows<int>();
        double margin = OcrSearchSettings.LineRemovalMarginRatio;
        foreach (Component component in detached.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool nearMargin = component.CenterX < width * margin || component.CenterX > width * (1 - margin)
                || component.CenterY < height * margin || component.CenterY > height * (1 - margin);
            bool solidTick = component.Density > 0.91 && component.MaxSize > 3 * component.MinSize;
            if (!nearMargin || component.MinSize < ScaleLength(8, scale) || component.MaxSize > maxGlyphSize
                || component.Area < ScaleArea(50, scale) || component.Density < 0.11 || solidTick)
            {
                continue;
            }

            Point firstPixel = FindFirstPixel(detachedRows, component);
            if (originalComponents[originalLabels[firstPixel.Y][firstPixel.X]].MaxSize > maxGlyphSize &&
                Classify(detachedRows, component, scale, insideDrawing: false) == Shape.Text)
            {
                CopyComponent(pixels, stride, detachedRows, component);
            }
        }
    }

    private static Point FindFirstPixel(MatRowAccessor<int> labels, Component component)
    {
        for (int y = component.Top; y < component.Bottom; y++)
        {
            int index = labels[y].Slice(component.Left, component.Width).IndexOf(component.Label);
            if (index >= 0)
            {
                return new Point(component.Left + index, y);
            }
        }
        throw new InvalidOperationException("連結成分の画素を取得できませんでした。");
    }

    private static void CopyComponent(byte[] output, int stride, MatRowAccessor<int> labels, Component component)
    {
        for (int y = component.Top; y < component.Bottom; y++)
        {
            ReadOnlySpan<int> row = labels[y];
            int outputOffset = y * stride;
            for (int x = component.Left; x < component.Right; x++)
            {
                if (row[x] == component.Label)
                {
                    // 白黒TIFFは1画素1ビット。白で初期化した出力に黒画素だけを残す。
                    output[outputOffset + x / 8] &= (byte)~(0x80 >> (x % 8));
                }
            }
        }
    }

    private static int ScaleLength(int length, double scale) => Math.Max(1, (int)Math.Round(length * scale));

    private static int ScaleArea(int area, double scale) => Math.Max(1, (int)Math.Round(area * scale * scale));

    private enum Shape { Text, Line, Frame, Ring }

    private readonly record struct Component(int Label, int Left, int Top, int Width, int Height, int Area)
    {
        public int Right => Left + Width;
        public int Bottom => Top + Height;
        public double CenterX => Left + Width / 2.0;
        public double CenterY => Top + Height / 2.0;
        public int MinSize => Math.Min(Width, Height);
        public int MaxSize => Math.Max(Width, Height);
        public double Density => Area / ((double)Width * Height);

        public bool IsInside(Component other) =>
            other.Left < Left && other.Top < Top && Right < other.Right && Bottom < other.Bottom;
    }
}
