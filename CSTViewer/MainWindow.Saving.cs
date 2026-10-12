using System.IO;
using System.Windows;

namespace CSTViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _saveCancellation;
    private string? _saveStatus;
    private bool IsSaveBusy => _saveCancellation is not null;

    private async void SaveImageButton_Click(object sender, RoutedEventArgs e) => await SaveCurrentFileToDesktopAsync();

    internal async Task<bool> SaveCurrentFileToDesktopAsync(string? desktopDirectory = null,
        Func<string, bool>? confirmOverwrite = null, Action<string>? showError = null)
    {
        if (IsSpreadBusy || _spreadController.IsChangingImages(this) || _session is not { } session
            || _currentPage is null) return false;

        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        StopPanning();
        CancelCropDrag();
        _detailLoadTimer?.Stop();
        ShowSaveStatus("Saving file to desktop…");
        UpdateCropBusyState();
        string? target = null;
        try
        {
            target = DesktopFileSave.TargetPath(session.Path, desktopDirectory);
            bool overwrite = File.Exists(target);
            if (overwrite)
            {
                bool confirmed = confirmOverwrite?.Invoke(target) ?? MessageBox.Show(this,
                    $"A file with the same name already exists on the desktop.\nOverwrite it?\n\n{target}",
                    "Save", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!confirmed)
                {
                    ShowSaveStatus($"Save cancelled: {target}");
                    return false;
                }
            }
            string saved = await DesktopFileSave.CopyAsync(session.Path, target, overwrite, cancellation.Token);
            if (_isClosed) return false;
            ShowSaveStatus($"Saved to desktop: {saved}");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            if (!_isClosed)
            {
                ShowSaveStatus($"Save failed: {exception.Message}");
                string message = $"Failed to save the file to the desktop.\n\nSource:\n{session.Path}\n\nDestination:\n{target}\n\n{exception.Message}";
                if (showError is not null) showError(message);
                else MessageBox.Show(this, message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
        finally
        {
            _saveCancellation = null;
            if (!_isClosed)
            {
                UpdateCropBusyState();
                ScheduleDetailLoad();
            }
        }
    }

    private void ShowSaveStatus(string message)
    {
        _cropStatus = _spreadStatus = _diffStatus = null;
        _saveStatus = message;
        UpdateStatusText();
    }
}
