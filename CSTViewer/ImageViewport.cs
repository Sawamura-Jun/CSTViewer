using System.Windows;
using System.Windows.Media;

namespace CSTViewer;

/// <summary>画像の倍率と位置を管理する。UIとは独立して操作計算を検証できる。</summary>
internal sealed class ImageViewport
{
    private Size _imageSize;
    private Size _viewportSize;

    public double Scale { get; private set; } = 1;
    public Point Offset { get; private set; }
    public bool IsFitMode { get; private set; } = true;
    public int RotationQuarterTurns { get; private set; }
    public Size DisplayImageSize => RotationQuarterTurns % 2 == 0 ? _imageSize : new Size(_imageSize.Height, _imageSize.Width);
    public bool HasImage => _imageSize.Width > 0 && _imageSize.Height > 0;
    public Rect ImageBounds => HasImage ? new Rect(_imageSize) : Rect.Empty;
    public Rect VisibleImageBounds => CanTransform
        ? Rect.Intersect(ImageToView(ImageBounds), new Rect(_viewportSize)) : Rect.Empty;
    public Rect VisibleSourceBounds => CanTransform
        ? Rect.Intersect(ViewToImage(new Rect(_viewportSize)), ImageBounds) : Rect.Empty;
    private bool CanTransform => HasImage && _viewportSize.Width > 0 && _viewportSize.Height > 0;
    private double FitScale => Math.Min(_viewportSize.Width / DisplayImageSize.Width,
        _viewportSize.Height / DisplayImageSize.Height);

    // 元画像を正の座標へ90度単位で回してから、表示倍率と位置を適用する。
    private Matrix RotationMatrix => RotationQuarterTurns switch
    {
        1 => new Matrix(0, 1, -1, 0, _imageSize.Height, 0),
        2 => new Matrix(-1, 0, 0, -1, _imageSize.Width, _imageSize.Height),
        3 => new Matrix(0, -1, 1, 0, 0, _imageSize.Width),
        _ => Matrix.Identity
    };

    public Matrix ImageToViewMatrix
    {
        get
        {
            Matrix matrix = RotationMatrix;
            matrix.Scale(Scale, Scale);
            matrix.Translate(Offset.X, Offset.Y);
            return matrix;
        }
    }

    public Point ViewToImage(Point position)
    {
        Matrix inverse = ImageToViewMatrix;
        inverse.Invert();
        return inverse.Transform(position);
    }

    public Rect ViewToImage(Rect rectangle)
    {
        if (rectangle.IsEmpty) return Rect.Empty;
        Matrix inverse = ImageToViewMatrix;
        inverse.Invert();
        rectangle.Transform(inverse);
        return rectangle;
    }

    public Rect ImageToView(Rect rectangle)
    {
        if (rectangle.IsEmpty) return Rect.Empty;
        rectangle.Transform(ImageToViewMatrix);
        return rectangle;
    }

    public void SetImage(Size imageSize, int rotationQuarterTurns = 0)
    {
        _imageSize = imageSize;
        RotationQuarterTurns = ((rotationQuarterTurns % 4) + 4) % 4;
        Fit();
    }

    public void Fit()
    {
        IsFitMode = true;
        if (!CanTransform)
        {
            return;
        }

        Scale = FitScale;
        Offset = new Point((_viewportSize.Width - DisplayImageSize.Width * Scale) / 2,
            (_viewportSize.Height - DisplayImageSize.Height * Scale) / 2);
    }

    public void Resize(Size viewportSize, bool preserveScale = false)
    {
        Size previousSize = _viewportSize;
        _viewportSize = viewportSize;
        if (!CanTransform)
        {
            return;
        }

        if (!preserveScale && (IsFitMode || Scale <= FitScale))
        {
            Fit();
        }
        else
        {
            // 拡大中とFitによる余白調整中は、倍率と中心にある画像座標を維持する。
            Offset += new Vector((viewportSize.Width - previousSize.Width) / 2,
                (viewportSize.Height - previousSize.Height) / 2);
            ClampOffset();
        }
    }

    public void Zoom(Point anchor, int wheelDelta)
    {
        if (!CanTransform || wheelDelta == 0)
        {
            return;
        }

        double factor = Math.Pow(ViewerSettings.WheelZoomFactor,
            wheelDelta / ViewerSettings.MouseWheelDeltaPerStep);
        double targetScale = Math.Clamp(Scale * factor, FitScale,
            Math.Max(FitScale, ViewerSettings.MaximumZoomScale));
        if (wheelDelta < 0 && targetScale <= FitScale * ViewerSettings.FitSnapToleranceRatio)
        {
            Fit();
            return;
        }

        if (targetScale == Scale)
        {
            return;
        }

        // マウス直下の画像座標が動かないよう、倍率の変更に合わせて位置を補正する。
        double ratio = targetScale / Scale;
        Offset = new Point(anchor.X - (anchor.X - Offset.X) * ratio,
            anchor.Y - (anchor.Y - Offset.Y) * ratio);
        Scale = targetScale;
        IsFitMode = false;
        ClampOffset();
    }

    public void Pan(Vector delta)
    {
        if (!CanTransform)
        {
            return;
        }

        Offset += delta;
        ClampOffset();
    }

    public ViewportSyncState? GetSyncState()
    {
        if (!CanTransform) return null;
        Point center = ViewToImage(new Point(_viewportSize.Width / 2, _viewportSize.Height / 2));
        // 寸法が違う原稿にも同じ部分を表示できるよう、倍率と中心を相対値で渡す。
        return new ViewportSyncState(IsFitMode, Math.Max(1, Scale / FitScale),
            new Point(Math.Clamp(center.X / _imageSize.Width, 0, 1),
                Math.Clamp(center.Y / _imageSize.Height, 0, 1)));
    }

    public bool ApplySyncState(ViewportSyncState state)
    {
        if (!CanTransform) return false;
        if (state.IsFitMode)
        {
            Fit();
            return true;
        }
        Scale = Math.Clamp(FitScale * Math.Max(1, state.ZoomRatio), FitScale,
            Math.Max(FitScale, ViewerSettings.MaximumZoomScale));
        Point center = RotationMatrix.Transform(new Point(Math.Clamp(state.Center.X, 0, 1) * _imageSize.Width,
            Math.Clamp(state.Center.Y, 0, 1) * _imageSize.Height));
        Offset = new Point(_viewportSize.Width / 2 - center.X * Scale,
            _viewportSize.Height / 2 - center.Y * Scale);
        IsFitMode = false;
        ClampOffset();
        return true;
    }

    private void ClampOffset()
    {
        // 小さい画像は中央に置き、拡大画像は表示領域から完全に外れないようにする。
        Offset = new Point(ClampAxis(Offset.X, DisplayImageSize.Width * Scale, _viewportSize.Width),
            ClampAxis(Offset.Y, DisplayImageSize.Height * Scale, _viewportSize.Height));
    }

    private static double ClampAxis(double offset, double imageLength, double viewportLength)
    {
        return imageLength <= viewportLength
            ? (viewportLength - imageLength) / 2
            : Math.Clamp(offset, viewportLength - imageLength, 0);
    }
}

internal readonly record struct ViewportSyncState(bool IsFitMode, double ZoomRatio, Point Center);
