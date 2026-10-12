using System.Globalization;
using System.IO;

namespace CSTViewer;

/// <summary>常駐中なら、WPFを初期化せず画像パスを送って終了する。</summary>
internal static class Program
{
    // WPFを起動する場合も、このメインスレッドをSTAのまま使う。
    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(StartupSettings.UiCultureName);
        string[] paths = StartupImagePaths.Resolve(args, Environment.CurrentDirectory);
        return Run(paths, SingleInstanceBroker.ApplicationPipeName, WpfStartup.Run, WpfStartup.ShowForwardingError);
    }

    internal static int Run(string[] paths, string pipeName,
        Func<SingleInstanceBroker, StartupRequestBuffer, string[], int> runApplication, Func<int> showForwardingError)
    {
        var requests = new StartupRequestBuffer();
        SingleInstanceBroker? broker = SingleInstanceBroker.TryStart(pipeName, requests.Enqueue);
        if (broker is null)
        {
            try
            {
                // UIの同期コンテキストを作る前なので、転送の完了を待ってそのまま終了できる。
                SingleInstanceBroker.ForwardAsync(pipeName, paths).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException or ArgumentException)
            {
                // 転送中に常駐先が終了した場合だけ、このプロセスが受信役を引き継ぐ。
                broker = SingleInstanceBroker.TryStart(pipeName, requests.Enqueue);
                if (broker is null) return showForwardingError();
            }
        }
        // WPFの初期化が失敗した場合にも、確保済みの受信口を必ず解放する。
        using (broker) return runApplication(broker, requests, paths);
    }
}
