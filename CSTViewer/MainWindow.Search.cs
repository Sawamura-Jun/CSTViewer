using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _ocrCancellation;
    private Rect[][]? _searchMatches;
    private string? _searchStatus;
    private string? _ocrStatus;
    private string? _ocrProgressStatus;

    private void UpdateSearchControls()
    {
        bool hasTiff = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy &&
            _session is { } session && ImageLoader.IsTiff(session.Path);
        SearchTextBox.IsEnabled = hasTiff;
        SearchTextBox.IsReadOnly = _ocrCancellation is not null;
        OcrButton.IsEnabled = hasTiff && _ocrCancellation is null;
        SearchButton.IsEnabled = OcrButton.IsEnabled && !string.IsNullOrWhiteSpace(SearchTextBox.Text);
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchTiffAsync();

    private async void OcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (OcrButton.IsEnabled) await RunTiffOcrAsync(query: null);
    }

    private async void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (!e.IsRepeat) await SearchTiffAsync();
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchMatches is not null || _searchStatus is not null)
        {
            _searchMatches = null;
            _searchStatus = null;
            UpdateSearchHighlights();
            UpdateStatusText();
        }
        UpdateSearchControls();
    }

    private Task SearchTiffAsync() =>
        SearchButton.IsEnabled ? RunTiffOcrAsync(SearchTextBox.Text) : Task.CompletedTask;

    private async Task RunTiffOcrAsync(string? query)
    {
        if (_session is not { } session || _ocrCancellation is not null) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        _ocrCancellation = cancellation;
        _saveStatus = _diffStatus = _cropStatus = _spreadStatus = null;
        _ocrStatus = null;
        if (query is not null)
        {
            _searchMatches = null;
            _searchStatus = null;
            UpdateSearchHighlights();
        }
        string operation = query is null ? "OCR" : "Search";
        _ocrProgressStatus = query is null ? "Opening OCR results…" : "Searching TIFF text…";
        UpdateSearchControls();
        UpdateStatusText();
        ImageViewportCanvas.Focus();
        // 前処理中は完了ページ数を表示し、Tesseractの認識中はアニメーションで処理中を示す。
        var preparedPages = new Progress<int>(count =>
        {
            if (!IsCurrentOcr(session, cancellation)) return;
            bool recognizing = count == session.PageCount;
            OcrProgressBar.Value = count;
            OcrProgressBar.IsIndeterminate = recognizing;
            _ocrProgressStatus = recognizing ? $"{operation} · Recognizing text…"
                : $"{operation} · Preparing pages: {count:N0} / {session.PageCount:N0}";
            UpdateStatusText();
        });
        try
        {
            OcrSearchPage[] pages = await session.GetOcrPagesAsync(cancellation.Token, preparedPages, () =>
            {
                if (query is null)
                {
                    _searchMatches = null;
                    _searchStatus = null;
                    UpdateSearchHighlights();
                    _ocrProgressStatus = "Running OCR…";
                }
                OcrProgressBar.Maximum = session.PageCount;
                OcrProgressBar.Value = 0;
                OcrProgressBar.IsIndeterminate = true;
                OcrProgressBar.Visibility = Visibility.Visible;
                UpdateStatusText();
            });
            if (!IsCurrentOcr(session, cancellation)) return;
            if (query is null)
            {
                new OcrResultWindow(this, session.Path, pages).Show();
                _ocrStatus = $"OCR complete · Pages: {pages.Length:N0}";
            }
            else
            {
                Rect[][] matches = await Task.Run(() => OcrTextSearch.FindMatches(pages, query, cancellation.Token),
                    cancellation.Token);
                if (!IsCurrentOcr(session, cancellation)) return;
                _searchMatches = matches;
                _searchStatus = $"Matches: {matches.Sum(page => page.Length):N0} · Pages with matches: {matches.Count(page => page.Length != 0):N0}";
                UpdateSearchHighlights();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsImageLoadException(exception) || exception is InvalidDataException
            or Win32Exception or OpenCvSharp.OpenCVException or EntryPointNotFoundException or BadImageFormatException or OverflowException)
        {
            if (IsCurrentOcr(session, cancellation))
            {
                if (query is null) _ocrStatus = $"OCR failed: {exception.Message}";
                else _searchStatus = $"Search failed: {exception.Message}";
            }
        }
        finally
        {
            if (_ocrCancellation == cancellation)
            {
                ClearOcrProgress();
                UpdateSearchControls();
                UpdateStatusText();
            }
        }
    }

    private bool IsCurrentOcr(ImageLoadSession session, CancellationTokenSource cancellation) =>
        !_isClosed && _session == session && _ocrCancellation == cancellation && !cancellation.IsCancellationRequested;

    private void ClearOcrProgress()
    {
        _ocrCancellation = null;
        _ocrProgressStatus = null;
        OcrProgressBar.Visibility = Visibility.Collapsed;
        OcrProgressBar.IsIndeterminate = false;
    }

    private void ResetTiffOcr()
    {
        _ocrCancellation?.Cancel();
        ClearOcrProgress();
        _searchMatches = null;
        _searchStatus = null;
        _ocrStatus = null;
        UpdateSearchHighlights();
    }

    private void UpdateSearchHighlights()
    {
        if (_currentPage is not { } page || _searchMatches is not { } matches || matches[page.Index].Length == 0)
        {
            SearchHighlightOverlay.Data = null;
            return;
        }

        // 矩形が重なっても、重なった部分を同じ濃さで塗る。
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (StreamGeometryContext drawing = geometry.Open())
        {
            Rect visibleBounds = _viewport.VisibleSourceBounds;
            foreach (Rect match in matches[page.Index])
            {
                var sourceBounds = new Rect(match.X * page.SourceWidth, match.Y * page.SourceHeight,
                    match.Width * page.SourceWidth, match.Height * page.SourceHeight);
                if (!sourceBounds.IntersectsWith(visibleBounds)) continue;
                Rect bounds = _viewport.ImageToView(sourceBounds);
                drawing.BeginFigure(bounds.TopLeft, isFilled: true, isClosed: true);
                drawing.LineTo(bounds.TopRight, isStroked: false, isSmoothJoin: false);
                drawing.LineTo(bounds.BottomRight, isStroked: false, isSmoothJoin: false);
                drawing.LineTo(bounds.BottomLeft, isStroked: false, isSmoothJoin: false);
            }
        }
        geometry.Freeze();
        SearchHighlightOverlay.Data = geometry;
    }
}
