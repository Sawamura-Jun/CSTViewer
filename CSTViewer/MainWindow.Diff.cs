using System.IO;
using System.Windows;

namespace CSTViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _diffCancellation;
    private string? _diffStatus;
    private bool IsDiffBusy => _diffCancellation is not null;
    internal string? DiffImagePath => !_isClosed && _currentPage is not null ? _session?.Path : null;

    private async void DiffImageButton_Click(object sender, RoutedEventArgs e) => await CreateDiffAsync();

    internal async Task<MainWindow?> CreateDiffAsync()
    {
        if (IsDiffBusy || IsSpreadBusy) return null;
        if (DiffImagePath is not { } newPath)
        {
            ShowDiffStatus("Cannot compare images: no new image is open");
            return null;
        }
        if (_spreadController.NearestImageWindow(this)?.DiffImagePath is not { } oldPath)
        {
            ShowDiffStatus("Open the old image in another visible window to compare images");
            return null;
        }
        string target;
        try
        {
            target = DiffSavePath.Resolve(newPath, CropSavePathTextBox.Text);
            DiffSavePath.Validate(oldPath, newPath, target);
        }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            ShowDiffStatus($"Diff failed: invalid save location ({exception.Message})");
            return null;
        }

        using var cancellation = new CancellationTokenSource();
        _diffCancellation = cancellation;
        StopPanning();
        CancelCropDrag();
        _detailLoadTimer?.Stop();
        DiffProgressBar.Value = 0;
        DiffProgressBar.Visibility = Visibility.Visible;
        ShowDiffStatus($"Comparing images: old={Path.GetFileName(oldPath)} / new={Path.GetFileName(newPath)}");
        UpdateCropBusyState();
        // Progressは生成したUIスレッドのDispatcherへ通知を戻す。
        var progress = new Progress<DiffProgress>(update =>
        {
            if (_isClosed || _diffCancellation != cancellation) return;
            DiffProgressBar.Value = Math.Max(DiffProgressBar.Value, update.Percent);
            ShowDiffStatus($"Comparing images ({DiffProgressBar.Value:0}%): {update.Stage}");
        });
        string saved;
        try
        {
            saved = await ImageDiff.CreateAsync(oldPath, newPath, target, progress, cancellation.Token);
            if (_isClosed) return null;
            ShowDiffStatus($"Diff saved: {saved}");
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception) when (IsImageLoadException(exception) || exception is OverflowException)
        {
            if (!_isClosed)
            {
                ShowDiffStatus($"Diff failed: {exception.Message}");
                MessageBox.Show(this, $"Failed to save the diff image.\n\n{target}\n\n{exception.Message}",
                    "Diff failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return null;
        }
        finally
        {
            _diffCancellation = null;
            if (!_isClosed)
            {
                DiffProgressBar.Visibility = Visibility.Collapsed;
                UpdateCropBusyState();
                ScheduleDetailLoad();
            }
        }
        var result = CreateNewWindow();
        await result.OpenImageAsync(saved);
        return result;
    }

    private void ShowDiffStatus(string message)
    {
        _saveStatus = null;
        _cropStatus = _spreadStatus = null;
        _diffStatus = message;
        UpdateStatusText();
    }
}
