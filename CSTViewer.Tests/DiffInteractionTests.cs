using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class DiffInteractionTests
{
    [Fact]
    public Task CreateDiff_DisablesRepeatedExecutionAndOpensSavedResult() => RunOnStaAsync(async () =>
    {
        string directory = ImageDiffTests.CreateDirectory();
        var controller = new SpreadController();
        MainWindow oldWindow = Window(controller), newWindow = Window(controller);
        MainWindow? result = null;
        try
        {
            string oldPath = Path.Combine(directory, "old.tif"), newPath = Path.Combine(directory, "new.tif");
            ImageDiffTests.SavePages(oldPath, (2, 1, [0, 0, 0, 255, 255, 255]));
            ImageDiffTests.SavePages(newPath, (2, 1, [255, 255, 255, 0, 0, 0]));
            oldWindow.Show(); newWindow.Show();
            await oldWindow.OpenImageAsync(oldPath);
            await newWindow.OpenImageAsync(newPath);
            string target = Path.Combine(directory, "new_dif.tif");
            var textbox = (TextBox)newWindow.FindName("CropSavePathTextBox");
            textbox.Text = "";
            var button = (Button)newWindow.FindName("DiffImageButton");
            Assert.True(button.IsEnabled);
            Task<MainWindow?> creating = newWindow.CreateDiffAsync();
            Assert.False(button.IsEnabled);
            Assert.False(textbox.IsEnabled);
            Assert.False(((Button)newWindow.FindName("OpenImageButton")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((ProgressBar)newWindow.FindName("DiffProgressBar")).Visibility);
            Assert.Null(await newWindow.CreateDiffAsync());
            await newWindow.OpenImageAsync(oldPath);
            Assert.Equal(newPath, newWindow.DiffImagePath);
            result = await creating;
            Assert.NotNull(result);
            Assert.True(result.IsVisible);
            Assert.Equal(target, result.DiffImagePath);
            Assert.True(button.IsEnabled);
            Assert.True(textbox.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((ProgressBar)newWindow.FindName("DiffProgressBar")).Visibility);
            Assert.Contains("Diff saved", ((TextBlock)newWindow.FindName("PreloadStatusText")).Text);
        }
        finally { result?.Close(); oldWindow.Close(); newWindow.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task Selection_SkipsHiddenMinimizedAndEmptyWindowsAndTextboxKeepsDKey() => RunOnStaAsync(async () =>
    {
        string directory = ImageDiffTests.CreateDirectory();
        var controller = new SpreadController();
        MainWindow source = Window(controller), nearest = Window(controller), farther = Window(controller), empty = Window(controller);
        try
        {
            Assert.Null(await source.CreateDiffAsync());
            string path = Path.Combine(directory, "source.tif");
            ImageDiffTests.SavePages(path, (2, 1, [0, 0, 0, 255, 255, 255]));
            foreach (var window in new[] { source, nearest, farther, empty })
            {
                window.Show();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Top = source.Top;
            }
            source.Left = 10; nearest.Left = 30; farther.Left = 100; empty.Left = 10;
            await source.OpenImageAsync(path);
            Assert.Null(await source.CreateDiffAsync());
            Assert.Contains("another visible window", ((TextBlock)source.FindName("PreloadStatusText")).Text);
            await nearest.OpenImageAsync(path);
            await farther.OpenImageAsync(path);
            Assert.Same(nearest, controller.NearestImageWindow(source));
            nearest.WindowState = WindowState.Minimized;
            Assert.Same(farther, controller.NearestImageWindow(source));
            nearest.WindowState = WindowState.Normal;
            nearest.Hide();
            Assert.Same(farther, controller.NearestImageWindow(source));

            var textbox = (TextBox)source.FindName("CropSavePathTextBox");
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, Key.D)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = textbox };
            source.RaiseEvent(args);
            Assert.False(args.Handled);
            textbox.Text = path.ToUpperInvariant();
            Assert.Null(await source.CreateDiffAsync());
            Assert.Contains("same file", ((TextBlock)source.FindName("PreloadStatusText")).Text);
        }
        finally
        {
            source.Close(); nearest.Close(); farther.Close(); empty.Close();
            Directory.Delete(directory, true);
        }
    });

    private static MainWindow Window(SpreadController controller) => new(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-diff-{Guid.NewGuid():N}.jsonl"), spreadController: controller)
    { ShowActivated = false };

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // 非同期保存と入力処理を、実画面と同じSTA Dispatcherで検証する。
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
