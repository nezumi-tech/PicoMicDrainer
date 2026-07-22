using System.Globalization;

namespace PicoMicDrainer
{
    internal static class Localization
    {
        public static bool IsJapanese { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

        // MainWindow.xaml
        public static string UpdateAvailable => IsJapanese
            ? "✨ 新しいアップデートが利用可能です！"
            : "✨ A new update is available!";

        public static string DownloadLatest => IsJapanese
            ? "BOOTHの商品ページを開いて最新版をダウンロードする"
            : "Open the BOOTH product page to download the latest version";

        public static string VisualizerCheckbox => IsJapanese
            ? "音声入力を可視化する（レベルメーター有効化）"
            : "Visualize audio input (enable level meter)";

        // MainWindow.xaml.cs - Logs & Messages
        public static string AppTitle => IsJapanese
            ? "PICO Connect マイクバッファ消費ツール"
            : "PICO Connect Mic Buffer Drainer";

        public static string SearchingDevices => IsJapanese
            ? "デバイスを検索中..."
            : "Searching for devices...";

        public static string ErrorDeviceNotFound => IsJapanese
            ? "\n[エラー] '{0}' を含むマイクが見つかりませんでした。"
            : "\n[Error] No microphone containing '{0}' was found.";

        public static string SuccessDeviceDetected => IsJapanese
            ? "\n[成功] 対象デバイスを検出しました: {0}"
            : "\n[Success] Target device detected: {0}";

        public static string StreamStarted => IsJapanese
            ? "\nマイクストリームの消費を開始しました！"
            : "\nStarted consuming microphone stream!";

        public static string ErrorMicOpenFailed => IsJapanese
            ? "\n[エラー] マイクのオープンに失敗しました: {0}"
            : "\n[Error] Failed to open microphone: {0}";

        public static string ErrorBrowserFailed => IsJapanese
            ? "[エラー] ブラウザを開けませんでした: {0}"
            : "[Error] Could not open browser: {0}";

        // Update notification
        public static string UpdateAvailableLog => IsJapanese
            ? "\n[通知] 最新バージョン (v{0}) が公開されました！"
            : "\n[Notice] A new version (v{0}) is now available!";

        public static string UpdateAccessBooth => IsJapanese
            ? "画面上部のリンクからBOOTHにアクセスしてください。"
            : "Please access BOOTH from the link at the top of the screen.";

        // Tray icon context menu
        public static string MenuShowLog => IsJapanese
            ? "ログを表示"
            : "Show Log";

        public static string MenuExit => IsJapanese
            ? "終了する"
            : "Exit";
    }
}
