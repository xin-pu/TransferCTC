using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace BltFileTransfer.Transfer
{
    public sealed class SerialTransferSender : IDisposable
    {
        private SerialPort _port;
        private CancellationTokenSource _cts;

        public bool IsSending { get; private set; }

        public async Task SendAsync(
            string portName,
            int baudRate,
            string filePath,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (IsSending)
                throw new InvalidOperationException("Send already in progress.");
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("Port name is required.");
            if (!File.Exists(filePath))
                throw new FileNotFoundException("File not found.", filePath);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _port = new SerialPort(portName, baudRate)
            {
                ReadBufferSize = 16384,
                WriteBufferSize = 16384
            };

            IsSending = true;
            try
            {
                await Task.Run(() => SendCore(filePath, progress, token), token).ConfigureAwait(false);
            }
            finally
            {
                ClosePort();
                IsSending = false;
            }
        }

        public void Cancel()
        {
            _cts?.Cancel();
        }

        private void SendCore(string filePath, IProgress<double> progress, CancellationToken token)
        {
            _port.Open();
            var fileName = Path.GetFileName(filePath);
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;
            uint rollingCrc = 0xFFFFFFFF;
            var crcTable = BuildCrcTable();
            long bytesSent = 0;
            var readBuffer = new byte[TransferProtocol.DefaultChunkSize];

            WriteFrame(TransferProtocol.BuildStartFrame(fileName, fileSize), token);

            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while ((read = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    rollingCrc = UpdateCrc(rollingCrc, readBuffer, 0, read, crcTable);

                    var dataFrame = TransferProtocol.BuildDataFrame(bytesSent, readBuffer, 0, read);
                    WriteFrame(dataFrame, token);
                    bytesSent += read;

                    progress?.Report(fileSize == 0 ? 100 : bytesSent * 100.0 / fileSize);
                    WaitForWriteBuffer(token);
                }
            }

            var fileCrc = rollingCrc ^ 0xFFFFFFFF;
            WriteFrame(TransferProtocol.BuildEndFrame(fileSize, fileCrc), token);
            progress?.Report(100);
        }

        private void WriteFrame(byte[] frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _port.Write(frame, 0, frame.Length);
            WaitForWriteBuffer(token);
        }

        private void WaitForWriteBuffer(CancellationToken token)
        {
            while (_port.BytesToWrite > TransferProtocol.WriteBufferThreshold)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(1);
            }
        }

        private void ClosePort()
        {
            if (_port == null) return;
            try
            {
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
            Cancel();
            ClosePort();
            _cts?.Dispose();
        }
    }
}
