using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CSTViewer;

/// <summary>字形の実寸に合わせてアイコンを中央に描画する。</summary>
public sealed class ToolbarIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string),
        typeof(ToolbarIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsMirroredProperty = DependencyProperty.Register(nameof(IsMirrored), typeof(bool),
        typeof(ToolbarIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    // ボタンの無効状態・選択状態の前景色を、そのままアイコンにも引き継ぐ。
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(ToolbarIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool IsMirrored { get => (bool)GetValue(IsMirroredProperty); set => SetValue(IsMirroredProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private Geometry? _icon;

    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = new FormattedText(Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(ViewerSettings.ToolbarIconFontFamily), ViewerSettings.ToolbarIconSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _icon = formatted.BuildGeometry(new Point());
        _icon.Freeze();
        return _icon.Bounds.Size;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_icon is null) return;
        Rect bounds = _icon.Bounds;
        drawingContext.PushTransform(new TranslateTransform((ActualWidth - bounds.Width) / 2 - bounds.X,
            (ActualHeight - bounds.Height) / 2 - bounds.Y));
        if (IsMirrored) drawingContext.PushTransform(new ScaleTransform(-1, 1, bounds.X + bounds.Width / 2, 0));
        drawingContext.DrawGeometry(Foreground, null, _icon);
        if (IsMirrored) drawingContext.Pop();
        drawingContext.Pop();
    }
}
