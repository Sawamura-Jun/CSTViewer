using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class SaveInteractionTests
{
    [Fact]
    public Task Save_CopiesAllPagesWithoutChangingViewOrUsingCropDestination() => RunOnStaAsync(async () =>
    {
        string directory = ImageDiffTests.CreateDirectory();
        var window = Window();
        try
        {
            var button = (Button)window.FindName("SaveImageButton");
            Assert.False(button.IsEnabled);
            Assert.False(await window.SaveCurrentFileToDesktopAsync(directory));
            string source = Path.Combine(directory, "複数ページ 画像.tif");
            ImageDiffTests.SavePages(source, (2, 1, [0, 0, 0, 255, 255, 255]), (1, 2, [255, 255, 255, 0, 0, 0]));
            string desktop = Directory.CreateDirectory(Path.Combine(directory, "Desktop")).FullName;
            window.Show();
            await window.OpenImageAsync(source);
            Assert.True(button.IsEnabled);
            await window.ChangePageAsync(1);
            window.HandleViewportWheel(new Point(150, 150), 120, System.Windows.Input.ModifierKeys.None);
            string title = window.Title;
            var image = (Image)window.FindName("DisplayedImage");
            var bitmap = image.Source;
            Matrix matrix = ((MatrixTransform)image.RenderTransform).Matrix;
            ((TextBox)window.FindName("CropSavePathTextBox")).Text = Path.Combine(directory, "unused.tif");

            Assert.True(await window.SaveCurrentFileToDesktopAsync(desktop));
            string target = Path.Combine(desktop, Path.GetFileName(source));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(target));
            Assert.Equal(source, window.DiffImagePath);
            Assert.Equal(title, window.Title);
            Assert.Same(bitmap, image.Source);
            Assert.Equal(matrix, ((MatrixTransform)image.RenderTransform).Matrix);
            Assert.False(File.Exists(Path.Combine(directory, "unused.tif")));
            Assert.Contains(target, ((TextBlock)window.FindName("PreloadStatusText")).Text);
            Assert.True(button.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility);
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Overwrite_CancelAndConfirmBlockReentryAndRestoreControls() => RunOnStaAsync(async () =>
    {
        string directory = ImageDiffTests.CreateDirectory();
        var window = Window();
        try
        {
            string source = Path.Combine(directory, "source.tif");
            ImageDiffTests.SavePages(source, (2, 1, [0, 0, 0, 255, 255, 255]));
            string desktop = Directory.CreateDirectory(Path.Combine(directory, "Desktop")).FullName;
            string target = Path.Combine(desktop, "source.tif");
            await File.WriteAllBytesAsync(target, [4, 5, 6]);
            window.Show();
            await window.OpenImageAsync(source);
            Assert.False(await window.SaveCurrentFileToDesktopAsync(desktop, path =>
            {
                Assert.Equal(target, path);
                Assert.True(window.IsSpreadBusy);
                foreach (string name in new[] { "SaveImageButton", "OpenImageButton", "FitImageButton", "DiffImageButton" })
                    Assert.False(((Button)window.FindName(name)).IsEnabled);
                Assert.False(((TextBox)window.FindName("CropSavePathTextBox")).IsEnabled);
                Assert.False(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility);
                // 確認ダイアログ中の再入と別画像の読み込みも、UIスレッド上で拒否する。
                Assert.False(window.SaveCurrentFileToDesktopAsync(desktop).GetAwaiter().GetResult());
                Assert.Null(window.CreateDiffAsync().GetAwaiter().GetResult());
                window.OpenImageAsync(target).GetAwaiter().GetResult();
                Assert.Equal(source, window.DiffImagePath);
                return false;
            }));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(target));
            Assert.Contains("cancelled", ((TextBlock)window.FindName("PreloadStatusText")).Text);
            Assert.True(((Button)window.FindName("SaveImageButton")).IsEnabled);
            Assert.True(((Button)window.FindName("OpenImageButton")).IsEnabled);
            Assert.False(window.IsSpreadBusy);

            Assert.True(await window.SaveCurrentFileToDesktopAsync(desktop, _ => true));
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(target));
            Assert.True(await window.SaveCurrentFileToDesktopAsync(directory, _ => true));
            Assert.Equal(source, window.DiffImagePath);
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task SaveFailure_ReportsErrorAndRestoresControls() => RunOnStaAsync(async () =>
    {
        string directory = ImageDiffTests.CreateDirectory();
        var window = Window();
        try
        {
            string source = Path.Combine(directory, "source.tif");
            ImageDiffTests.SavePages(source, (2, 1, [0, 0, 0, 255, 255, 255]));
            string desktop = Directory.CreateDirectory(Path.Combine(directory, "Desktop")).FullName;
            Directory.CreateDirectory(Path.Combine(desktop, "source.tif"));
            window.Show();
            await window.OpenImageAsync(source);
            string? message = null;
            Assert.False(await window.SaveCurrentFileToDesktopAsync(desktop, showError: error => message = error));
            Assert.Contains(source, message);
            Assert.Contains("Save failed", ((TextBlock)window.FindName("PreloadStatusText")).Text);
            Assert.True(((Button)window.FindName("SaveImageButton")).IsEnabled);
            Assert.True(((Button)window.FindName("OpenImageButton")).IsEnabled);
            Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility);
            Assert.Empty(Directory.GetFiles(desktop, ".CSTViewer-save-*.tmp"));
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    });

    private static MainWindow Window() => new(Path.Combine(Path.GetTempPath(), $"CSTViewer-save-{Guid.NewGuid():N}.jsonl"))
        { ShowActivated = false };

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // 実画面と同じSTA Dispatcherで、保存の完了を待ちながらUIを動かす。
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
