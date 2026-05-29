using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.IO.Ports;
using Microsoft.Win32;
using System.Windows.Forms;
using BltFileTransfer.Transfer;

namespace BltFileTransfer
{
    public partial class MainWindow : Window
    {
        private readonly SerialTransferSender _sender = new SerialTransferSender();
        private readonly SerialTransferReceiver _receiver = new SerialTransferReceiver();
        private CancellationTokenSource _sendCts;
        private bool _isSending;
        private int _lastLoggedSendPercent = -1;
        private int _lastLoggedReceivePercent = -1;

        public MainWindow()
        {
            InitializeComponent();
            InitialPortName();
            WireReceiverEvents();
            AppendLog("程序已启动。");
        }

        private void WireReceiverEvents()
        {
            _receiver.Message += msg => RunOnUi(() => AppendLog(msg));
            _receiver.ProgressChanged += p => RunOnUi(() =>
            {
                ProgressReceive.Value = p;
                LogProgress(ref _lastLoggedReceivePercent, p, "接收");
            });
            _receiver.FileCompleted += path => RunOnUi(() => AppendLog("接收成功: " + path));
            _receiver.FileFailed += (name, reason) => RunOnUi(() => AppendLog("接收失败 [" + name + "]: " + reason));
        }

        private void InitialPortName()
        {
            RefreshPortLists();

            var baudRateList = new List<int> { 9600, 115200, 921600 };
            ComboTxtBaudRate.ItemsSource = baudRateList;
            ComboTxtBaudRate_R.ItemsSource = baudRateList;
            ComboTxtBaudRate.SelectedIndex = 2;
            ComboTxtBaudRate_R.SelectedIndex = 2;

            Progress.Maximum = 100;
            ProgressReceive.Maximum = 100;

            var defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Transfer");
            TxtSaveDirectory.Text = defaultDir;
        }

        private void RefreshPortLists()
        {
            var portNames = SerialPort.GetPortNames().OrderBy(p => p).ToList();
            var sendPort = ComboTxtPortName.SelectedItem as string;
            var recvPort = ComboTxtPortName_R.SelectedItem as string;

            ComboTxtPortName.ItemsSource = portNames;
            ComboTxtPortName_R.ItemsSource = portNames;

            if (!string.IsNullOrEmpty(sendPort) && portNames.Contains(sendPort))
                ComboTxtPortName.SelectedItem = sendPort;
            else if (portNames.Count > 0)
                ComboTxtPortName.SelectedIndex = 0;

            if (!string.IsNullOrEmpty(recvPort) && portNames.Contains(recvPort))
                ComboTxtPortName_R.SelectedItem = recvPort;
            else if (portNames.Count > 0)
                ComboTxtPortName_R.SelectedIndex = 0;
        }

        private void BtnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            RefreshPortLists();
            AppendLog("已刷新串口列表。");
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending)
            {
                _sender.Cancel();
                _sendCts?.Cancel();
                AppendLog("正在取消发送…");
                return;
            }

            var path = SelectFileName();
            if (string.IsNullOrEmpty(path)) return;

            var portName = ComboTxtPortName.SelectedItem as string;
            if (string.IsNullOrEmpty(portName))
            {
                AppendLog("请选择发送端口。");
                return;
            }

            var baudRate = (int)ComboTxtBaudRate.SelectedItem;
            SetSendUiBusy(true);
            Progress.Value = 0;
            _lastLoggedSendPercent = -1;
            _sendCts = new CancellationTokenSource();

            try
            {
                AppendLog("开始发送: " + Path.GetFileName(path));
                var progress = new Progress<double>(p =>
                {
                    Progress.Value = p;
                    LogProgress(ref _lastLoggedSendPercent, p, "发送");
                });

                await _sender.SendAsync(portName, baudRate, path, progress, _sendCts.Token);
                AppendLog("发送完成。");
            }
            catch (OperationCanceledException)
            {
                AppendLog("发送已取消。");
            }
            catch (Exception ex)
            {
                AppendLog("发送失败: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "发送错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetSendUiBusy(false);
                _sendCts?.Dispose();
                _sendCts = null;
            }
        }

        private void BtnReceieve_Click(object sender, RoutedEventArgs e)
        {
            if (_receiver.IsReceiving)
            {
                _receiver.Stop();
                SetReceiveUiBusy(false);
                return;
            }

            var portName = ComboTxtPortName_R.SelectedItem as string;
            if (string.IsNullOrEmpty(portName))
            {
                AppendLog("请选择接收端口。");
                return;
            }

            var saveDir = TxtSaveDirectory.Text?.Trim();
            if (string.IsNullOrEmpty(saveDir))
            {
                AppendLog("请选择保存目录。");
                return;
            }

            try
            {
                var baudRate = (int)ComboTxtBaudRate_R.SelectedItem;
                ProgressReceive.Value = 0;
                _lastLoggedReceivePercent = -1;
                _receiver.Start(portName, baudRate, saveDir);
                SetReceiveUiBusy(true);
            }
            catch (Exception ex)
            {
                AppendLog("启动接收失败: " + ex.Message);
                System.Windows.MessageBox.Show(ex.Message, "接收错误", MessageBoxButton.OK, MessageBoxImage.Error);
                SetReceiveUiBusy(false);
            }
        }

        private void BtnBrowseSaveDir_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择接收文件保存目录";
                if (Directory.Exists(TxtSaveDirectory.Text))
                    dialog.SelectedPath = TxtSaveDirectory.Text;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    TxtSaveDirectory.Text = dialog.SelectedPath;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isSending || _receiver.IsReceiving)
            {
                var result = System.Windows.MessageBox.Show(
                    "正在传输数据，确定要退出吗？",
                    "确认退出",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _sender.Cancel();
            _sendCts?.Cancel();
            _receiver.Stop();
            _sender.Dispose();
            _receiver.Dispose();
        }

        private void SetSendUiBusy(bool busy)
        {
            _isSending = busy;
            BtnConnect.Content = busy ? "取消发送" : "选择文件并发送";
            ComboTxtPortName.IsEnabled = !busy;
            ComboTxtBaudRate.IsEnabled = !busy;
            BtnRefreshPorts.IsEnabled = !busy;
        }

        private void SetReceiveUiBusy(bool busy)
        {
            BtnReceieve.Content = busy ? "停止接收" : "开始接收";
            ComboTxtPortName_R.IsEnabled = !busy;
            ComboTxtBaudRate_R.IsEnabled = !busy;
            TxtSaveDirectory.IsEnabled = !busy;
            BtnBrowseSaveDir.IsEnabled = !busy;
        }

        private string SelectFileName()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "压缩文件|*.zip;*.jar",
                CheckFileExists = true
            };
            return dialog.ShowDialog() == true ? dialog.FileName : string.Empty;
        }

        private void AppendLog(string message)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            Log.AppendText(line);
            Log.ScrollToEnd();
        }

        private void LogProgress(ref int lastLogged, double percent, string label)
        {
            var bucket = (int)(percent / 5) * 5;
            if (bucket <= lastLogged && percent < 100) return;
            lastLogged = bucket;
            if (bucket % 5 == 0 || percent >= 100)
                AppendLog(label + "进度: " + percent.ToString("F0") + "%");
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.BeginInvoke(action);
        }
    }
}
