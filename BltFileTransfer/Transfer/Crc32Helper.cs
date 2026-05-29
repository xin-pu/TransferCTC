using System;

namespace BltFileTransfer.Transfer
{
    /// <summary>
    /// IEEE CRC-32 (same polynomial as ZIP).
    /// </summary>
    public static class Crc32Helper
    {
        private static readonly uint[] Table = CreateTable();

        private static uint[] CreateTable()
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

        public static uint Compute(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            return crc ^ 0xFFFFFFFF;
        }

        public static uint Compute(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            return Compute(data, 0, data.Length);
        }
    }
}
