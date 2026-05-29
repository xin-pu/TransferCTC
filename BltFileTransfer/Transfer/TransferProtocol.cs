using System;
using System.Collections.Generic;
using System.Text;

namespace BltFileTransfer.Transfer
{
    public enum FrameType : byte
    {
        Start = 0x01,
        Data = 0x02,
        End = 0x03
    }

    public sealed class StartFramePayload
    {
        public long FileSize { get; set; }
        public string FileName { get; set; }
    }

    public sealed class DataFramePayload
    {
        public long Offset { get; set; }
        public byte[] Data { get; set; }
    }

    public sealed class EndFramePayload
    {
        public long FileSize { get; set; }
        public uint FileCrc32 { get; set; }
    }

    public sealed class TransferFrame
    {
        public FrameType Type { get; set; }
        public StartFramePayload Start { get; set; }
        public DataFramePayload Data { get; set; }
        public EndFramePayload End { get; set; }
    }

    internal static class BinaryIO
    {
        public static void WriteUInt16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        public static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static void WriteInt64(byte[] buffer, int offset, long value)
        {
            WriteUInt32(buffer, offset, (uint)(value & 0xFFFFFFFF));
            WriteUInt32(buffer, offset + 4, (uint)((value >> 32) & 0xFFFFFFFF));
        }

        public static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }

        public static uint ReadUInt32(IList<byte> buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        public static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        public static long ReadInt64(byte[] buffer, int offset)
        {
            var low = ReadUInt32(buffer, offset);
            var high = ReadUInt32(buffer, offset + 4);
            return (long)((ulong)high << 32 | low);
        }
    }

    public static class TransferProtocol
    {
        public static readonly byte[] Magic = { 0x42, 0x4C, 0x54, 0x46 };
        public const int HeaderSize = 9;
        public const int CrcSize = 4;
        public const int MinFrameSize = HeaderSize + CrcSize;
        public const int DefaultChunkSize = 4096;
        public const int WriteBufferThreshold = 8192;

        public static byte[] BuildStartFrame(string fileName, long fileSize)
        {
            var nameBytes = Encoding.UTF8.GetBytes(fileName ?? string.Empty);
            if (nameBytes.Length > ushort.MaxValue)
                throw new ArgumentException("File name too long.");

            var payload = new byte[8 + 2 + nameBytes.Length];
            BinaryIO.WriteInt64(payload, 0, fileSize);
            BinaryIO.WriteUInt16(payload, 8, (ushort)nameBytes.Length);
            Buffer.BlockCopy(nameBytes, 0, payload, 10, nameBytes.Length);
            return BuildFrame(FrameType.Start, payload);
        }

        public static byte[] BuildDataFrame(long offset, byte[] data, int offsetInBuffer, int count)
        {
            var payload = new byte[8 + count];
            BinaryIO.WriteInt64(payload, 0, offset);
            Buffer.BlockCopy(data, offsetInBuffer, payload, 8, count);
            return BuildFrame(FrameType.Data, payload);
        }

        public static byte[] BuildEndFrame(long fileSize, uint fileCrc32)
        {
            var payload = new byte[12];
            BinaryIO.WriteInt64(payload, 0, fileSize);
            BinaryIO.WriteUInt32(payload, 8, fileCrc32);
            return BuildFrame(FrameType.End, payload);
        }

        public static byte[] BuildFrame(FrameType type, byte[] payload)
        {
            var payloadLen = payload?.Length ?? 0;
            var frame = new byte[HeaderSize + payloadLen + CrcSize];
            Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
            frame[4] = (byte)type;
            BinaryIO.WriteUInt32(frame, 5, (uint)payloadLen);
            if (payloadLen > 0)
                Buffer.BlockCopy(payload, 0, frame, HeaderSize, payloadLen);
            var crc = Crc32Helper.Compute(payload ?? new byte[0]);
            BinaryIO.WriteUInt32(frame, HeaderSize + payloadLen, crc);
            return frame;
        }

        public static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "received.bin";

            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(c, '_');

            return fileName.Trim();
        }
    }

    /// <summary>
    /// Incremental frame parser for serial receive buffer (handles sticky/half packets).
    /// </summary>
    public sealed class TransferFrameParser
    {
        private readonly List<byte> _buffer = new List<byte>();

        public event Action<TransferFrame> FrameReceived;

        public void Append(byte[] chunk)
        {
            if (chunk == null || chunk.Length == 0) return;
            _buffer.AddRange(chunk);
            TryParseFrames();
        }

        public void Reset()
        {
            _buffer.Clear();
        }

        private void TryParseFrames()
        {
            while (true)
            {
                if (_buffer.Count < TransferProtocol.MinFrameSize)
                    return;

                int magicIndex = FindMagicIndex();
                if (magicIndex < 0)
                {
                    _buffer.Clear();
                    return;
                }

                if (magicIndex > 0)
                    _buffer.RemoveRange(0, magicIndex);

                if (_buffer.Count < TransferProtocol.HeaderSize)
                    return;

                var payloadLen = BinaryIO.ReadUInt32(_buffer, 5);
                if (payloadLen > 10 * 1024 * 1024)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                var totalLen = TransferProtocol.HeaderSize + (int)payloadLen + TransferProtocol.CrcSize;
                if (_buffer.Count < totalLen)
                    return;

                var payload = new byte[payloadLen];
                if (payloadLen > 0)
                {
                    for (int i = 0; i < payloadLen; i++)
                        payload[i] = _buffer[TransferProtocol.HeaderSize + i];
                }

                var crcExpected = BinaryIO.ReadUInt32(_buffer, TransferProtocol.HeaderSize + (int)payloadLen);
                var crcActual = Crc32Helper.Compute(payload);
                if (crcExpected != crcActual)
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                var frameType = (FrameType)_buffer[4];
                TransferFrame frame;
                try
                {
                    frame = DecodePayload(frameType, payload);
                }
                catch
                {
                    _buffer.RemoveAt(0);
                    continue;
                }

                _buffer.RemoveRange(0, totalLen);
                FrameReceived?.Invoke(frame);
            }
        }

        private int FindMagicIndex()
        {
            for (int i = 0; i <= _buffer.Count - TransferProtocol.Magic.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < TransferProtocol.Magic.Length; j++)
                {
                    if (_buffer[i + j] != TransferProtocol.Magic[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static TransferFrame DecodePayload(FrameType type, byte[] payload)
        {
            var frame = new TransferFrame { Type = type };
            switch (type)
            {
                case FrameType.Start:
                    if (payload.Length < 10)
                        throw new InvalidOperationException("Invalid START payload.");
                    var nameLen = BinaryIO.ReadUInt16(payload, 8);
                    if (payload.Length < 10 + nameLen)
                        throw new InvalidOperationException("Invalid START payload.");
                    frame.Start = new StartFramePayload
                    {
                        FileSize = BinaryIO.ReadInt64(payload, 0),
                        FileName = Encoding.UTF8.GetString(payload, 10, nameLen)
                    };
                    break;
                case FrameType.Data:
                    if (payload.Length < 8)
                        throw new InvalidOperationException("Invalid DATA payload.");
                    frame.Data = new DataFramePayload
                    {
                        Offset = BinaryIO.ReadInt64(payload, 0),
                        Data = new byte[payload.Length - 8]
                    };
                    if (frame.Data.Data.Length > 0)
                        Buffer.BlockCopy(payload, 8, frame.Data.Data, 0, frame.Data.Data.Length);
                    break;
                case FrameType.End:
                    if (payload.Length < 12)
                        throw new InvalidOperationException("Invalid END payload.");
                    frame.End = new EndFramePayload
                    {
                        FileSize = BinaryIO.ReadInt64(payload, 0),
                        FileCrc32 = BinaryIO.ReadUInt32(payload, 8)
                    };
                    break;
                default:
                    throw new InvalidOperationException("Unknown frame type.");
            }
            return frame;
        }
    }
}
