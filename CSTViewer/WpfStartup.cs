using System.Runtime.CompilerServices;
using System.Windows;

namespace CSTViewer;

/// <summary>受信役になったプロセスだけが実行するWPFの初期化。</summary>
internal static class WpfStartup
{
    // インライン化を禁止し、転送だけの起動でWPFの型がJITに読み込まれないようにする。
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(SingleInstanceBroker broker, StartupRequestBuffer requests, string[] paths)
    {
        var app = new App(broker, requests, paths);
        app.InitializeComponent();
        return app.Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int ShowForwardingError()
    {
        MessageBox.Show("Could not send the images to the running CSTViewer instance. Please try opening them again.",
            "CSTViewer", MessageBoxButton.OK, MessageBoxImage.Error);
        return 1;
    }
}
