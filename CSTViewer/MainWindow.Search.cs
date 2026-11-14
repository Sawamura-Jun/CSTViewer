using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CSTViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _searchCancellation;
    private Rect[][]? _searchMatches;
    private string? _searchStatus;

    private void UpdateSearchControls()
    {
        bool canSearch = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy &&
            _session is { } session && ImageLoader.IsTiff(session.Path);
        SearchTextBox.IsEnabled = canSearch;
        SearchTextBox.IsReadOnly = _searchCancellation is not null;
        SearchButton.IsEnabled = canSearch && _searchCancellation is null && !string.IsNullOrWhiteSpace(SearchTextBox.Text);
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchTiffAsync();

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

    private async Task SearchTiffAsync()
    {
        if (!SearchButton.IsEnabled || _session is not { } session) return;
        string query = SearchTextBox.Text;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        _searchCancellation = cancellation;
        _searchMatches = null;
        _saveStatus = _diffStatus = _cropStatus = _spreadStatus = null;
        _searchStatus = "Searching TIFF text…";
        UpdateSearchControls();
        UpdateSearchHighlights();
        UpdateStatusText();
        ImageViewportCanvas.Focus();
        try
        {
            Rect[][] matches = await TiffSearch.FindMatchesAsync(session.Path, session.PageCount, query, cancellation.Token);
            if (_isClosed || _session != session || _searchCancellation != cancellation) return;
            _searchMatches = matches;
            _searchStatus = $"Matches: {matches.Sum(page => page.Length):N0} · Pages with matches: {matches.Count(page => page.Length != 0):N0}";
            UpdateSearchHighlights();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (IsImageLoadException(exception) || exception is InvalidDataException
            or Win32Exception or OpenCvSharp.OpenCVException or EntryPointNotFoundException or BadImageFormatException or OverflowException)
        {
            if (!_isClosed && _session == session && _searchCancellation == cancellation)
                _searchStatus = $"Search failed: {exception.Message}";
        }
        finally
        {
            if (_searchCancellation == cancellation)
            {
                _searchCancellation = null;
                UpdateSearchControls();
                UpdateStatusText();
            }
        }
    }

    private void ResetTiffSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        _searchMatches = null;
        _searchStatus = null;
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
