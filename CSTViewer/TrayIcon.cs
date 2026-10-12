using System.IO;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CSTViewer;

/// <summary>常駐中の表示・終了操作を通知領域に用意する。</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon _icon;
    private readonly Drawing.Font _font;
    private bool _disposed;

    public TrayIcon(Action openViewer, Action exitApplication)
    {
        // アイコンを埋め込みリソースから読み、配置先や単一ファイル発行に依存させない。
        using Stream stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(ViewerSettings.TrayIconResourceName)
            ?? throw new InvalidOperationException("The CSTViewer tray icon resource is missing.");
        using var sourceIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        _icon = (Drawing.Icon)sourceIcon.Clone();
        // WPFのDIPとWindows Formsのptを換算し、本文と文字サイズを揃える。
        _font = new Drawing.Font(ViewerSettings.TrayMenuFontFamily, (float)(ViewerSettings.UiFontSize * 72 / 96));
        _menu = new Forms.ContextMenuStrip
        {
            Name = "TrayApplicationMenu",
            ShowImageMargin = false,
            Font = _font,
            ForeColor = Drawing.ColorTranslator.FromHtml(ViewerSettings.TrayMenuForeground),
            Renderer = new Forms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false }
        };
        var openItem = new Forms.ToolStripMenuItem("Open CSTViewer") { Name = "TrayOpenViewerMenuItem" };
        var exitItem = new Forms.ToolStripMenuItem("Exit") { Name = "TrayExitApplicationMenuItem" };
        openItem.Click += (_, _) => openViewer();
        exitItem.Click += (_, _) => exitApplication();
        _menu.Items.Add(openItem);
        _menu.Items.Add(exitItem);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = ViewerSettings.ApplicationTitle,
            ContextMenuStrip = _menu,
            Visible = true
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) openViewer();
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _font.Dispose();
        _icon.Dispose();
    }

    // 通知領域のメニューも、ビューアーと同じダーク配色にする。
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        private static Drawing.Color Background => Drawing.ColorTranslator.FromHtml(ViewerSettings.TrayMenuBackground);
        private static Drawing.Color Hover => Drawing.ColorTranslator.FromHtml(ViewerSettings.TrayMenuHoverBackground);
        private static Drawing.Color Border => Drawing.ColorTranslator.FromHtml(ViewerSettings.TrayMenuBorder);
        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Border;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color MenuItemPressedGradientBegin => Hover;
        public override Drawing.Color MenuItemPressedGradientMiddle => Hover;
        public override Drawing.Color MenuItemPressedGradientEnd => Hover;
    }
}
