using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CSTViewer;

public partial class App : Application
{
    private readonly ConcurrentQueue<string[]> _openRequests = new();
    private SingleInstanceBroker? _instanceBroker;
    private TrayIcon? _trayIcon;
    private bool _startupReady;
    private bool _processingRequests;
    private bool _exiting;
    private readonly string[]? _startupPaths;

    public App()
    {
    }

    internal App(SingleInstanceBroker broker, StartupRequestBuffer requests, string[] paths)
    {
        _instanceBroker = broker;
        _startupPaths = paths;
        requests.Attach(QueueOpenRequest);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // .NETが生成するエラーメッセージも英語に揃え、数値や日時の書式は変更しない。
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(ViewerSettings.UiCultureName);
        base.OnStartup(e);
        // 計測ツールなどがRun(window)でウィンドウを指定した場合は、その画面を使う。
        if (MainWindow is not null) return;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string[] paths = _startupPaths ?? StartupImagePaths.Resolve(e.Args, Environment.CurrentDirectory);
        if (_instanceBroker is null)
        {
            // 計測ツールなどがAppを直接作成した場合も、従来の単一起動を維持する。
            string pipeName = SingleInstanceBroker.ApplicationPipeName;
            _instanceBroker = SingleInstanceBroker.TryStart(pipeName, QueueOpenRequest);
            if (_instanceBroker is null)
            {
                try
                {
                    await SingleInstanceBroker.ForwardAsync(pipeName, paths);
                    Shutdown();
                    return;
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException)
                {
                    _instanceBroker = SingleInstanceBroker.TryStart(pipeName, QueueOpenRequest);
                    if (_instanceBroker is null)
                    {
                        Shutdown(WpfStartup.ShowForwardingError());
                        return;
                    }
                }
            }
        }
        _trayIcon = new TrayIcon(() => QueueOpenRequest([]), ExitApplication);
        // 同時起動の要求より先に、最初の起動で指定した画像を表示する。
        await OpenWindowsAsync(paths);
        _startupReady = true;
        // 最後の画面を閉じても、ランタイムと画像読み込みライブラリを常駐させる。
        ProcessOpenRequests();
    }

    private void QueueOpenRequest(string[] paths)
    {
        _openRequests.Enqueue(paths);
        // 名前付きパイプの受信スレッドから、WPFのUIスレッドへ切り替える。
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ProcessOpenRequests));
    }

    private async void ProcessOpenRequests()
    {
        if (!_startupReady || _processingRequests || _exiting) return;
        _processingRequests = true;
        try
        {
            while (!_exiting && _openRequests.TryDequeue(out string[]? paths))
                await OpenWindowsAsync(paths);
        }
        finally { _processingRequests = false; }
    }

    private async Task OpenWindowsAsync(string[] paths)
    {
        if (paths.Length == 0)
        {
            var active = Windows.OfType<MainWindow>().FirstOrDefault(window => window.IsActive)
                ?? Windows.OfType<MainWindow>().LastOrDefault();
            if (active is not null)
            {
                if (active.WindowState == WindowState.Minimized) active.WindowState = WindowState.Normal;
                active.Activate();
                return;
            }
            paths = new[] { string.Empty };
        }
        foreach (string path in paths)
        {
            if (_exiting) return;
            var window = new MainWindow();
            MainWindow ??= window;
            if (path.Length > 0) await window.PrepareStartupImageAsync(path);
            else window.PrepareEmptyStartup();
            if (_exiting) { window.Close(); return; }
            window.Show();
            window.Activate();
            // 複数ページの先読み中も、別のTIFFを新しい画面で開けるようにする。
            if (path.Length > 0) _ = window.OpenImageAsync(path);
        }
    }

    internal void ReleaseClosedWindow(MainWindow window)
    {
        // Application.MainWindowが閉じた画面を保持すると、常駐中に回収されなくなる。
        if (ReferenceEquals(MainWindow, window))
            MainWindow = Windows.OfType<MainWindow>().FirstOrDefault(other => !ReferenceEquals(other, window));
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _openRequests.Clear();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _trayIcon?.Dispose();
        _instanceBroker?.Dispose();
        base.OnExit(e);
    }
}
