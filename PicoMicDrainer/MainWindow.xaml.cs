using System;
using System.Windows;
using NAudio.Wave;
// タスクトレイ機能のために追加
using System.Drawing;
using System.Windows.Forms;

namespace PicoMicDrainer
{
    public partial class MainWindow : Window
    {
        private const string TargetDeviceKeyword = "PicoStreamingMicrophone";
        private WaveInEvent? _waveIn;
        private NotifyIcon? _notifyIcon;
        private bool _isExitMode = false; // 本当に終了するかどうかのフラグ

        public MainWindow()
        {
            InitializeComponent();
            SetupTrayIcon();
        }

        // タスクトレイアイコンの設定
        private void SetupTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            // Windows標準のアプリアイコンを使用
            _notifyIcon.Icon = SystemIcons.Application;
            _notifyIcon.Visible = true;
            _notifyIcon.Text = "PICO Mic Drainer";

            // アイコンをダブルクリックした時の処理（ウィンドウを表示）
            _notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            };

            // 右クリックメニューの設定
            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add("ログを表示", null, (s, e) =>
            {
                this.Show();
                this.WindowState = WindowState.Normal;
            });
            contextMenu.Items.Add("終了する", null, (s, e) =>
            {
                _isExitMode = true; // 終了フラグを立てる
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
                    WaveFormat = new WaveFormat(48000, 1),
                    BufferMilliseconds = 50
                };

                _waveIn.DataAvailable += (s, a) => { /* 何もしない（捨てる） */ };
                _waveIn.StartRecording();

                AddLog("\nマイクストリームの消費を開始しました！");
                AddLog("※ウィンドウを閉じても、タスクトレイで動作し続けます。");
            }
            catch (Exception ex)
            {
                AddLog($"\n[エラー] マイクのオープンに失敗しました: {ex.Message}");
            }
        }

        private void AddLog(string message)
        {
            LogText.Text += message + "\n";
        }

        // ウィンドウを閉じるときの処理
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExitMode)
            {
                // 右クリックから「終了する」を選んでいない場合（[X]ボタン等）は、ウィンドウを隠すだけ
                e.Cancel = true;
                this.Hide();
                return;
            }

            // 本当の終了処理
            if (_waveIn != null)
            {
                _waveIn.StopRecording();
                _waveIn.Dispose();
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false; // アイコンを消す
                _notifyIcon.Dispose();
            }
        }
    }
}