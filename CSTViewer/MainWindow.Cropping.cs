using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace CSTViewer;

public partial class MainWindow
{
    private readonly ImageCropSelection _cropSelection = new();
    private readonly List<Rectangle> _cropHandles = [];
    private bool _isCropBusy;
    private string? _cropStatus;
    private bool CanEditCrop => !_isClosed && !_isLoading && !_isChangingPage && !_isCropBusy && !IsDiffBusy && !IsSaveBusy
        && _currentPage is not null;

    private void InitializeCropOverlay()
    {
        CropSavePathTextBox.Text = CropSavePath.DefaultText;
        for (int index = 0; index < 8; index++)
        {
            var handle = new Rectangle
            {
                Width = ViewerSettings.CropHandleSize, Height = ViewerSettings.CropHandleSize,
                Fill = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                Stroke = new SolidColorBrush(Color.FromRgb(96, 205, 255)), StrokeThickness = 1
            };
            _cropHandles.Add(handle);
            CropSelectionOverlay.Children.Add(handle);
        }
    }

    internal bool BeginCropSelection(Point position)
    {
        if (!CanEditCrop) return false;
        StopPanning();
        ClearCropSelection();
        if (!_cropSelection.BeginSelection(position, _viewport)) return false;
        // キャプチャで画像領域の外へ出たドラッグとボタン解放も受け取る。
        if (!ImageViewportCanvas.CaptureMouse())
        {
            _cropSelection.CancelDrag();
            return false;
        }
        ImageViewportCanvas.Focus();
        ImageViewportCanvas.Cursor = Cursors.Cross;
        UpdateCropOverlay();
        return true;
    }

    internal bool BeginCropAdjustment(Point position)
    {
        if (!CanEditCrop || _cropSelection.IsDragging || _isPanning
            || !_cropSelection.BeginAdjustment(position, _viewport)) return false;
        if (!ImageViewportCanvas.CaptureMouse())
        {
            _cropSelection.CancelDrag();
            return false;
        }
        ImageViewportCanvas.Focus();
        _cropStatus = null;
        UpdateCropCursor(position);
        UpdateStatusText();
        return true;
    }

    internal void UpdateCropDrag(Point position)
    {
        if (!_cropSelection.IsDragging || !CanEditCrop) return;
        _cropSelection.Update(position, _viewport);
        UpdateCropOverlay();
        UpdateCropCursor(position);
    }

    internal bool CompleteCropDrag(Point position)
    {
        if (!_cropSelection.IsDragging) return false;
        bool save = _cropSelection.End(position, _viewport);
        ImageViewportCanvas.ReleaseMouseCapture();
        UpdateCropOverlay();
        UpdateCropCursor(position);
        return save;
    }

    internal async Task<bool> CompleteCropAdjustmentAsync(Point position, Action<DataObject>? writeClipboard = null)
    {
        bool save = CompleteCropDrag(position);
        return save && await SaveConfiguredCropAsync(writeClipboard);
    }

    internal void ClearCropSelection()
    {
        bool wasDragging = _cropSelection.IsDragging;
        _cropSelection.Clear();
        _cropStatus = null;
        if (wasDragging) ImageViewportCanvas.ReleaseMouseCapture();
        UpdateCropOverlay();
        if (!_isPanning) ImageViewportCanvas.Cursor = _viewport.HasImage ? Cursors.Hand : Cursors.Arrow;
    }

    private void CancelCropDrag()
    {
        if (!_cropSelection.IsDragging) return;
        _cropSelection.CancelDrag();
        ImageViewportCanvas.ReleaseMouseCapture();
        UpdateCropOverlay();
    }

    private void ImageViewportCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        BeginCropSelection(e.GetPosition(ImageViewportCanvas));
        e.Handled = true;
    }

    private void ImageViewportCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropSelection.Mode != CropHit.NewSelection) return;
        CompleteCropDrag(e.GetPosition(ImageViewportCanvas));
        e.Handled = true;
    }

    private void UpdateCropOverlay()
    {
        Rect rectangle = _viewport.ImageToView(_cropSelection.ImageRectangle);
        bool visible = _cropSelection.HasSelection;
        CropSelectionOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            Canvas.SetLeft(CropRubberBand, rectangle.Left);
            Canvas.SetTop(CropRubberBand, rectangle.Top);
            CropRubberBand.Width = rectangle.Width;
            CropRubberBand.Height = rectangle.Height;
            double middleX = rectangle.Left + rectangle.Width / 2;
            double middleY = rectangle.Top + rectangle.Height / 2;
            Point[] positions = [rectangle.TopLeft, new(middleX, rectangle.Top), rectangle.TopRight,
                new(rectangle.Right, middleY), rectangle.BottomRight, new(middleX, rectangle.Bottom),
                rectangle.BottomLeft, new(rectangle.Left, middleY)];
            for (int index = 0; index < _cropHandles.Count; index++)
            {
                Canvas.SetLeft(_cropHandles[index], positions[index].X - ViewerSettings.CropHandleSize / 2);
                Canvas.SetTop(_cropHandles[index], positions[index].Y - ViewerSettings.CropHandleSize / 2);
            }
        }
        UpdateStatusText();
    }

    private Int32Rect CurrentCropPixels() => _currentPage is { } page
        ? _cropSelection.PixelRectangle(new Size(page.SourceWidth, page.SourceHeight)) : Int32Rect.Empty;

    private void UpdateCropCursor(Point position)
    {
        CropHit hit = _cropSelection.IsDragging ? _cropSelection.Mode : _cropSelection.HitTest(position, _viewport);
        ImageViewportCanvas.Cursor = hit switch
        {
            CropHit.NewSelection => Cursors.Cross,
            CropHit.Left | CropHit.Top or CropHit.Right | CropHit.Bottom => Cursors.SizeNWSE,
            CropHit.Right | CropHit.Top or CropHit.Left | CropHit.Bottom => Cursors.SizeNESW,
            CropHit.Left or CropHit.Right => Cursors.SizeWE,
            CropHit.Top or CropHit.Bottom => Cursors.SizeNS,
            CropHit.Inside => _cropSelection.IsDragging ? Cursors.SizeAll : Cursors.Hand,
            _ => _viewport.HasImage ? Cursors.Hand : Cursors.Arrow
        };
    }

    internal Task<bool> SaveConfiguredCropAsync(Action<DataObject>? writeClipboard = null)
    {
        string text = CropSavePathTextBox.Text;
        return ExecuteCropAsync(async (session, page, rectangle) =>
        {
            string outputPath = CropSavePath.Resolve(session.Path, text);
            await session.SaveCropAsync(page.Index, rectangle, outputPath);
            string savedPaths = outputPath;
            CropPng png;
            try
            {
                png = await session.LoadCropPngAsync(page.Index, rectangle);
                if (png.SaveAlongsideTiff)
                {
                    string pngPath = Path.ChangeExtension(outputPath, ".png");
                    await ImageLoader.SavePngAsync(png.Bytes, pngPath, session.Token);
                    savedPaths += $" / {pngPath}";
                }
            }
            catch (Exception exception) when (IsImageLoadException(exception))
            {
                throw new IOException($"Saved in the source format: {outputPath}. Failed to create the PNG: {exception.Message}", exception);
            }
            try
            {
                await WriteCropClipboardAsync(png.Bytes, session.Token, writeClipboard);
                return $"Saved and copied: {savedPaths}";
            }
            catch (Exception exception) when (IsImageLoadException(exception)
                || exception is System.Runtime.InteropServices.ExternalException)
            {
                // コピーに失敗しても、保存済みのファイルと選択枠はそのまま使える。
                return $"Saved: {savedPaths} / Clipboard copy failed: {exception.Message}";
            }
        }, "Saving and copying selection…");
    }

    internal Task<bool> SaveCropAsync(string outputPath) => ExecuteCropAsync(async (session, page, rectangle) =>
    {
        await session.SaveCropAsync(page.Index, rectangle, outputPath);
        return $"Saved: {System.IO.Path.GetFileName(outputPath)}";
    }, "Saving selection…");

    internal Task<bool> CopyCropAsync(Action<DataObject>? writeClipboard = null) => ExecuteCropAsync(async (session, page, rectangle) =>
    {
        CropPng png = await session.LoadCropPngAsync(page.Index, rectangle);
        await WriteCropClipboardAsync(png.Bytes, session.Token, writeClipboard);
        return $"Copied: {rectangle.Width:N0} × {rectangle.Height:N0} px";
    }, "Copying selection…");

    private async Task WriteCropClipboardAsync(byte[] png, CancellationToken token, Action<DataObject>? writeClipboard)
    {
        BitmapSource bitmap = await Task.Run(() =>
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }, token);
        token.ThrowIfCancellationRequested();
        if (_isClosed) throw new OperationCanceledException();
        // PNG形式は透明度を保ち、Bitmap形式も併記して貼り付け先の互換性を確保する。
        var data = new DataObject();
        data.SetImage(bitmap);
        using var pngStream = new MemoryStream(png);
        data.SetData("PNG", pngStream);
        // テストでは書き込み先を差し替え、利用者のクリップボードを変更せず内容を検証する。
        if (writeClipboard is null) Clipboard.SetDataObject(data, copy: true);
        else writeClipboard(data);
    }

    private async Task<bool> ExecuteCropAsync(Func<ImageLoadSession, ImagePage, Int32Rect, Task<string>> operation,
        string message)
    {
        if (!CanEditCrop || !_cropSelection.HasSelection || _cropSelection.IsDragging
            || _session is not { } session || _currentPage is not { } page) return false;
        Int32Rect rectangle = CurrentCropPixels();
        StopPanning();
        _isCropBusy = true;
        _saveStatus = null;
        _diffStatus = null;
        _detailLoadTimer?.Stop();
        _cropStatus = message;
        UpdateCropBusyState();
        try
        {
            _cropStatus = await operation(session, page, rectangle);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (IsImageLoadException(exception)
            || exception is System.Runtime.InteropServices.ExternalException)
        {
            _cropStatus = $"Crop failed: {exception.Message}";
            return false;
        }
        finally
        {
            _isCropBusy = false;
            if (!_isClosed)
            {
                UpdateCropBusyState();
                ScheduleDetailLoad();
            }
        }
    }

    private void UpdateCropBusyState()
    {
        CropSavePathTextBox.IsEnabled = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy;
        OpenImageButton.IsEnabled = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy;
        ImageViewportCanvas.IsEnabled = !_isLoading && !_isCropBusy && !IsDiffBusy && !IsSaveBusy;
        // 読み込み中は待機カーソルだけにし、表示中の画像を覆って点滅させない。
        ImageViewportBorder.Cursor = _isLoading ? Cursors.Wait : null;
        ImageLoadingText.Text = IsSaveBusy ? "Saving file to desktop…" : _isCropBusy ? _cropStatus : string.Empty;
        LoadingOverlay.Visibility = _isCropBusy || IsSaveBusy ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageControls();
    }
}
