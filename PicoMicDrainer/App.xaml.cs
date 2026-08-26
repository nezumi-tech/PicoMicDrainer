using System.Configuration;
using System.Data;
#if !DEBUG
using System.Threading;
#endif
using System.Windows;

namespace PicoMicDrainer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        // バグ7修正用：多重起動防止のための名前付き Mutex。
        // プロセスが終了すると自動的に解放されるため、明示的な Dispose は不要。
        // Debug ビルドでは多重起動を許容するため、フィールド自体もコンパイルしない（CS0169 回避）。
#if !DEBUG
        private Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = @"Local\PicoMicDrainer.SingleInstance";
            _singleInstanceMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // 既に起動中：確認ダイアログを出し、Yes の場合のみこの（2番目の）インスタンスも起動させる。
                var result = System.Windows.MessageBox.Show(
                    Localization.AlreadyRunningMessage,
                    Localization.AlreadyRunningTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    Shutdown();
                    return;
                }
            }

            base.OnStartup(e);
        }
#endif
    }

}
