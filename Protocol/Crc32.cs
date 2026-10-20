using System;

namespace HandheldDsuServer.Protocol;

/// <summary>
/// Cemuhook DSU protokolü için standart IEEE 802.3 (0xEDB88320) CRC32 hesaplayıcısı.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        const uint polynomial = 0xEDB88320u;
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

    /// <summary>
    /// Verilen byte dizisinin CRC32 checksum değerini hesaplar.
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        for (int i = 0; i < data.Length; i++)
        {
            byte index = (byte)((crc ^ data[i]) & 0xFF);
            crc = (crc >> 8) ^ Table[index];
        }

        return ~crc;
    }
}
