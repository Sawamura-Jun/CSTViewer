using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace CSTViewer;

public partial class MainWindow
{
    private CancellationTokenSource? _saveCancellation;
    private string? _saveStatus;
    private bool IsSaveBusy => _saveCancellation is not null;

    private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) await SaveCurrentFileAsAsync();
        else await SaveCurrentFileToDesktopAsync();
    }

    internal Task<bool> SaveCurrentFileToDesktopAsync(string? desktopDirectory = null,
        Func<string, bool>? confirmOverwrite = null, Action<string>? showError = null) =>
        SaveCurrentFileAsync(source => DesktopFileSave.TargetPath(source, desktopDirectory), confirmOverwrite, showError);

    internal Task<bool> SaveCurrentFileAsAsync(Func<SaveFileDialog, bool?>? showDialog = null) => SaveCurrentFileAsync(source =>
    {
        string extension = Path.GetExtension(source);
        var dialog = new SaveFileDialog
        {
            Title = "Save original image",
            InitialDirectory = DesktopFileSave.DefaultDirectory,
            FileName = Path.GetFileName(source),
            DefaultExt = extension,
            Filter = $"Original image (*{extension})|*{extension}",
            // ダイアログで保存先を選んだ後、通常の保存と同じ上書き確認を行う。
            OverwritePrompt = false
        };
        bool? accepted = showDialog is null ? dialog.ShowDialog(this) : showDialog(dialog);
        return accepted == true ? dialog.FileName : null;
    });

    private async Task<bool> SaveCurrentFileAsync(Func<string, string?> selectTarget,
        Func<string, bool>? confirmOverwrite = null, Action<string>? showError = null)
    {
        if (IsSpreadBusy || _spreadController.IsChangingImages(this) || _session is not { } session
            || _currentPage is null) return false;

        using var cancellation = new CancellationTokenSource();
        _saveCancellation = cancellation;
        StopPanning();
        CancelCropDrag();
        _detailLoadTimer?.Stop();
        ShowSaveStatus("Saving file…");
        UpdateCropBusyState();
        string? target = null;
        try
        {
            target = selectTarget(session.Path);
            if (cancellation.IsCancellationRequested) return false;
            if (target is null)
            {
                ShowSaveStatus("Save cancelled.");
                return false;
            }
            bool overwrite = File.Exists(target);
            if (overwrite)
            {
                bool confirmed = confirmOverwrite?.Invoke(target) ?? MessageBox.Show(this,
                    $"A file with the same name already exists at the save location.\nOverwrite it?\n\n{target}",
                    "Save", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!confirmed)
                {
                    ShowSaveStatus($"Save cancelled: {target}");
                    return false;
                }
            }
            string saved = await DesktopFileSave.CopyAsync(session.Path, target, overwrite, cancellation.Token);
            if (_isClosed) return false;
            ShowSaveStatus($"Saved: {saved}");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (IsImageLoadException(exception))
        {
            if (!_isClosed)
            {
                ShowSaveStatus($"Save failed: {exception.Message}");
                string message = $"Failed to save the file.\n\nSource:\n{session.Path}\n\nDestination:\n{target}\n\n{exception.Message}";
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
