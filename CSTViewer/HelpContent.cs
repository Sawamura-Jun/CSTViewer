using System.Globalization;
using System.Runtime.InteropServices;

namespace CSTViewer;

internal sealed record HelpContent(string LanguageCode, string DisplayName, string Title, string LanguageLabel,
    string FunctionHeader, HelpToolbar Toolbar, HelpCrop Crop, HelpMouse Mouse)
{
    internal static IReadOnlyList<HelpContent> Languages { get; } =
    [
        new("en", "English", "CSTViewer Help", "Language", "Function",
            new("Toolbar", "Button", "Shortcut",
            [
                (nameof(MainWindow.SaveImageButton), "Save", "—", "Copy the entire open image file to the desktop."),
                (nameof(MainWindow.OpenImageButton), "Open", "—", "Choose an image file to open."),
                (nameof(MainWindow.NewWindowButton), "New window", "Ctrl+Shift+N", "Open another viewer window."),
                (nameof(MainWindow.SpreadLayoutButton), "Sync", "S",
                    "Arrange two windows side by side and sync actions such as zooming and page navigation. Press again to turn sync off."),
                (nameof(MainWindow.DiffImageButton), "Compare", "D",
                    "Compare all pages with the nearest window, using this window as the newer image. Additions are red and deletions are blue."),
                (nameof(MainWindow.FitImageButton), "Fit", "F",
                    "Fit the entire image in the window. If it already fits, reduce the window margins."),
                (nameof(MainWindow.FitWheelZoomButton), "Resize with zoom", "A",
                    "Toggle resizing the window along with mouse-wheel zoom."),
                (nameof(MainWindow.RotateRightButton), "Rotate right", "R", "Rotate the displayed page 90° right."),
                (nameof(MainWindow.RotateLeftButton), "Rotate left", "L", "Rotate the displayed page 90° left."),
                (nameof(MainWindow.PreviousPageButton), "Previous page", "Page Up / 1", "Go to the previous page in a multipage TIFF."),
                (nameof(MainWindow.NextPageButton), "Next page", "Page Down / 2", "Go to the next page in a multipage TIFF."),
                (nameof(MainWindow.PreviousFileButton), "Previous file", "Q", "Open the previous image in the same folder."),
                (nameof(MainWindow.NextFileButton), "Next file", "W", "Open the next image in the same folder."),
                (nameof(MainWindow.HelpButton), "Help", "—", "Open this help window.")
            ]),
            new("Crop",
            [
                ("Select", "Right-drag on the image to select the area to crop."),
                ("Adjust", "Left-drag inside the selection to move it, or drag an edge or corner to resize it."),
                ("Save", "Left-click inside the selection to save the crop and copy it to the clipboard.")
            ],
                "Press Ctrl+C to copy the selection to the clipboard, or Esc to clear it.",
                " specifies the destination for crops and comparison images."),
            new("Mouse controls", "Action",
            [
                ("Mouse wheel", "Zoom around the pointer."),
                ("Left-drag", "Pan the image by dragging outside the crop selection."),
                ("Ctrl + mouse wheel", "Resize the window in its normal state."),
                ("Left double-click", "Maximize or restore the window."),
                ("Middle-click", "Close the window. In Sync mode, close both windows."),
                ("Drop an image file", "Open the dropped image.")
            ])),
        new("ja", "日本語", "CSTViewer ヘルプ", "表示言語", "機能",
            new("ツールバー", "ボタン", "ショートカット",
            [
                (nameof(MainWindow.SaveImageButton), "保存", "—", "開いている画像ファイルを丸ごとデスクトップにコピーします。"),
                (nameof(MainWindow.OpenImageButton), "開く", "—", "表示する画像ファイルを選びます。"),
                (nameof(MainWindow.NewWindowButton), "新しいウィンドウ", "Ctrl+Shift+N", "別のビューアーウィンドウを開きます。"),
                (nameof(MainWindow.SpreadLayoutButton), "同期", "S",
                    "2つのウィンドウを横に並べ、ズームやページ移動を同期します。もう一度押すと解除します。"),
                (nameof(MainWindow.DiffImageButton), "差分", "D",
                    "このウィンドウを新しい画像として、最も近いウィンドウと全ページを比較します。追加は赤、削除は青です。"),
                (nameof(MainWindow.FitImageButton), "全体表示", "F",
                    "画像全体を表示します。すでに全体を表示しているときは、ウィンドウの余白を詰めます。"),
                (nameof(MainWindow.FitWheelZoomButton), "ウィンドウ連動ズーム", "A",
                    "ホイールでズームするときに、ウィンドウのサイズも変えるかを切り替えます。"),
                (nameof(MainWindow.RotateRightButton), "右回転", "R", "表示中のページを右に90°回転します。"),
                (nameof(MainWindow.RotateLeftButton), "左回転", "L", "表示中のページを左に90°回転します。"),
                (nameof(MainWindow.PreviousPageButton), "前のページ", "Page Up / 1", "複数ページのTIFFで前のページに移動します。"),
                (nameof(MainWindow.NextPageButton), "次のページ", "Page Down / 2", "複数ページのTIFFで次のページに移動します。"),
                (nameof(MainWindow.PreviousFileButton), "前のファイル", "Q", "同じフォルダー内の前の画像を開きます。"),
                (nameof(MainWindow.NextFileButton), "次のファイル", "W", "同じフォルダー内の次の画像を開きます。"),
                (nameof(MainWindow.HelpButton), "ヘルプ", "—", "このヘルプウィンドウを開きます。")
            ]),
            new("切り抜き（CROP）",
            [
                ("選ぶ", "画像を右ドラッグして、切り抜く範囲を選びます。"),
                ("調整する", "選択枠の内側を左ドラッグすると移動し、枠の辺や角を左ドラッグするとサイズが変わります。"),
                ("保存する", "選択枠の内側を左クリックすると、切り抜きを保存し、クリップボードにもコピーします。")
            ],
                "Ctrl+C でクリップボードにコピーし、Esc で選択を解除します。",
                " 欄では、切り抜きや差分画像の保存先を指定できます。"),
            new("マウス操作", "操作",
            [
                ("ホイール", "ポインターの位置を中心に拡大・縮小します。"),
                ("左ドラッグ", "切り抜きの選択枠の外側で画像を移動します。"),
                ("Ctrl ＋ ホイール", "通常表示時にウィンドウのサイズを変更します。"),
                ("左ダブルクリック", "最大化と通常表示を切り替えます。"),
                ("中クリック", "ウィンドウを閉じます。同期中は2つとも閉じます。"),
                ("画像ファイルをドロップ", "ドロップした画像を開きます。")
            ]))
    ];

    // コンボボックスと読み上げに、選択肢の表示名を使う。
    public override string ToString() => DisplayName;

    internal static HelpContent WindowsDefaultLanguage()
    {
        // アプリが英語に固定する CurrentUICulture とは別に、Windows の表示言語を取得する。
        try
        {
            string code = CultureInfo.GetCultureInfo(GetUserDefaultUILanguage()).TwoLetterISOLanguageName;
            return Languages.FirstOrDefault(help => help.LanguageCode == code) ?? Languages[0];
        }
        catch (CultureNotFoundException)
        {
            return Languages[0];
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}

internal sealed record HelpToolbar(string Title, string ButtonHeader, string ShortcutHeader,
    (string ButtonName, string Label, string Shortcut, string Description)[] Rows);

internal sealed record HelpCrop(string Title, (string Action, string Description)[] Steps,
    string Shortcuts, string SaveToDescription);

internal sealed record HelpMouse(string Title, string ActionHeader, (string Action, string Description)[] Rows);
