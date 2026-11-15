using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace CSTViewer;

public partial class OcrResultWindow : Window
{
    private readonly FileStream _preparedTiff;
    private readonly string _defaultFileName;
    private CancellationTokenSource? _saveCancellation;
    private bool _isClosed;

    internal OcrResultWindow(MainWindow owner, string path, OcrResult result)
    {
        InitializeComponent();
        Owner = owner;
        Title = $"OCR results — {Path.GetFileName(path)}";
        OcrResultTextBox.Text = FormatPages(result.Pages);
        _defaultFileName = $"{Path.GetFileNameWithoutExtension(path)}{OcrSearchSettings.LineArtRemovalFileSuffix}.tif";
        _preparedTiff = result.OpenPreparedTiff();
    }

    private async void LineArtRemovalImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saveCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        LineArtRemovalImageButton.IsEnabled = false;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = OcrSearchSettings.LineArtRemovalButtonText,
                InitialDirectory = DesktopFileSave.DefaultDirectory,
                FileName = _defaultFileName,
                Filter = "TIFF image (*.tif;*.tiff)|*.tif;*.tiff",
                DefaultExt = ".tif",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true || cancellation.IsCancellationRequested) return;

            LineArtRemovalSaveStatus.Text = "Saving TIFF…";
            LineArtRemovalSaveStatus.ToolTip = null;
            // 何度でも保存できるよう、OCRに渡したTIFFを毎回先頭からコピーする。
            _preparedTiff.Position = 0;
            string saved = await DesktopFileSave.CopyAsync(_preparedTiff, dialog.FileName, overwrite: true, cancellation.Token);
            if (_isClosed) return;
            LineArtRemovalSaveStatus.Text = $"Saved: {Path.GetFileName(saved)}";
            LineArtRemovalSaveStatus.ToolTip = saved;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (_isClosed) return;
            LineArtRemovalSaveStatus.Text = "Save failed";
            LineArtRemovalSaveStatus.ToolTip = null;
            MessageBox.Show(this, exception.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _saveCancellation = null;
            if (_isClosed) _preparedTiff.Dispose();
            else LineArtRemovalImageButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        // コピー中ならキャンセルし、処理終了後に読み取りハンドルを閉じる。
        if (_saveCancellation is { } cancellation) cancellation.Cancel();
        else _preparedTiff.Dispose();
        base.OnClosed(e);
    }

    internal static string FormatPages(IReadOnlyList<OcrSearchPage> pages)
    {
        var text = new StringBuilder();
        for (int index = 0; index < pages.Count; index++)
        {
            if (index > 0) text.AppendLine();
            text.AppendLine($"Page {index + 1} / {pages.Count}");
            int pageStart = text.Length;
            foreach (var line in pages[index].Lines)
            {
                if (line.Count == 0) continue;
                text.Append(line[0].Text);
                for (int word = 1; word < line.Count; word++)
                {
                    if (OcrTextSearch.NeedsWordSeparator(text[^1], line[word].Text[0])) text.Append(' ');
                    text.Append(line[word].Text);
                }
                text.AppendLine();
            }
            if (text.Length == pageStart) text.AppendLine("No text recognized");
        }
        return text.ToString();
    }
}
