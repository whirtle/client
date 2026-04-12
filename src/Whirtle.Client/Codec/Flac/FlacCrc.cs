// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// CRC routines used by the FLAC format.
///
/// CRC-8  (polynomial x^8 + x^2 + x + 1, i.e. 0x07):
///   Covers the frame header bytes up to (but not including) the CRC-8 byte.
///   Initial value 0x00, no reflection.
///
/// CRC-16 (polynomial x^16 + x^15 + x^2 + 1, i.e. 0x8005):
///   Covers all bytes of a frame from the sync word through the last subframe
///   byte. The two-byte footer carries this value big-endian.
///   Initial value 0x0000, no reflection.
/// </summary>
internal static class FlacCrc
{
    // -----------------------------------------------------------------------
    // Lookup tables — computed once at type initialisation
    // -----------------------------------------------------------------------

    private static readonly byte[]   _crc8Table  = BuildCrc8Table();
    private static readonly ushort[] _crc16Table = BuildCrc16Table();

    private static byte[] BuildCrc8Table()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte crc = (byte)i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 0x80) != 0
                    ? (byte)((crc << 1) ^ 0x07)
                    : (byte)(crc  << 1);
            table[i] = crc;
        }
        return table;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)(i << 8);
            for (int j = 0; j < 8; j++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x8005)
                    : (ushort)(crc  << 1);
            table[i] = crc;
        }
        return table;
    }

    // -----------------------------------------------------------------------
    // CRC-8 API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds one byte into a running CRC-8 accumulator and returns the new value.
    /// Start with <c>crc = 0</c> and call this for each byte in the protected range.
    /// </summary>
    public static byte UpdateCrc8(byte crc, byte b) => _crc8Table[crc ^ b];

    /// <summary>Computes CRC-8 over <paramref name="data"/> from an initial value of 0.</summary>
    public static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
            crc = _crc8Table[crc ^ b];
        return crc;
    }

    // -----------------------------------------------------------------------
    // CRC-16 API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds one byte into a running CRC-16 accumulator and returns the new value.
    /// Start with <c>crc = 0</c> and call this for each byte in the protected range.
    /// </summary>
    public static ushort UpdateCrc16(ushort crc, byte b) =>
        (ushort)((crc << 8) ^ _crc16Table[((crc >> 8) ^ b) & 0xFF]);

    /// <summary>Computes CRC-16 over <paramref name="data"/> from an initial value of 0.</summary>
    public static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
            crc = (ushort)((crc << 8) ^ _crc16Table[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }
}
