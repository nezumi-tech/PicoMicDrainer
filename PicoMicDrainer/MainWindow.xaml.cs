using System;
using System.Windows;
using System.Windows.Media; // CompositionTarget のために追加
using NAudio.Wave;
using System.Drawing;
using System.Windows.Forms;

namespace PicoMicDrainer
{
    public partial class MainWindow : Window
    {
        private const string TargetDeviceKeyword = "PicoStreaming";
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

            // WPFの描画フレーム同期イベントを登録（メーターの滑らかな更新用）
            CompositionTarget.Rendering += OnRendering;
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

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AddLog("--- PICO Connect マイクバッファ消費ツール ---");
            AddLog("デバイスを検索中...");
            StartDraining();
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
        private void OnRendering(object sender, EventArgs e)
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
            LogText.Text += message + "\n";
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitMode)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }

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
    }
}