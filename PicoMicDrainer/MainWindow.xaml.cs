using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media; // CompositionTarget のために追加
using System.Windows.Threading;

namespace PicoMicDrainer
{
    public partial class MainWindow : Window
    {
        private const string TargetDeviceKeyword = "PicoStreaming";

        private const string GithubOwner = "nezumi-tech";
        private const string GithubRepo = "PicoMicDrainer";

        // 対象デバイスが未接続・ストリーム切断時の自動再試行間隔
        private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

        // バグ2修正用：ログの最大保持行数。超過すると古い行から削除する。
        private const int MaxLogLines = 500;
        // 上限をこれだけ超過した時点でまとめてトリムする（毎回書かないためのバッファ量）
        private const int LogTrimBatchSize = 100;

        private WaveInEvent? _waveIn;
        /// <summary>現在、マイクストリームを正常に消費できているか（StartRecording 成功〜停止までの状態）。</summary>
        private volatile bool _isStreamRunning = false;
        private DispatcherTimer? _reconnectTimer;
        private NotifyIcon? _notifyIcon;
        private bool _isExitMode = false;

        // バグ3修正用：終了処理開始フラグ。
        // 終了中は AddLog（Dispatcher.Invoke）や再接続リトライを実行しないことで、
        // シャットダウン済みディスパッチャへの呼び出しによる例外を防ぐ。
        private volatile bool _isShuttingDown = false;

        // バグ8修正用：CompositionTarget.Rendering に登録済みか（ウィンドウ非表示中は解除して毎フレームの無駄な呼び出しを止める）
        private bool _renderingHooked = false;

        // UI更新フラグ
        private bool _isUpdatingStartupUI = false; // ★追加

        // 可視化用の変数
        private volatile bool _isVisualizerEnabled = false;
        private float _latestVolumePeak = 0f;

        public MainWindow()
        {
            InitializeComponent();
            ApplyLocalization();
            SetupTrayIcon();

            // WPFの描画フレーム同期イベントを登録（バグ8修正：ウィンドウ非表示中は解除する）
            HookRendering(true);

            // 起動時にスタートアップ登録状態をチェックしてUIに反映する
            CheckStartupStatus();

            // 画面の表示状態に関わらず、生成と同時に起動処理を走らせる
            _ = StartupProcessAsync();
        }

        /// <summary>
        /// バグ8修正用：CompositionTarget.Rendering の購読/解除を行う。
        /// ウィンドウが隠れている（トレイ格納・最小化）間も毎フレーム OnRendering が呼ばれ続けるのを防ぐため、
        /// 非表示時は解除し、再表示時に再接続する。
        /// </summary>
        private void HookRendering(bool hook)
        {
            if (hook && !_renderingHooked)
            {
                CompositionTarget.Rendering += OnRendering;
                _renderingHooked = true;
            }
            else if (!hook && _renderingHooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderingHooked = false;
            }
        }

        private void ApplyLocalization()
        {
            UpdateTitleRun.Text = Localization.UpdateAvailable;
            DownloadLinkRun.Text = Localization.DownloadLatest;
            VisualizerCheck.Content = Localization.VisualizerCheckbox;
            StartupCheck.Content = Localization.StartupCheckbox;
            StartupPromptRun.Text = Localization.StartupPromptText;
        }

        private void SetupTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            try
            {
                // 埋め込みリソースから app.ico を読み込む
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("PicoMicDrainer.app.ico"))
                {
                    if (stream != null)
                    {
                        _notifyIcon.Icon = new Icon(stream);
                    }
                    else
                    {
                        Debug.WriteLine("Warning: app.ico リソースが見つかりません。デフォルトアイコンを使用します。");
                        _notifyIcon.Icon = SystemIcons.Application;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: タスクトレイアイコンの読み込みに失敗しました: {ex.Message}");
                _notifyIcon.Icon = SystemIcons.Application;
            }
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "Pico Mic Drainer";

            _notifyIcon.DoubleClick += (s, e) => ShowFromTray();

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(Localization.MenuShowLog, null, (s, e) => ShowFromTray());
            contextMenu.Items.Add(Localization.MenuExit, null, (s, e) =>
            {
                _isExitMode = true;
                System.Windows.Application.Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        /// <summary>トレイからウィンドウを再表示する。</summary>
        private void ShowFromTray()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            // バグ8修正：再表示に合わせて描画フレーム同期イベントを再接続する
            HookRendering(true);
        }

        private async Task StartupProcessAsync()
        {
            // 少しだけ待機（UIスレッドの初期化を確実に終わらせるための安全策）
            await Task.Delay(500);

            AddLog(Localization.AppTitle);
            AddLog(GetApplicationHeader());
            AddLog(Localization.SearchingDevices);

            if (!StartDraining())
            {
                // 起動時のみ「見つかりません」エラーをログに出す。
                // 以降のタイマーによるリトライ中は静かに再チェックし、数秒ごとにログを連打しないようにする。
                AddLog(string.Format(Localization.ErrorDeviceNotFound, TargetDeviceKeyword));
            }

            // 自動再接続のリトライを開始：デバイス未接続・ストリーム切断時は ReconnectInterval ごとに再接続を試みる
            _reconnectTimer = new DispatcherTimer { Interval = ReconnectInterval };
            _reconnectTimer.Tick += OnReconnectTick;
            _reconnectTimer.Start();

            await CheckForUpdatesAsync();
        }

        private string GetApplicationHeader()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Unknown";
            var cleanVersion = informationalVersion.Split('+')[0];
            return $"--- Pico Mic Drainer v{cleanVersion} ---";
        }

        /// <summary>
        /// 対象マイクの音声消費を開始する。成功したら true、デバイスが見つからない・開始失敗なら false を返す。
        /// 「見つかりません」のログは呼び出し側が出す（リトライ中は静かにするため）。
        /// </summary>
        private bool StartDraining()
        {
            // 再接続のため、古いインスタンスを先に破棄する
            DisposeWaveIn();

            int deviceNumber = -1;
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var capabilities = WaveIn.GetCapabilities(i);
                if (capabilities.ProductName.IndexOf(TargetDeviceKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    deviceNumber = i;
                    break;
                }
            }

            if (deviceNumber == -1)
            {
                return false;
            }

            var deviceInfo = WaveIn.GetCapabilities(deviceNumber);
            AddLog(string.Format(Localization.SuccessDeviceDetected, deviceInfo.ProductName));

            try
            {
                _waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(48000, 1), // 48kHz, モノラル (16bit PCM)
                    BufferMilliseconds = 50
                };

                // データ受信時の処理（超軽量化設計）
                _waveIn.DataAvailable += (s, a) =>
                {
                    // チェックボックスがオフなら、解析を一切スキップして即座に終了（バッファ消費最優先）
                    if (!_isVisualizerEnabled) return;

                    float max = 0f;
                    // 16bit PCMは2バイトで1サンプル。バッファ内の最大絶対値を検索
                    for (int i = 0; i < a.BytesRecorded; i += 2)
                    {
                        short sample = BitConverter.ToInt16(a.Buffer, i);
                        float sample32 = sample / 32768f; // -1.0 〜 1.0 に正規化
                        if (Math.Abs(sample32) > max)
                        {
                            max = Math.Abs(sample32);
                        }
                    }

                    // 最新のピーク値を保持（UIスレッド側がこれを拾って描画する）
                    _latestVolumePeak = max;
                };

                // ストリームが停止した（デバイス切断・PICO Connect のクラッシュ等）場合の処理
                _waveIn.RecordingStopped += OnWaveInRecordingStopped;

                _waveIn.StartRecording();
                _isStreamRunning = true;

                AddLog(Localization.StreamStarted);
                return true;
            }
            catch (Exception ex)
            {
                AddLog(string.Format(Localization.ErrorMicOpenFailed, ex.Message));
                DisposeWaveIn();
                return false;
            }
        }

        /// <summary>現在の録音インスタンスを停止・破棄する。</summary>
        private void DisposeWaveIn()
        {
            _isStreamRunning = false;
            if (_waveIn == null) return;
            try { _waveIn.StopRecording(); } catch { /* 既に止まっている可能性があり無視してよい */ }
            _waveIn.Dispose();
            _waveIn = null;
        }

        /// <summary>
        /// 録音ストリームが停止したイベント。異常停止（例外付き）のときのみログを出す。
        /// 再接続そのものはリトライタイマー (_reconnectTimer) が担当する。
        /// </summary>
        private void OnWaveInRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _isStreamRunning = false;

            if (e.Exception != null && !_isExitMode && !_isShuttingDown)
            {
                AddLog(string.Format(Localization.ErrorStreamDisconnected, e.Exception.Message));
            }
        }

        /// <summary>
        /// リトライタイマーの tick。正常に消費中なら何もしない。
        /// 未接続・切断中は StartDraining() を再呼び出しして再接続を試みる（成功時のみログが出る）。
        /// </summary>
        private void OnReconnectTick(object? sender, EventArgs e)
        {
            if (_isExitMode || _isShuttingDown) return;

            // 正常に消費中なら何もしない
            if (_waveIn != null && _isStreamRunning) return;

            StartDraining();
        }

        // チェックボックスの状態変更イベント
        private void VisualizerCheck_Changed(object? sender, RoutedEventArgs e)
        {
            _isVisualizerEnabled = VisualizerCheck.IsChecked ?? false;
            if (!_isVisualizerEnabled)
            {
                _latestVolumePeak = 0f;
            }
        }

        // WPFの画面描画（リフレッシュレート）と同期して呼ばれる軽量ループ
        private void OnRendering(object? sender, EventArgs e)
        {
            if (_isVisualizerEnabled)
            {
                // 0.0〜1.0 のピーク値を 0〜100 のパーセンテージに変換してメーターに反映
                VolumeBar.Value = _latestVolumePeak * 100;

                // メーターがカクつかずスムーズに減少するよう、少しずつ減衰（フォールオフ）させる
                _latestVolumePeak *= 0.85f;
                if (_latestVolumePeak < 0.001f) _latestVolumePeak = 0f;
            }
            else
            {
                if (VolumeBar.Value > 0) VolumeBar.Value = 0;
            }
        }

        private void AddLog(string message)
        {
            // バグ3修正：終了処理中はログを書かない（シャットダウン済みディスパッチャへの Invoke は例外になるため）
            if (_isShuttingDown) return;

            // UIを操作するため、確実にUIスレッド上で実行する。
            // 終了直後の競合で Dispatcher が既に無効化されていた場合は安全に無視する。
            try
            {
                Dispatcher.Invoke(() => AppendLogCore(message));
            }
            catch (Exception)
            {
                // アプリ終了直後の呼ばれ（未観測タスク例外にならないように握り潰す）
            }
        }

        /// <summary>ログを TextBlock に追記し、行数が上限を超えたら古い行を削除する。</summary>
        private void AppendLogCore(string message)
        {
            // ログのテキストを追記
            LogText.Text += message + "\n";

            // バグ2修正：常駐アプリではログが無制限に増え続けるため、行数上限で古い行を切る。
            // 毎回トリムすると重いので、MaxLogLines を LogTrimBatchSize だけ超過した時点でまとめて削除する。
            string text = LogText.Text;

            int newlineCount = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') newlineCount++;
            }

            if (newlineCount > MaxLogLines + LogTrimBatchSize)
            {
                // MaxLogLines ちょうどまで戻す行数だけ、先頭から削除する
                int linesToRemove = newlineCount - MaxLogLines;
                int startIndex = 0;
                for (int i = 0; i < text.Length && linesToRemove > 0; i++)
                {
                    if (text[i] == '\n')
                    {
                        linesToRemove--;
                        if (linesToRemove == 0)
                        {
                            startIndex = i + 1;
                        }
                    }
                }
                LogText.Text = text.Substring(startIndex);
            }

            // ★追加：ScrollViewer を自動的に一番下までスクロールさせる
            LogScrollViewer.ScrollToEnd();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitMode)
            {
                // 右クリックから「終了する」を選んでいない場合は、ウィンドウを隠す
                e.Cancel = true;
                this.Hide();

                // ★追加：タスクトレイ格納時にチェックをOFFにして音声計算を止める
                VisualizerCheck.IsChecked = false;

                // バグ8修正：非表示中は描画フレーム同期イベントを解除し、毎フレームの無駄な呼び出しを止める
                VolumeBar.Value = 0;
                HookRendering(false);

                return;
            }

            // 本当の終了処理
            // バグ3修正：最初にフラグを立てることで、終了中にイベントハンドラ・非同期処理から
            // AddLog（Dispatcher.Invoke）が呼ばれても安全に無視されるようにする。
            _isShuttingDown = true;

            HookRendering(false);

            // 再接続リトライタイマーを停止する
            if (_reconnectTimer != null)
            {
                _reconnectTimer.Stop();
                _reconnectTimer.Tick -= OnReconnectTick;
            }

            DisposeWaveIn();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
        }
        // ウィンドウの状態が変わったときに呼ばれるメソッド
        protected override void OnStateChanged(EventArgs e)
        {
            // 最小化されたら、ウィンドウを隠してチェックをOFFにする
            if (WindowState == WindowState.Minimized)
            {
                this.Hide();
                VisualizerCheck.IsChecked = false;

                // バグ8修正：非表示中は描画フレーム同期イベントを解除する
                VolumeBar.Value = 0;
                HookRendering(false);
            }
            base.OnStateChanged(e);
        }
        private async Task CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                // GitHub API は User-Agent ヘッダーが必須です
                client.DefaultRequestHeaders.Add("User-Agent", "PicoMicDrainer-UpdateChecker");

                // 最新リリースの情報を取得するAPI URL
                string url = $"https://api.github.com/repos/{GithubOwner}/{GithubRepo}/releases/latest";

                var response = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);

                // JSONからタグ名（例: "v1.0.1"）とリリースURLを取り出す
                string? tagName = doc.RootElement.GetProperty("tag_name").GetString();
                string? releaseUrl = doc.RootElement.GetProperty("html_url").GetString();

                if (!string.IsNullOrEmpty(tagName))
                {
                    // "v1.0.1" などの先頭の 'v' を取り除いて "1.0.1" にする
                    string cleanTag = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName.Substring(1) : tagName;

                    // GitHubのバージョンと、現在のアプリのバージョン（.csprojで設定した値）を比較
                    if (Version.TryParse(cleanTag, out Version? latestVersion))
                    {
                        Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version!;

                        // GitHubのバージョンの方が新しい場合
                        if (latestVersion > currentVersion)
                        {
                            // バグ3修正：終了中に結果が帰ってきてもUIを更新しない（Dispatcher.Invoke が例外になるため）
                            if (_isShuttingDown) return;

                            Dispatcher.Invoke(() =>
                            {
                                // ウィンドウを隠し状態から復帰させる
                                this.Show();
                                this.WindowState = WindowState.Normal;
                                this.Activate();

                                // バグ8修正：再表示に合わせて描画フレーム同期イベントを再接続する
                                HookRendering(true);

                                // 専用のアップデートパネルを表示状態にする
                                UpdatePanel.Visibility = Visibility.Visible;

                                AddLog(string.Format(Localization.UpdateAvailableLog, cleanTag));
                                AddLog(Localization.UpdateAccessBooth);
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // オフライン時やAPI制限時などにアプリが落ちないよう、エラーは握り潰す（何もしない）
            }
        }
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true // これを true にしないと .NET Core/5+ ではブラウザが開きません
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AddLog(string.Format(Localization.ErrorBrowserFailed, ex.Message));
            }
        }

        private string GetStartupShortcutPath()
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(startupFolder, "Pico Mic Drainer.lnk");
        }

        /// <summary>スタートアップショートカットが実際に存在するか（＝登録済みか）を安全に確認する。</summary>
        private bool IsStartupShortcutPresent()
        {
            try
            {
                return File.Exists(GetStartupShortcutPath());
            }
            catch (Exception)
            {
                // 起動フォルダの取得失敗等は「未登録」と扱う
                return false;
            }
        }

        /// <summary>チェックボックスとパネルを実態（isRegistered）に合わせて復元する。</summary>
        private void RevertStartupUi(bool isRegistered)
        {
            _isUpdatingStartupUI = true;
            StartupCheck.IsChecked = isRegistered;
            StartupPromptPanel.Visibility = isRegistered ? Visibility.Collapsed : Visibility.Visible;
            _isUpdatingStartupUI = false;
        }

        // 起動時のチェック処理
        private void CheckStartupStatus()
        {
            _isUpdatingStartupUI = true;

            bool isRegistered = IsStartupShortcutPresent();
            StartupCheck.IsChecked = isRegistered;

            // 未登録(false)ならパネルを表示、登録済み(true)なら隠す
            StartupPromptPanel.Visibility = isRegistered ? Visibility.Collapsed : Visibility.Visible;

            _isUpdatingStartupUI = false;

            // ウィンドウの生成処理が完了した直後に実行されるよう、
            // Dispatcher.InvokeAsync を使って表示処理をスケジュールします。
            if (!isRegistered)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate(); // ウィンドウをアクティブ（最前面）にする

                    // バグ8修正：再表示に合わせて描画フレーム同期イベントを再接続する
                    HookRendering(true);
                });
            }
        }

        // チェックボックスがクリックされた時の処理
        private void StartupCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingStartupUI) return;

            bool isEnabled = StartupCheck.IsChecked ?? false;

            StartupPromptPanel.Visibility = isEnabled ? Visibility.Collapsed : Visibility.Visible;

            string shortcutPath = GetStartupShortcutPath();

            // バグ4修正：exe パスを取得できない場合はエラーをログにし、チェックを実態に合わせて戻す
            // （従来は無言で return し、チェックは ON のまま何もしない状態になっていた）
            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                AddLog(Localization.ErrorStartupExeNotFound);
                RevertStartupUi(IsStartupShortcutPresent());
                return;
            }

            try
            {
                if (isEnabled)
                {
                    // WScript.Shellを動的に呼び出してショートカットを作成
                    Type? t = Type.GetTypeFromProgID("WScript.Shell");
                    if (t == null)
                    {
                        AddLog(Localization.ErrorStartupComUnavailable);
                        RevertStartupUi(IsStartupShortcutPresent());
                        return;
                    }

                    dynamic shell = Activator.CreateInstance(t)!;
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = exePath;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                    shortcut.Description = "Pico Mic Drainer";
                    shortcut.Save();

                    AddLog(Localization.StartupRegistered);
                }
                else
                {
                    if (File.Exists(shortcutPath))
                    {
                        File.Delete(shortcutPath);
                        AddLog(Localization.StartupUnregistered);
                    }
                    // ショートカットが存在しない場合は、すでに「未登録」の状態と一致しているため何もしない
                }
            }
            catch (Exception ex)
            {
                // バグ4修正：例外時はショートカットの実在を確認してUIを実態に合わせて戻す
                // （例: 登録失敗→OFF / 削除失敗→実際にはまだ登録済みなのでON）
                RevertStartupUi(IsStartupShortcutPresent());
                AddLog(string.Format(Localization.ErrorStartupFailed, ex.Message));
            }
        }
    }
}
