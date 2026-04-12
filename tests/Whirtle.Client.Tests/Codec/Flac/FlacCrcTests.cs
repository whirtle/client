// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacCrcTests
{
    // ASCII "123456789" used as a portable test vector (same bytes across platforms).
    private static readonly byte[] Digits = "123456789"u8.ToArray();

    // -----------------------------------------------------------------------
    // CRC-8  (poly 0x07, init 0x00, no reflection)
    // -----------------------------------------------------------------------

    [Fact]
    public void Crc8_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0x00, FlacCrc.ComputeCrc8([]));
    }

    [Fact]
    public void Crc8_SingleZeroByte_ReturnsZero()
    {
        // table[0 ^ 0x00] = table[0] = 0
        Assert.Equal(0x00, FlacCrc.ComputeCrc8([0x00]));
    }

    [Fact]
    public void Crc8_SingleByte_0x01_Returns_0x07()
    {
        // Hand-computed: shift 0x01 left 8 times through poly 0x07,
        // which is equivalent to x^8 mod poly = x^2 + x + 1 = 0x07.
        Assert.Equal(0x07, FlacCrc.ComputeCrc8([0x01]));
    }

    [Fact]
    public void Crc8_AllOnesBlock_IsNotZero()
    {
        // Sanity: a non-trivial input should produce a non-zero CRC.
        Assert.NotEqual(0x00, FlacCrc.ComputeCrc8([0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Crc8_Digits123456789_Returns_0xF4()
    {
        // Standard CRC-8/SMBUS check value (poly=0x07, init=0x00, no reflection).
        // Verified against multiple independent implementations.
        Assert.Equal(0xF4, FlacCrc.ComputeCrc8(Digits));
    }

    [Fact]
    public void Crc8_IncrementalMatchesBatch()
    {
        byte batch       = FlacCrc.ComputeCrc8(Digits);
        byte incremental = 0;
        foreach (byte b in Digits)
            incremental = FlacCrc.UpdateCrc8(incremental, b);

        Assert.Equal(batch, incremental);
    }

    [Fact]
    public void Crc8_Update_ChainedFromNonZeroAccumulator()
    {
        // Compute CRC-8 of [A, B] in two ways:
        //   1. As a single batch over [A, B].
        //   2. Batch over [A], then Update with B.
        byte[] ab = [0x31, 0x32];
        byte expected = FlacCrc.ComputeCrc8(ab);
        byte crc      = FlacCrc.ComputeCrc8(ab[..1]);
        crc           = FlacCrc.UpdateCrc8(crc, 0x32);

        Assert.Equal(expected, crc);
    }

    // -----------------------------------------------------------------------
    // CRC-16  (poly 0x8005, init 0x0000, no reflection)
    // -----------------------------------------------------------------------

    [Fact]
    public void Crc16_EmptyInput_ReturnsZero()
    {
        Assert.Equal(0x0000, FlacCrc.ComputeCrc16([]));
    }

    [Fact]
    public void Crc16_SingleZeroByte_ReturnsZero()
    {
        // table[0] = 0 for poly 0x8005; update = (0<<8) ^ 0 = 0.
        Assert.Equal(0x0000, FlacCrc.ComputeCrc16([0x00]));
    }

    [Fact]
    public void Crc16_SingleByte_0x01_Returns_0x8005()
    {
        // Hand-computed: table[1] for poly 0x8005.
        // 0x01 << 8 = 0x0100; shift left 7 more times without hitting MSB → 0x8000;
        // MSB set: (0x8000 << 1) & 0xFFFF = 0x0000, XOR 0x8005 = 0x8005.
        // table[1] = 0x8005.
        // Update(0, 0x01) = (0<<8) ^ table[(0>>8)^0x01] = table[1] = 0x8005.
        Assert.Equal(0x8005, FlacCrc.ComputeCrc16([0x01]));
    }

    [Fact]
    public void Crc16_AllOnesBlock_IsNotZero()
    {
        Assert.NotEqual(0x0000, FlacCrc.ComputeCrc16([0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Crc16_Digits123456789_Returns_0xFEE8()
    {
        // Standard CRC-16/LHA check value (poly=0x8005, init=0x0000, no reflection).
        Assert.Equal(0xFEE8, FlacCrc.ComputeCrc16(Digits));
    }

    [Fact]
    public void Crc16_IncrementalMatchesBatch()
    {
        ushort batch       = FlacCrc.ComputeCrc16(Digits);
        ushort incremental = 0;
        foreach (byte b in Digits)
            incremental = FlacCrc.UpdateCrc16(incremental, b);

        Assert.Equal(batch, incremental);
    }

    [Fact]
    public void Crc16_Update_ChainedFromNonZeroAccumulator()
    {
        byte[] ab = [0x31, 0x32];
        ushort expected = FlacCrc.ComputeCrc16(ab);
        ushort crc      = FlacCrc.ComputeCrc16(ab[..1]);
        crc             = FlacCrc.UpdateCrc16(crc, 0x32);

        Assert.Equal(expected, crc);
    }
}
