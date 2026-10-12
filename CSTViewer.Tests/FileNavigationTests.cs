using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class FileNavigationTests
{
    [Fact]
    public void DirectoryList_UsesCaseInsensitiveFilenameOrderAndOnlyTopLevelSupportedImages()
    {
        string directory = CreateDirectory();
        try
        {
            string[] names = ["10.tif", "2.tiff", "A.tif", "b.TIFF", "c.PNG", "d.jpg", "e.JPEG", "f.bmp", "g.GIF"];
            foreach (string name in names.Concat(["notes.txt", "image.webp"]))
                File.WriteAllBytes(Path.Combine(directory, name), []);
            Directory.CreateDirectory(Path.Combine(directory, "folder.tif"));
            string child = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
            File.WriteAllBytes(Path.Combine(child, "11.tif"), []);
            File.WriteAllBytes(Path.Combine(child, "12.png"), []);

            // 数値の自然順ではなく、Python版と同じ10→2→A→bの名前順で移動する。
            var images = DirectoryImageList.Create(Path.Combine(directory, "2.TIFF"));
            Assert.Equal(Path.Combine(directory, "10.tif"), images.GetNeighbor(-1));
            Assert.Equal(Path.Combine(directory, "A.tif"), images.GetNeighbor(1));
            images = DirectoryImageList.Create(Path.Combine(directory, "A.tif"));
            Assert.Equal(Path.Combine(directory, "b.TIFF"), images.GetNeighbor(1));
            // 拡張子の大文字・小文字や形式を問わず、同じ一覧を前後に移動できる。
            for (int index = 0; index < names.Length; index++)
            {
                images = DirectoryImageList.Create(Path.Combine(directory, names[index]));
                Assert.Equal(index > 0 ? Path.Combine(directory, names[index - 1]) : null, images.GetNeighbor(-1));
                Assert.Equal(index + 1 < names.Length ? Path.Combine(directory, names[index + 1]) : null, images.GetNeighbor(1));
            }
            Assert.Null(DirectoryImageList.Create(Path.Combine(directory, "notes.txt")).GetNeighbor(-1));
            Assert.Null(DirectoryImageList.Create(Path.Combine(directory, "image.webp")).GetNeighbor(-1));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DirectoryList_DoesNotWrapAtEitherEndOrMoveFromUnlistedImages()
    {
        string directory = CreateDirectory();
        try
        {
            string first = Path.Combine(directory, "first.tif");
            string last = Path.Combine(directory, "last.tiff");
            File.WriteAllBytes(first, []);
            File.WriteAllBytes(last, []);
            Assert.Null(DirectoryImageList.Create(first).GetNeighbor(-1));
            Assert.Null(DirectoryImageList.Create(last).GetNeighbor(1));
            Assert.Null(DirectoryImageList.Create(first).GetNeighbor(0));
            Assert.Null(DirectoryImageList.Create(Path.Combine(directory, "missing.tif")).GetNeighbor(1));
            Assert.Null(DirectoryImageList.Create(Path.Combine(directory, "image.png")).GetNeighbor(1));
            Assert.Null(DirectoryImageList.Create(Path.Combine(directory, "missing", "image.tif")).GetNeighbor(1));
            File.Delete(last);
            Assert.Null(DirectoryImageList.Create(first).GetNeighbor(1));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DirectoryList_CancellationStopsEnumeration()
    {
        string directory = CreateDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => DirectoryImageList.Create(
                Path.Combine(directory, "image.tif"), cancellation.Token));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public Task ButtonsAndShortcuts_NavigateFilesAndResetToFirstPage()
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            var window = CreateWindow();
            try
            {
                string first = SaveImage(directory, "01.tif", 31, 2);
                SaveImage(directory, "02.TIFF", 179, 2);
                SaveImage(directory, "03.tif", 220);
                window.Show();
                window.UpdateLayout();
                var previous = Button(window, "PreviousFileButton");
                var next = Button(window, "NextFileButton");
                Assert.False(previous.IsEnabled);
                Assert.False(next.IsEnabled);
                Assert.True(RaiseKey(window, Key.N).Handled);
                await window.OpenImageAsync(first);
                await WaitForAsync(() => next.IsEnabled);
                Assert.False(previous.IsEnabled);
                Button(window, "NextPageButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal("2 / 2", PageText(window));

                next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await WaitForAsync(() => window.Title.StartsWith("02.TIFF") && next.IsEnabled && previous.IsEnabled);
                Assert.Equal("1 / 2", PageText(window));
                AssertPixel(window, 179);
                Assert.True(RaiseKey(window, Key.N).Handled);
                await WaitForAsync(() => window.Title.StartsWith("03.tif") && previous.IsEnabled);
                Assert.False(next.IsEnabled);
                AssertPixel(window, 220);
                await window.ChangeFileAsync(1);
                Assert.StartsWith("03.tif", window.Title);

                Assert.True(RaiseKey(window, Key.B).Handled);
                await WaitForAsync(() => window.Title.StartsWith("02.TIFF") && next.IsEnabled && previous.IsEnabled);
                previous.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await WaitForAsync(() => window.Title.StartsWith("01.tif") && next.IsEnabled);
                Assert.False(previous.IsEnabled);
                Assert.Equal("1 / 2", PageText(window));
                AssertPixel(window, 31);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, true);
            }
        });
    }

    [Fact]
    public Task ButtonsAndShortcuts_NavigateAcrossAllSupportedFormats()
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            var window = CreateWindow();
            try
            {
                string[] names = ["01.tif", "02.PNG", "03.jpg", "04.JPEG", "05.bmp", "06.GIF", "07.tiff"];
                foreach (string name in names) SaveImage(directory, name, 99, name == names[0] ? 2 : 1);
                window.Show();
                await window.OpenImageAsync(Path.Combine(directory, names[0]));
                var previous = Button(window, "PreviousFileButton");
                var next = Button(window, "NextFileButton");
                await WaitForAsync(() => next.IsEnabled);
                Button(window, "NextPageButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Equal("2 / 2", PageText(window));

                // 実際の画像を混在させ、ボタンとキーの両方で形式をまたいだ切り替えを検証する。
                for (int index = 1; index < names.Length; index++)
                {
                    if (index % 2 == 1) next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    else Assert.True(RaiseKey(window, Key.N).Handled);
                    string expected = names[index];
                    await WaitForAsync(() => window.Title.StartsWith(expected) && previous.IsEnabled);
                    Assert.Equal("1 / 1", PageText(window));
                    Assert.Equal(index + 1 < names.Length, next.IsEnabled);
                    Assert.False(Button(window, "PreviousPageButton").IsEnabled);
                    Assert.False(Button(window, "NextPageButton").IsEnabled);
                }
                for (int index = names.Length - 2; index >= 0; index--)
                {
                    if (index % 2 == 0) previous.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    else Assert.True(RaiseKey(window, Key.B).Handled);
                    string expected = names[index];
                    await WaitForAsync(() => window.Title.StartsWith(expected) && next.IsEnabled);
                    Assert.Equal(index == 0 ? "1 / 2" : "1 / 1", PageText(window));
                    Assert.Equal(index > 0, previous.IsEnabled);
                }
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Fact]
    public Task FileChange_DoubleExecutionIsIgnoredAndOpeningAnotherFolderRebuildsNavigation()
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            var window = CreateWindow();
            try
            {
                string first = SaveImage(directory, "01.tif", 31);
                SaveImage(directory, "02.tif", 179);
                SaveImage(directory, "03.tif", 220);
                string otherDirectory = Directory.CreateDirectory(Path.Combine(directory, "other")).FullName;
                string other = SaveImage(otherDirectory, "A.tif", 55);
                SaveImage(otherDirectory, "B.tif", 77);
                window.Show();
                await window.OpenImageAsync(first);
                var previous = Button(window, "PreviousFileButton");
                var next = Button(window, "NextFileButton");
                await WaitForAsync(() => next.IsEnabled);

                Task change = window.ChangeFileAsync(1);
                Assert.False(previous.IsEnabled);
                Assert.False(next.IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("LoadingOverlay")).Visibility);
                await window.ChangeFileAsync(1);
                await change;
                await WaitForAsync(() => next.IsEnabled && previous.IsEnabled);
                Assert.StartsWith("02.tif", window.Title);

                await window.OpenImageAsync(other);
                await WaitForAsync(() => next.IsEnabled);
                Assert.False(previous.IsEnabled);
                await window.ChangeFileAsync(1);
                await WaitForAsync(() => previous.IsEnabled);
                Assert.StartsWith("B.tif", window.Title);
                Assert.False(next.IsEnabled);
                AssertPixel(window, 77);

                string png = Path.Combine(directory, "image.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(80, 60, 99)));
                using (var stream = File.Create(png)) encoder.Save(stream);
                await window.OpenImageAsync(png);
                await WaitForAsync(() => previous.IsEnabled);
                Assert.False(next.IsEnabled);
                await window.ChangeFileAsync(-1);
                Assert.StartsWith("03.tif", window.Title);
                AssertPixel(window, 220);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, true);
            }
        });
    }

    [Theory]
    [InlineData(-1, "01.tif", 31)]
    [InlineData(1, "03.tif", 220)]
    public Task FileChange_KeepsCurrentImageVisibleUntilReplacementIsReady(int step, string expectedFile, byte expectedPixel)
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            var window = CreateWindow();
            try
            {
                SaveImage(directory, "01.tif", 31);
                string middle = SaveImage(directory, "02.tif", 179);
                SaveImage(directory, "03.tif", 220);
                window.Show();
                await window.OpenImageAsync(middle);
                await WaitForAsync(() => Button(window, "PreviousFileButton").IsEnabled
                    && Button(window, "NextFileButton").IsEnabled);
                var image = (Image)window.FindName("DisplayedImage");
                var overlay = (FrameworkElement)window.FindName("LoadingOverlay");
                var border = (FrameworkElement)window.FindName("ImageViewportBorder");
                var source = image.Source;
                Matrix transform = ((MatrixTransform)image.RenderTransform).Matrix;
                var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
                bool coveredImage = false;
                bool clearedImage = false;
                var visibility = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, overlay.GetType())!;
                var imageSource = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, image.GetType())!;
                EventHandler visibilityChanged = (_, _) => coveredImage |= overlay.Visibility == Visibility.Visible;
                EventHandler sourceChanged = (_, _) => clearedImage |= image.Source is null;
                // 切り替えの途中で一瞬だけ画像が隠れる場合も検出する。
                visibility.AddValueChanged(overlay, visibilityChanged);
                imageSource.AddValueChanged(image, sourceChanged);
                try
                {
                    Task change = window.ChangeFileAsync(step);
                    Assert.True(window.IsSpreadBusy);
                    Assert.Same(source, image.Source);
                    Assert.Equal(transform, ((MatrixTransform)image.RenderTransform).Matrix);
                    Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                    Assert.Empty(((TextBlock)window.FindName("ImageLoadingText")).Text);
                    Assert.Equal(Cursors.Wait, border.Cursor);
                    AssertPixel(window, 179);

                    await change;

                    Assert.StartsWith(expectedFile, window.Title);
                    AssertPixel(window, expectedPixel);
                    Assert.False(coveredImage);
                    Assert.False(clearedImage);
                    Assert.Equal(bounds, new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight));
                    Assert.Null(border.Cursor);
                    Assert.True(Button(window, "OpenImageButton").IsEnabled);
                }
                finally
                {
                    visibility.RemoveValueChanged(overlay, visibilityChanged);
                    imageSource.RemoveValueChanged(image, sourceChanged);
                }
            }
            finally { window.Close(); Directory.Delete(directory, true); }
        });
    }

    [Theory]
    [InlineData(".tif", 2)]
    [InlineData(".png", 1)]
    public Task SameFolder_KeepsSnapshotUntilF5RefreshWithoutChangingDisplayedPage(string extension, int pages)
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            var window = CreateWindow();
            try
            {
                string first = SaveImage(directory, "01.tif", 31);
                string middleName = "02" + extension;
                SaveImage(directory, middleName, 179, pages);
                window.Show();
                await window.OpenImageAsync(first);
                var next = Button(window, "NextFileButton");
                var previous = Button(window, "PreviousFileButton");
                await WaitForAsync(() => next.IsEnabled);
                SaveImage(directory, "03.bmp", 220);

                // 一覧取得後に追加したファイルは、同じフォルダーを移動しても未反映のまま。
                await window.ChangeFileAsync(1);
                Assert.StartsWith(middleName, window.Title);
                Assert.False(next.IsEnabled);
                Assert.True(previous.IsEnabled);
                if (pages > 1)
                    Button(window, "NextPageButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                string expectedPage = $"{pages} / {pages}";
                Assert.Equal(expectedPage, PageText(window));
                var image = (Image)window.FindName("DisplayedImage");
                var source = image.Source;
                string title = window.Title;

                Assert.True(RaiseKey(window, Key.F5).Handled);
                await WaitForAsync(() => next.IsEnabled);
                Assert.Same(source, image.Source);
                Assert.Equal(title, window.Title);
                Assert.Equal(expectedPage, PageText(window));

                // 削除も自動再列挙せず、F5で明示的に反映する。
                File.Delete(first);
                Assert.True(previous.IsEnabled);
                Assert.True(RaiseKey(window, Key.F5).Handled);
                await WaitForAsync(() => !previous.IsEnabled && next.IsEnabled);
                Assert.True(next.IsEnabled);
                Assert.Equal(title, window.Title);
                Assert.Equal(expectedPage, PageText(window));
                AssertPixel(window, 179);
            }
            finally
            {
                window.Close();
                Directory.Delete(directory, true);
            }
        });
    }

    [Fact]
    public Task SlowEnumeration_DoesNotDelayImageDisplayAndIsSharedAcrossImageChanges()
    {
        return RunOnStaAsync(async () =>
        {
            string directory = CreateDirectory();
            using var release = new ManualResetEventSlim();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var cache = new DirectoryImageListCache((path, token) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return DirectoryImageList.Create(path, token);
            });
            var window = CreateWindow(cache);
            try
            {
                string first = SaveImage(directory, "01.tif", 31);
                string second = SaveImage(directory, "02.tif", 179);
                window.Show();
                await window.OpenImageAsync(first);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                AssertPixel(window, 31);
                Assert.False(release.IsSet);
                Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("LoadingOverlay")).Visibility);
                Assert.True(Button(window, "FitImageButton").IsEnabled);
                await window.RefreshFileListAsync();
                Assert.Equal(1, calls);

                await window.OpenImageAsync(second);
                AssertPixel(window, 179);
                Assert.Equal(1, calls);
                release.Set();
                await WaitForAsync(() => Button(window, "PreviousFileButton").IsEnabled);
                Assert.True(Button(window, "PreviousFileButton").IsEnabled);
                Assert.False(Button(window, "NextFileButton").IsEnabled);
                Assert.StartsWith("02.tif", window.Title);
            }
            finally
            {
                release.Set();
                window.Close();
                Directory.Delete(directory, true);
            }
        });
    }

    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static string PageText(MainWindow window) => window.Title.Split(" — ")[1].Replace("Page ", "");

    private static KeyEventArgs RaiseKey(MainWindow window, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        window.RaiseEvent(args);
        return args;
    }

    private static void AssertPixel(MainWindow window, byte expected)
    {
        var bitmap = (BitmapSource)((Image)window.FindName("DisplayedImage")).Source;
        int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Assert.Equal(expected, pixels[0]);
    }

    private static MainWindow CreateWindow(DirectoryImageListCache? cache = null) => new(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-navigation-{Guid.NewGuid()}.jsonl"), cache)
    {
        ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000, Top = -10000
    };

    private static string CreateDirectory() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"CSTViewer-navigation-{Guid.NewGuid()}")).FullName;

    private static string SaveImage(string directory, string name, byte value, int pages = 1)
    {
        string path = Path.Combine(directory, name);
        BitmapEncoder encoder = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".tif" or ".tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            ".png" => new PngBitmapEncoder(),
            ".jpg" or ".jpeg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => throw new ArgumentException("Unsupported test image extension.", nameof(name))
        };
        for (int index = 0; index < pages; index++)
            encoder.Frames.Add(BitmapFrame.Create(PreviewLoadingTests.CreateBitmap(80, 60, value)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(predicate());
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // WPFの入力と非同期読み込みを、STAのDispatcher上で検証する。
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
