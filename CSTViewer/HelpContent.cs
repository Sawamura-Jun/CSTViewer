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
                (nameof(MainWindow.OpenImageButton), "Open", "—",
                    "Choose a TIFF, PNG, JPEG, BMP, GIF, or WebP image. Animated GIF and WebP files display the first frame."),
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
                (nameof(MainWindow.SearchButton), "Search TIFF text", "Enter in search box",
                    "Type a query and search all pages of the open TIFF. Shaded red areas mark matches on the current page. Use the page controls to view other pages. The status bar shows progress while recognizing text. Searches reuse the recognized text while the TIFF stays open and unchanged."),
                (nameof(MainWindow.OcrButton), "OCR results", "—",
                    "Open the recognized text from every TIFF page in a separate window. Search and the OCR window share the recognition results from the Tesseract engine bundled with CSTViewer."),
                (nameof(MainWindow.CropSaveDestinationButton), "Save to", "—",
                    "Open the save location window for crops and comparison images."),
                (nameof(MainWindow.HelpButton), "Help", "—", "Open this help window.")
            ]),
            new("Crop",
            [
                ("Select", "Right-drag on the image to select the area to crop."),
                ("Adjust", "Left-drag inside the selection to move it, or drag an edge or corner to resize it."),
                ("Save", "Left-click inside the selection to save the crop and copy it to the clipboard. WebP crops are saved as lossless WebP.")
            ],
                "Press Ctrl+C to copy the selection to the clipboard, or Esc to clear it.",
                " — Click the crop icon to open a window where you can enter a folder path or filename. Select OK to apply the change. Leave the field blank to use the source folder and an automatic filename."),
            new("Mouse controls", "Action",
            [
                ("Mouse wheel", "Zoom around the pointer. For TIFFs, the initial page image is used during zoom. To keep the image visible when zooming starts, right-click the CSTViewer notification area icon and select Keep detail while zooming TIFFs. The setting applies until CSTViewer exits."),
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
                (nameof(MainWindow.OpenImageButton), "開く", "—",
                    "TIFF、PNG、JPEG、BMP、GIF、WebPの画像を選びます。アニメーションGIFとWebPは先頭フレームを表示します。"),
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
                (nameof(MainWindow.SearchButton), "TIFFの文字検索", "検索欄で Enter",
                    "検索語を入力し、開いているTIFFの全ページを検索します。表示中のページの一致箇所を半透明の赤で塗りつぶします。他のページはページ移動ボタンで確認できます。文字認識中はステータスバーに進捗を表示します。同じTIFFを開いていてファイルに変更がない間は、認識結果を再利用します。"),
                (nameof(MainWindow.OcrButton), "OCR結果", "—",
                    "TIFFの全ページから認識した文字を別ウィンドウに表示します。検索とOCRウィンドウは、CSTViewerに同梱のTesseractで得た認識結果を共有します。"),
                (nameof(MainWindow.CropSaveDestinationButton), "保存先（Save to）", "—",
                    "切り抜きと差分画像の保存先を設定するウィンドウを開きます。"),
                (nameof(MainWindow.HelpButton), "ヘルプ", "—", "このヘルプウィンドウを開きます。")
            ]),
            new("切り抜き（CROP）",
            [
                ("選ぶ", "画像を右ドラッグして、切り抜く範囲を選びます。"),
                ("調整する", "選択枠の内側を左ドラッグすると移動し、枠の辺や角を左ドラッグするとサイズが変わります。"),
                ("保存する", "選択枠の内側を左クリックすると、切り抜きを保存し、クリップボードにもコピーします。WebPの切り抜きは可逆圧縮のWebPで保存します。")
            ],
                "Ctrl+C でクリップボードにコピーし、Esc で選択を解除します。",
                " のクロップアイコンを押し、開いたウィンドウにフォルダーのパスかファイル名を入力して OK を押します。空欄にすると、元画像と同じフォルダーに自動で付けた名前で保存します。"),
            new("マウス操作", "操作",
            [
                ("ホイール", "ポインターの位置を中心に拡大・縮小します。TIFFのズーム中は最初に表示したページ画像を使います。ズーム開始時に表示していた画像を使うには、通知領域のCSTViewerアイコンを右クリックして「Keep detail while zooming TIFFs」にチェックを入れます。設定はCSTViewerを終了するまで有効です。"),
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
