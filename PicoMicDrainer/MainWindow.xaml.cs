using System;
using System.Windows;
using System.Windows.Media; // CompositionTarget のために追加
using NAudio.Wave;
using System.Drawing;
using System.Windows.Forms;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Reflection;
using System.Diagnostics;

namespace PicoMicDrainer
{
    public partial class MainWindow : Window
    {
        private const string TargetDeviceKeyword = "PicoStreaming";

        private const string GithubOwner = "nezumi-tech";
        private const string GithubRepo = "PicoMicDrainer";

        private WaveInEvent? _waveIn;
        private NotifyIcon? _notifyIcon;
        private bool _isExitMode = false;

        // 可視化用の変数
        private volatile bool _isVisualizerEnabled = false;
        private float _latestVolumePeak = 0f;

        public MainWindow()
        {
            InitializeComponent();
            SetupTrayIcon();

            // WPFの描画フレーム同期イベントを登録
            CompositionTarget.Rendering += OnRendering;

            // 画面の表示状態に関わらず、生成と同時に起動処理を走らせる
            _ = StartupProcessAsync();
        }

        private void SetupTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "PICO Mic Drainer";

            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            };

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("ログを表示", null, (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            });
            contextMenu.Items.Add("終了する", null, (s, e) =>
            {
                _isExitMode = true;
                System.Windows.Application.Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        private async Task StartupProcessAsync()
        {
            // 少しだけ待機（UIスレッドの初期化を確実に終わらせるための安全策）
            await Task.Delay(500);

            AddLog("--- PICO Connect マイクバッファ消費ツール ---");
            AddLog("デバイスを検索中...");
            StartDraining();

            await CheckForUpdatesAsync();
        }

        private void StartDraining()
        {
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
                AddLog($"\n[エラー] '{TargetDeviceKeyword}' を含むマイクが見つかりませんでした。");
                return;
            }

            var deviceInfo = WaveIn.GetCapabilities(deviceNumber);
            AddLog($"\n[成功] 対象デバイスを検出しました: {deviceInfo.ProductName}");

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

                _waveIn.StartRecording();

                AddLog("\nマイクストリームの消費を開始しました！");
            }
            catch (Exception ex)
            {
                AddLog($"\n[エラー] マイクのオープンに失敗しました: {ex.Message}");
            }
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
            // UIを操作するため、確実にUIスレッド上で実行する
            Dispatcher.Invoke(() =>
            {
                // ログのテキストを追記
                LogText.Text += message + "\n";

                // ★追加：ScrollViewer を自動的に一番下までスクロールさせる
                LogScrollViewer.ScrollToEnd();
            });
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

                return;
            }

            // 本当の終了処理
            CompositionTarget.Rendering -= OnRendering;

            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
            }
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
                            Dispatcher.Invoke(() =>
                            {
                                // ウィンドウを隠し状態から復帰させる
                                this.Show();
                                this.WindowState = WindowState.Normal;
                                this.Activate();

                                // 専用のアップデートパネルを表示状態にする
                                UpdatePanel.Visibility = Visibility.Visible;

                                AddLog($"\n[通知] 最新バージョン (v{cleanTag}) が公開されました！");
                                AddLog("画面上部のリンクからBOOTHにアクセスしてください。");
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
                AddLog($"[エラー] ブラウザを開けませんでした: {ex.Message}");
            }
        }
    }
}