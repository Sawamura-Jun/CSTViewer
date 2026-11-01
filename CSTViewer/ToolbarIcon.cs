using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace CSTViewer;

/// <summary>字形の実寸でアイコンとショートカットを並べ、ボタンの左右の余白を揃える。</summary>
public sealed class ToolbarIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string),
        typeof(ToolbarIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(nameof(Shortcut), typeof(string),
        typeof(ToolbarIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsMirroredProperty = DependencyProperty.Register(nameof(IsMirrored), typeof(bool),
        typeof(ToolbarIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    // ボタンの無効状態・選択状態の前景色を、そのままアイコンにも引き継ぐ。
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(ToolbarIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Shortcut { get => (string)GetValue(ShortcutProperty); set => SetValue(ShortcutProperty, value); }
    public bool IsMirrored { get => (bool)GetValue(IsMirroredProperty); set => SetValue(IsMirroredProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private Geometry? _icon;
    private Geometry? _shortcut;
    private Size _contentSize;

    protected override Size MeasureOverride(Size availableSize)
    {
        _icon = CreateShape(Glyph, ViewerSettings.ToolbarIconFontFamily, ViewerSettings.ToolbarIconSize);
        _shortcut = string.IsNullOrEmpty(Shortcut) ? null
            : CreateShape(Shortcut, ViewerSettings.ToolbarShortcutFontFamily, ViewerSettings.UiFontSize);
        _contentSize = new Size(_icon.Bounds.Width + (_shortcut is null ? 0
            : ViewerSettings.ToolbarIconShortcutGap + _shortcut.Bounds.Width),
            Math.Max(_icon.Bounds.Height, _shortcut?.Bounds.Height ?? 0));
        return _contentSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_icon is null) return;
        double left = (ActualWidth - _contentSize.Width) / 2;
        DrawShape(drawingContext, _icon, left, IsMirrored);
        if (_shortcut is not null)
            DrawShape(drawingContext, _shortcut, left + _icon.Bounds.Width + ViewerSettings.ToolbarIconShortcutGap, false);
    }

    private void DrawShape(DrawingContext context, Geometry shape, double left, bool mirrored)
    {
        Rect bounds = shape.Bounds;
        context.PushTransform(new TranslateTransform(left - bounds.X, (ActualHeight - bounds.Height) / 2 - bounds.Y));
        if (mirrored) context.PushTransform(new ScaleTransform(-1, 1, bounds.X + bounds.Width / 2, 0));
        context.DrawGeometry(Foreground, null, shape);
        if (mirrored) context.Pop();
        context.Pop();
    }

    private Geometry CreateShape(string text, string fontFamily, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(fontFamily), size, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        Geometry geometry = formatted.BuildGeometry(new Point());
        geometry.Freeze();
        return geometry;
    }
}
