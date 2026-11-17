using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSTViewer.Tests;

public class CropInteractionTests
{
    [Fact]
    public Task Selection_SaveUsesFullResolutionAndBlocksConcurrentOperations()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage();
            string output = Path.ChangeExtension(path, ".crop.tif");
            var window = CreateWindow();
            try
            {
                Assert.False(window.BeginCropSelection(new Point(100, 100)));
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                var image = (Image)window.FindName("DisplayedImage");
                Assert.True(((BitmapSource)image.Source).PixelWidth < 2000);
                Select(window);
                Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.Equal(Visibility.Visible, ((Canvas)window.FindName("CropSelectionOverlay")).Visibility);
                Task<bool> save = window.SaveCropAsync(output);
                Assert.False(((Button)window.FindName("OpenImageButton")).IsEnabled);
                Assert.False(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.False(window.BeginCropSelection(new Point(100, 100)));
                Assert.False(await window.SaveCropAsync(output));
                await window.ChangeFileAsync(1);
                Assert.True(await save);
                BitmapSource crop = await ImageLoader.LoadAsync(output);
                Assert.Equal((1000, 500), (crop.PixelWidth, crop.PixelHeight));
                Assert.True(((Button)window.FindName("OpenImageButton")).IsEnabled);
                Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("LoadingOverlay")).Visibility);
            }
            finally { window.Close(); File.Delete(path); File.Delete(output); }
        });
    }

    [Fact]
    public Task ZoomResizeAndCaptureLoss_KeepCompletedSelectionAndCancelIncompleteDrag()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage();
            var window = CreateWindow();
            try
            {
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                var canvas = (Canvas)window.FindName("ImageViewportCanvas");
                Select(window);
                var selection = GetSelection(window);
                Rect before = selection.ImageRectangle;
                window.HandleViewportWheel(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 240, ModifierKeys.None);
                window.Width = 900;
                window.UpdateLayout();
                Assert.Equal(before, selection.ImageRectangle);
                Assert.Equal(Visibility.Visible, ((Canvas)window.FindName("CropSelectionOverlay")).Visibility);
                Assert.True(window.BeginCropSelection(new Point(100, 100)));
                window.UpdateCropDrag(new Point(200, 200));
                canvas.ReleaseMouseCapture();
                Assert.False(selection.HasSelection);
                Assert.False(selection.IsDragging);
                Assert.Equal(Visibility.Collapsed, ((Canvas)window.FindName("CropSelectionOverlay")).Visibility);
            }
            finally { window.Close(); File.Delete(path); }
        });
    }

    [Fact]
    public Task RightButtonWheel_ResizesWindowAndRestoresPreviousCrop() => RunOnStaAsync(async () =>
    {
        string path = SaveImage();
        var window = CreateWindow();
        try
        {
            window.Show();
            await window.OpenImageAsync(path);
            window.UpdateLayout();
            var canvas = (Canvas)window.FindName("ImageViewportCanvas");
            Select(window);
            var selection = GetSelection(window);
            Rect previous = selection.ImageRectangle;
            Point start = ImagePoint(window, new Point(300, 300));
            Point end = ImagePoint(window, new Point(900, 700));
            Assert.True(window.BeginCropSelection(start));
            window.UpdateCropDrag(end);
            Assert.NotEqual(previous, selection.ImageRectangle);
            Assert.True(canvas.IsMouseCaptured);

            double width = window.Width;
            window.HandleViewportWheel(end, 120, ModifierKeys.None, isRightButtonPressed: true);
            window.UpdateLayout();
            Assert.True(window.Width > width);
            Assert.Equal(previous, selection.ImageRectangle);
            Assert.False(selection.IsDragging);
            Assert.False(canvas.IsMouseCaptured);

            // 同じ右ボタン押下中の次のホイール入力でもサイズを変えられる。
            width = window.Width;
            window.HandleViewportWheel(end, -120, ModifierKeys.None, isRightButtonPressed: true);
            window.UpdateLayout();
            Assert.True(window.Width < width);
            Assert.Equal(previous, selection.ImageRectangle);

            start = ImagePoint(window, new Point(300, 300));
            end = ImagePoint(window, new Point(900, 700));
            Assert.True(window.BeginCropSelection(start));
            window.CompleteCropDrag(end);
            Assert.True(selection.HasSelection);
            Assert.NotEqual(previous, selection.ImageRectangle);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task PageAndFileChanges_ClearSelectionAndSaveFailureRestoresControls()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage();
            var window = CreateWindow();
            try
            {
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                Select(window);
                Assert.False(await window.SaveCropAsync(path));
                Assert.True(GetSelection(window).HasSelection);
                Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.Contains("failed", ((TextBlock)window.FindName("PreloadStatusText")).Text);
                var next = (Button)window.FindName("NextPageButton");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(GetSelection(window).HasSelection);
                Assert.Contains("— Page 2 / 2", window.Title);
                Select(window);
                await window.OpenImageAsync(path);
                Assert.False(GetSelection(window).HasSelection);
                Assert.False(await window.CopyCropAsync(_ => Assert.Fail("未選択でコピーされました。")));
                Select(window);
                var canvas = (Canvas)window.FindName("ImageViewportCanvas");
                canvas.Focus();
                canvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Assert.False(GetSelection(window).HasSelection);
            }
            finally { window.Close(); File.Delete(path); }
        });
    }

    private static MainWindow CreateWindow() => new(Path.Combine(Path.GetTempPath(), $"CSTViewer-crop-{Guid.NewGuid():N}.jsonl"))
        { ShowActivated = false, Width = 750, Height = 500 };

    [Theory]
    [InlineData(TiffCompressOption.Zip, false)]
    [InlineData(TiffCompressOption.Lzw, true)]
    public Task InsideClick_SavesToConfiguredDestinationCopiesPngAndKeepsSelection(TiffCompressOption compression, bool extraPng)
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage(compression);
            string directory = Path.Combine(AppContext.BaseDirectory, $"crop-destination-{Guid.NewGuid():N}", "new folder");
            string output = Path.Combine(directory, "clip.tif");
            var window = CreateWindow();
            try
            {
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                Select(window);
                window.CropSaveDestination = Path.ChangeExtension(output, ".png");
                var destinationButton = (Button)window.FindName("CropSaveDestinationButton");
                Rect before = GetSelection(window).ImageRectangle;
                Point center = ImagePoint(window, new Point(700, 350));
                Assert.True(window.BeginCropAdjustment(center),
                    $"切り抜き調整を開始できませんでした。選択範囲={before}、操作位置={center}");
                byte[]? copiedPng = null;
                Task<bool> save = window.CompleteCropAdjustmentAsync(center, data =>
                {
                    Assert.Equal((1000, 500), (data.GetImage().PixelWidth, data.GetImage().PixelHeight));
                    copiedPng = Assert.IsAssignableFrom<MemoryStream>(data.GetData("PNG")).ToArray();
                });
                Assert.False(destinationButton.IsEnabled,
                    $"保存処理中に保存先ボタンが無効化されていません。操作状態={GetSelection(window).Mode}、保存タスク完了={save.IsCompleted}、状態表示={((TextBlock)window.FindName("PreloadStatusText")).Text}");
                Assert.False(await window.SaveConfiguredCropAsync(_ => Assert.Fail("二重実行されました。")));
                Assert.True(await save, ((TextBlock)window.FindName("PreloadStatusText")).Text);
                Assert.NotNull(copiedPng);
                Assert.True(File.Exists(output));
                Assert.Equal(TiffEncoding.Read(path, 0).Compression, TiffEncoding.Read(output, 0).Compression);
                Assert.Equal(extraPng, File.Exists(Path.ChangeExtension(output, ".png")));
                if (extraPng) Assert.Equal(copiedPng, File.ReadAllBytes(Path.ChangeExtension(output, ".png")));
                Assert.Equal(before, GetSelection(window).ImageRectangle);
                Assert.True(destinationButton.IsEnabled);

                // 指定名への保存はPython版と同じく上書きし、コピー失敗も保存を取り消さない。
                File.WriteAllText(output, "old output");
                Assert.True(await window.SaveConfiguredCropAsync(_ =>
                    throw new System.Runtime.InteropServices.ExternalException("clipboard busy")));
                BitmapSource saved = await ImageLoader.LoadAsync(output);
                Assert.Equal((1000, 500), (saved.PixelWidth, saved.PixelHeight));
                Assert.Contains("Clipboard copy failed", ((TextBlock)window.FindName("PreloadStatusText")).Text);
                Assert.Equal(before, GetSelection(window).ImageRectangle);

                Assert.True(window.BeginCropAdjustment(center));
                Assert.False(await window.CompleteCropAdjustmentAsync(center + new Vector(20, 20),
                    _ => Assert.Fail("ドラッグで保存されました。")));
                Assert.NotEqual(before, GetSelection(window).ImageRectangle);
            }
            finally
            {
                window.Close();
                File.Delete(path);
                // 保存前にテストが失敗しても、後片付けの例外で本来の失敗を隠さない。
                if (File.Exists(output)) File.Delete(output);
                if (File.Exists(Path.ChangeExtension(output, ".png"))) File.Delete(Path.ChangeExtension(output, ".png"));
                if (Directory.Exists(directory)) Directory.Delete(directory);
                if (Directory.Exists(Path.GetDirectoryName(directory))) Directory.Delete(Path.GetDirectoryName(directory)!);
            }
        });
    }

    [Fact]
    public Task DestinationDialog_ConfirmsOrDiscardsEditsWithoutTriggeringImageShortcuts()
    {
        return RunOnStaAsync(async () =>
        {
            string path = SaveImage();
            var window = CreateWindow();
            try
            {
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                Select(window);
                Assert.Null(window.FindName("CropSavePathTextBox"));
                Assert.Equal(CropSavePath.DefaultText, window.CropSaveDestination);
                var button = (Button)window.FindName("CropSaveDestinationButton");
                const string destination = "scan-B-N.tif";

                await EditDestination(destination, confirm: true);
                Assert.Equal(destination, window.CropSaveDestination);
                Assert.True(GetSelection(window).HasSelection);
                Assert.Contains("— Page 1 / 2", window.Title);

                await window.OpenImageAsync(path);
                await EditDestination("cancelled.tif", confirm: false);
                Assert.Equal(destination, window.CropSaveDestination);

                async Task EditDestination(string text, bool confirm)
                {
                    // ShowDialog のメッセージループで、表示されたダイアログの入力欄を操作する。
                    var edit = window.Dispatcher.InvokeAsync(() =>
                    {
                        var dialog = Assert.Single(window.OwnedWindows.OfType<CropSaveDestinationWindow>());
                        try
                        {
                            Assert.False(window.IsActive);
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Same(dialog, Assert.Single(window.OwnedWindows.OfType<CropSaveDestinationWindow>()));
                            var textbox = (TextBox)dialog.FindName("CropSavePathTextBox");
                            Assert.Equal(window.CropSaveDestination, textbox.Text);
                            textbox.Text = text;
                            foreach (Key key in new[] { Key.A, Key.C, Key.D, Key.L, Key.Q, Key.R, Key.S, Key.W, Key.D1, Key.D2, Key.PageDown })
                            {
                                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(textbox)!,
                                    Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                                textbox.RaiseEvent(args);
                                Assert.False(args.Handled);
                            }
                            if (confirm)
                            {
                                var ok = (Button)dialog.FindName("ConfirmDestinationButton");
                                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                        }
                        finally
                        {
                            if (dialog.IsVisible) dialog.Close();
                        }
                    }, DispatcherPriority.Background);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await edit.Task;
                }
            }
            finally { window.Close(); File.Delete(path); }
        });
    }

    [Fact]
    public Task HelpButton_ShowsCropDestinationIconAndLocalizedInstructions() => RunOnStaAsync(() =>
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            ((Button)window.FindName("HelpButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var help = Assert.Single(window.OwnedWindows.OfType<HelpWindow>());
            var languages = (ComboBox)help.FindName("HelpLanguageComboBox");
            var viewer = (FlowDocumentScrollViewer)help.FindName("HelpDocumentViewer");
            var toolbarIcon = Assert.IsType<ToolbarIcon>(((Button)window.FindName("CropSaveDestinationButton")).Content);
            foreach (HelpContent language in languages.Items)
            {
                languages.SelectedItem = language;
                FlowDocument document = viewer.Document;
                var toolbar = document.Blocks.OfType<Table>().First();
                int destinationIndex = Array.FindIndex(language.Toolbar.Rows,
                    row => row.ButtonName == nameof(MainWindow.CropSaveDestinationButton));
                Assert.True(destinationIndex >= 0);
                TableRow destinationRow = toolbar.RowGroups[0].Rows[destinationIndex + 1];
                var label = Assert.IsType<Paragraph>(destinationRow.Cells[0].Blocks.FirstBlock);
                var iconContainer = Assert.Single(label.Inlines.OfType<InlineUIContainer>());
                var icon = Assert.IsType<ToolbarIcon>(Assert.IsType<Border>(iconContainer.Child).Child);
                Assert.Equal(toolbarIcon.Glyph, icon.Glyph);
                string documentText = new TextRange(document.ContentStart, document.ContentEnd).Text;
                Assert.Contains(language.Crop.SaveToDescription, documentText);
                var search = Assert.Single(language.Toolbar.Rows, row => row.ButtonName == nameof(MainWindow.SearchButton));
                Assert.Contains(search.Description, documentText);
                var ocr = Assert.Single(language.Toolbar.Rows, row => row.ButtonName == nameof(MainWindow.OcrButton));
                Assert.Contains(ocr.Description, documentText);
                Assert.Contains(language.LanguageCode == "ja" ? "右ボタン ＋ ホイール" : "right button + mouse wheel", documentText);
                Assert.Contains(language.LanguageCode == "ja" ? "左右に並んでいる間は、タイトルバー" : "arranged side by side, use the title bar", documentText);
            }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task CopyCrop_ContainsTransparentPngAndBitmapAndRecoversFromClipboardFailure()
    {
        return RunOnStaAsync(async () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, $"CSTViewer-crop-alpha-{Guid.NewGuid():N}.png");
            var window = CreateWindow();
            try
            {
                var pixels = new byte[2000 * 1000 * 4];
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 30; pixels[index + 1] = 80; pixels[index + 2] = 170; pixels[index + 3] = 128;
                }
                var source = BitmapSource.Create(2000, 1000, 96, 96, PixelFormats.Bgra32, null, pixels, 2000 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var stream = File.Create(path)) encoder.Save(stream);
                window.Show();
                await window.OpenImageAsync(path);
                window.UpdateLayout();
                Select(window);
                bool captured = false;
                Assert.True(await window.CopyCropAsync(data =>
                {
                    // OSへ書き込む直前のデータを確認し、実際のクリップボードは変更しない。
                    Assert.True(data.GetDataPresent(DataFormats.Bitmap));
                    BitmapSource bitmap = data.GetImage();
                    Assert.Equal((1000, 500), (bitmap.PixelWidth, bitmap.PixelHeight));
                    var png = Assert.IsAssignableFrom<MemoryStream>(data.GetData("PNG"));
                    png.Position = 0;
                    var decoded = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    var firstPixel = new byte[4];
                    decoded.CopyPixels(new Int32Rect(0, 0, 1, 1), firstPixel, 4, 0);
                    Assert.Equal(new byte[] { 30, 80, 170, 128 }, firstPixel);
                    captured = true;
                }));
                Assert.True(captured);
                Assert.Contains("Copied", ((TextBlock)window.FindName("PreloadStatusText")).Text);
                Assert.False(await window.CopyCropAsync(_ => throw new System.Runtime.InteropServices.ExternalException("clipboard busy")));
                Assert.True(((Canvas)window.FindName("ImageViewportCanvas")).IsEnabled);
                Assert.True(GetSelection(window).HasSelection);
                Assert.Contains("clipboard busy", ((TextBlock)window.FindName("PreloadStatusText")).Text);
            }
            finally { window.Close(); File.Delete(path); }
        });
    }

    private static ImageCropSelection GetSelection(MainWindow window) => (ImageCropSelection)typeof(MainWindow)
        .GetField("_cropSelection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Select(MainWindow window)
    {
        var image = (Image)window.FindName("DisplayedImage");
        Matrix matrix = ((MatrixTransform)image.RenderTransform).Matrix;
        // 整数境界ぴったりの座標を避け、倍率の丸め誤差に依存せず200～1199・100～599画素を選ぶ。
        Assert.True(window.BeginCropSelection(matrix.Transform(new Point(200.1, 100.1))));
        window.UpdateCropDrag(matrix.Transform(new Point(1199.9, 599.9)));
        window.CompleteCropDrag(matrix.Transform(new Point(1199.9, 599.9)));
    }

    private static Point ImagePoint(MainWindow window, Point point) =>
        ((MatrixTransform)((Image)window.FindName("DisplayedImage")).RenderTransform).Matrix.Transform(point);

    private static string SaveImage(TiffCompressOption compression = TiffCompressOption.Zip)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"CSTViewer-crop-ui-{Guid.NewGuid():N}.tif");
        var image = BitmapSource.Create(2000, 1000, 96, 96, PixelFormats.Gray8, null, new byte[2000 * 1000], 2000);
        var encoder = new TiffBitmapEncoder { Compression = compression };
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // WPFのキャプチャと非同期UI更新を、STAのDispatcher上で検証する。
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
