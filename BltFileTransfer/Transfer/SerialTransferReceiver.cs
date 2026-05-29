using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace BltFileTransfer.Transfer
{
    public sealed class SerialTransferReceiver : IDisposable
    {
        private readonly TransferFrameParser _parser = new TransferFrameParser();
        private readonly ConcurrentQueue<byte[]> _incoming = new ConcurrentQueue<byte[]>();
        private SerialPort _port;
        private CancellationTokenSource _cts;
        private Task _processTask;
        private FileStream _currentFile;
        private string _currentPath;
        private long _expectedSize;
        private long _bytesReceived;
        private uint _rollingCrc = 0xFFFFFFFF;
        private uint[] _crcTable;
        private string _saveDirectory;

        public bool IsReceiving { get; private set; }

        public event Action<string> Message;
        public event Action<double> ProgressChanged;
        public event Action<string> FileCompleted;
        public event Action<string, string> FileFailed;

        public void Start(string portName, int baudRate, string saveDirectory)
        {
            if (IsReceiving)
                throw new InvalidOperationException("Receive already in progress.");
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("Port name is required.");
            if (string.IsNullOrWhiteSpace(saveDirectory))
                throw new ArgumentException("Save directory is required.");

            if (!Directory.Exists(saveDirectory))
                Directory.CreateDirectory(saveDirectory);

            _saveDirectory = saveDirectory;
            _crcTable = BuildCrcTable();
            _parser.FrameReceived += OnFrameReceived;
            _parser.Reset();

            _port = new SerialPort(portName, baudRate)
            {
                ReadBufferSize = 16384,
                WriteBufferSize = 16384
            };
            _port.DataReceived += Port_DataReceived;

            _cts = new CancellationTokenSource();
            _port.Open();
            IsReceiving = true;
            _processTask = Task.Run(() => ProcessLoop(_cts.Token));
            Message?.Invoke("开始接收，等待数据…");
        }

        public void Stop()
        {
            if (!IsReceiving) return;

            _cts?.Cancel();
            try
            {
                _processTask?.Wait(2000);
            }
            catch
            {
            }

            ClosePort();
            CloseCurrentFile();
            _parser.FrameReceived -= OnFrameReceived;
            _parser.Reset();
            IsReceiving = false;
            Message?.Invoke("接收已停止。");
        }

        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                var length = sp.BytesToRead;
                if (length <= 0) return;
                var buffer = new byte[length];
                sp.Read(buffer, 0, length);
                _incoming.Enqueue(buffer);
            }
            catch (Exception ex)
            {
                Message?.Invoke("读取串口失败: " + ex.Message);
            }
        }

        private void ProcessLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                byte[] chunk;
                if (_incoming.TryDequeue(out chunk))
                    _parser.Append(chunk);
                else
                    Thread.Sleep(2);
            }

            byte[] remaining;
            while (_incoming.TryDequeue(out remaining))
                _parser.Append(remaining);
        }

        private void OnFrameReceived(TransferFrame frame)
        {
            try
            {
                switch (frame.Type)
                {
                    case FrameType.Start:
                        HandleStart(frame.Start);
                        break;
                    case FrameType.Data:
                        HandleData(frame.Data);
                        break;
                    case FrameType.End:
                        HandleEnd(frame.End);
                        break;
                }
            }
            catch (Exception ex)
            {
                Message?.Invoke("处理帧失败: " + ex.Message);
                AbortCurrentFile("处理异常");
            }
        }

        private void HandleStart(StartFramePayload start)
        {
            CloseCurrentFile();

            var safeName = TransferProtocol.SanitizeFileName(start.FileName);
            _currentPath = Path.Combine(_saveDirectory, safeName);
            if (File.Exists(_currentPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(safeName);
                var ext = Path.GetExtension(safeName);
                _currentPath = Path.Combine(_saveDirectory,
                    baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext);
            }

            _expectedSize = start.FileSize;
            _bytesReceived = 0;
            _rollingCrc = 0xFFFFFFFF;

            _currentFile = new FileStream(_currentPath, FileMode.Create, FileAccess.Write, FileShare.None);
            Message?.Invoke("开始接收文件: " + Path.GetFileName(_currentPath) + " (" + _expectedSize + " 字节)");
            ProgressChanged?.Invoke(0);
        }

        private void HandleData(DataFramePayload data)
        {
            if (_currentFile == null || data.Data == null || data.Data.Length == 0)
                return;

            _currentFile.Position = data.Offset;
            _currentFile.Write(data.Data, 0, data.Data.Length);
            _rollingCrc = UpdateCrc(_rollingCrc, data.Data, 0, data.Data.Length, _crcTable);
            _bytesReceived = Math.Max(_bytesReceived, data.Offset + data.Data.Length);

            if (_expectedSize > 0)
                ProgressChanged?.Invoke(_bytesReceived * 100.0 / _expectedSize);
        }

        private void HandleEnd(EndFramePayload end)
        {
            if (_currentFile == null)
                return;

            CloseCurrentFile();
            var fileCrc = _rollingCrc ^ 0xFFFFFFFF;
            var fileName = Path.GetFileName(_currentPath);

            if (_bytesReceived != end.FileSize || fileCrc != end.FileCrc32)
            {
                var reason = "校验失败 (期望大小 " + end.FileSize + ", 实际 " + _bytesReceived
                    + "; 期望 CRC " + end.FileCrc32.ToString("X8") + ", 实际 " + fileCrc.ToString("X8") + ")";
                var failedPath = _currentPath + "_failed";
                try
                {
                    if (File.Exists(_currentPath))
                        File.Move(_currentPath, failedPath);
                }
                catch
                {
                }
                FileFailed?.Invoke(fileName, reason);
                Message?.Invoke("文件 " + fileName + " " + reason);
            }
            else
            {
                FileCompleted?.Invoke(_currentPath);
                Message?.Invoke("文件接收完成: " + _currentPath);
                ProgressChanged?.Invoke(100);
            }

            _currentPath = null;
            _expectedSize = 0;
            _bytesReceived = 0;
        }

        private void AbortCurrentFile(string reason)
        {
            var path = _currentPath;
            CloseCurrentFile();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            if (!string.IsNullOrEmpty(path))
                FileFailed?.Invoke(Path.GetFileName(path), reason);
        }

        private void CloseCurrentFile()
        {
            if (_currentFile == null) return;
            try
            {
                _currentFile.Flush();
                _currentFile.Dispose();
            }
            catch
            {
            }
            _currentFile = null;
        }

        private void ClosePort()
        {
            if (_port == null) return;
            try
            {
                _port.DataReceived -= Port_DataReceived;
                if (_port.IsOpen)
                    _port.Close();
            }
            catch
            {
            }
            _port.Dispose();
            _port = null;
        }

        private static uint[] BuildCrcTable()
        {
            const uint polynomial = 0xEDB88320;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                        crc = (crc >> 1) ^ polynomial;
                    else
                        crc >>= 1;
                }
                table[i] = crc;
            }
            return table;
        }

        private static uint UpdateCrc(uint crc, byte[] data, int offset, int count, uint[] table)
        {
            for (int i = offset; i < offset + count; i++)
                crc = (crc >> 8) ^ table[(crc ^ data[i]) & 0xFF];
            return crc;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
