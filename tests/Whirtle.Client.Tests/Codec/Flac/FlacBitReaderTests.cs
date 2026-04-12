// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacBitReaderTests
{
    // -----------------------------------------------------------------------
    // ReadBit / ReadBits — MSB-first ordering
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadBit_ReadsHighBitFirst()
    {
        // 0x80 = 1000_0000 — only the MSB is set
        var reader = Make(0x80);
        Assert.True(reader.ReadBit());   // bit 7 → 1
        Assert.False(reader.ReadBit());  // bit 6 → 0
    }

    [Fact]
    public void ReadBit_ReadsAllBitsOfOneByteMsbFirst()
    {
        // 0xAB = 1010_1011
        var reader   = Make(0xAB);
        bool[] expected = [true, false, true, false, true, false, true, true];
        bool[] actual   = new bool[8];
        for (int i = 0; i < 8; i++)
            actual[i] = reader.ReadBit();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ReadBits_8_ReturnsSameAsByteValue()
    {
        var reader = Make(0xAB);
        Assert.Equal(0xABu, reader.ReadBits(8));
    }

    [Fact]
    public void ReadBits_0_ReturnsZero()
    {
        var reader = Make(0xFF);
        Assert.Equal(0u, reader.ReadBits(0));
    }

    [Fact]
    public void ReadBits_4_ReturnsHighNibble()
    {
        // 0xAB = 1010_1011 → high nibble = 0xA = 10
        var reader = Make(0xAB);
        Assert.Equal(0xAu, reader.ReadBits(4));
    }

    [Fact]
    public void ReadBits_AcrossByteBoundary()
    {
        // bytes: 0b0000_0001, 0b1000_0000
        // bits (MSB-first): 0 0 0 0 0 | 0 0 1 | 1 0 0 0 0 0 0 0
        //                   (skip 5)    byte0   byte1
        // bits 5..10 = 0,0,1,1,0,0 = 0b001100 = 12
        var reader = Make(0x01, 0x80);
        reader.SkipBits(5);
        Assert.Equal(0b001100u, reader.ReadBits(6));
    }

    [Fact]
    public void ReadBits_16_SpansTwoBytes()
    {
        var reader = Make(0x12, 0x34);
        Assert.Equal(0x1234u, reader.ReadBits(16));
    }

    [Fact]
    public void ReadBits_32_SpansFourBytes()
    {
        var reader = Make(0xDE, 0xAD, 0xBE, 0xEF);
        Assert.Equal(0xDEADBEEFu, reader.ReadBits(32));
    }

    [Fact]
    public void ReadBits_SequentialCallsAccumulate()
    {
        // 0xAB = 1010_1011
        var reader = Make(0xAB);
        uint hi = reader.ReadBits(4); // 1010 = 0xA
        uint lo = reader.ReadBits(4); // 1011 = 0xB
        Assert.Equal(0xAu, hi);
        Assert.Equal(0xBu, lo);
    }

    [Fact]
    public void ReadBits_CountAbove32_Throws()
    {
        // ref struct cannot be captured in a lambda; use try/catch instead.
        var reader = Make(0x00, 0x00, 0x00, 0x00, 0x00);
        try
        {
            reader.ReadBits(33);
            Assert.Fail("Expected ArgumentOutOfRangeException.");
        }
        catch (ArgumentOutOfRangeException) { }
    }

    // -----------------------------------------------------------------------
    // ReadSignedBits
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadSignedBits_PositiveValue_Unchanged()
    {
        // 0x70 = 0111_0000; first 6 bits (MSB-first) = 0,1,1,1,0,0 = 0b011100 = 28
        // MSB of the 6-bit field is 0 → positive, no sign extension
        var reader = Make(0x70);
        Assert.Equal(28, reader.ReadSignedBits(6));
    }

    [Fact]
    public void ReadSignedBits_NegativeValue_SignExtended()
    {
        // Encode -1 as 4-bit two's complement: 0b1111 = 0xF (high nibble of 0xF0)
        var reader = Make(0xF0);
        Assert.Equal(-1, reader.ReadSignedBits(4));
    }

    [Fact]
    public void ReadSignedBits_MinValue_4Bit()
    {
        // 4-bit min = -8 = 0b1000 (high nibble of 0x80)
        var reader = Make(0x80);
        Assert.Equal(-8, reader.ReadSignedBits(4));
    }

    [Fact]
    public void ReadSignedBits_NegativeAcrossByteBoundary()
    {
        // 0x07 = 0000_0111, 0xC0 = 1100_0000
        // Bits (MSB-first): 0 0 0 0 0 | 1 1 1 | 1 1 0 0 0 0 0 0
        //                   (skip 5)    byte0    byte1
        // After skipping 5: next 5 bits = 1,1,1,1,1 = 0b11111 = -1 in 5-bit signed
        var reader = Make(0x07, 0xC0);
        reader.SkipBits(5);
        Assert.Equal(-1, reader.ReadSignedBits(5));
    }

    // -----------------------------------------------------------------------
    // ReadByte
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadByte_WhenAligned_ReturnsFullByte()
    {
        var reader = Make(0xCD);
        Assert.Equal(0xCD, reader.ReadByte());
    }

    [Fact]
    public void ReadByte_WhenUnaligned_ReadsCorrectBits()
    {
        // 0xAB 0xCD = 1010_1011 1100_1101
        // After reading 4 bits (0xA), reading a byte should give the next 8 bits:
        // 1011_1100 = 0xBC
        var reader = Make(0xAB, 0xCD);
        reader.ReadBits(4);
        Assert.Equal(0xBC, reader.ReadByte());
    }

    // -----------------------------------------------------------------------
    // AlignToByte
    // -----------------------------------------------------------------------

    [Fact]
    public void AlignToByte_WhenAligned_IsNoop()
    {
        var reader = Make(0xAB, 0xCD);
        reader.ReadBits(8);          // consume exactly one byte
        reader.AlignToByte();
        Assert.Equal(1, reader.BytePosition);
    }

    [Fact]
    public void AlignToByte_WhenUnaligned_AdvancesToNextByte()
    {
        var reader = Make(0xAB, 0xCD);
        reader.ReadBits(3);          // consume 3 bits — now at bit offset 3
        reader.AlignToByte();
        Assert.Equal(1, reader.BytePosition); // should have jumped to byte 1
    }

    [Fact]
    public void AlignToByte_ThenReadByte_GetsSecondByte()
    {
        var reader = Make(0xAB, 0xCD);
        reader.ReadBits(1);          // consume 1 bit of byte 0
        reader.AlignToByte();        // skip remaining 7 bits of byte 0
        Assert.Equal(0xCD, reader.ReadByte());
    }

    // -----------------------------------------------------------------------
    // BytePosition / BitsConsumed / IsAligned
    // -----------------------------------------------------------------------

    [Fact]
    public void BytePosition_StartsAtZero()
    {
        var reader = Make(0x00);
        Assert.Equal(0, reader.BytePosition);
    }

    [Fact]
    public void BytePosition_AfterReadingEightBits_IsOne()
    {
        var reader = Make(0xFF, 0x00);
        reader.ReadBits(8);
        Assert.Equal(1, reader.BytePosition);
    }

    [Fact]
    public void BitsConsumed_TracksExactCount()
    {
        var reader = Make(0xFF, 0xFF);
        reader.ReadBits(5);
        reader.ReadBits(3);
        Assert.Equal(8, reader.BitsConsumed);
    }

    [Fact]
    public void IsAligned_WhenOnByteBoundary_IsTrue()
    {
        var reader = Make(0xFF, 0xFF);
        reader.ReadBits(8);
        Assert.True(reader.IsAligned);
    }

    [Fact]
    public void IsAligned_WhenNotOnByteBoundary_IsFalse()
    {
        var reader = Make(0xFF);
        reader.ReadBits(3);
        Assert.False(reader.IsAligned);
    }

    // -----------------------------------------------------------------------
    // ReadBytes
    // -----------------------------------------------------------------------

    [Fact]
    public void ReadBytes_WhenAligned_ReturnsCorrectSlice()
    {
        var reader = Make(0xAA, 0xBB, 0xCC);
        reader.ReadBits(8);                   // consume first byte
        var slice = reader.ReadBytes(2);
        Assert.Equal([0xBB, 0xCC], slice.ToArray());
    }

    [Fact]
    public void ReadBytes_WhenUnaligned_Throws()
    {
        // ref struct cannot be captured in a lambda; use try/catch instead.
        var reader = Make(0xFF, 0xFF);
        reader.ReadBits(1);
        try
        {
            reader.ReadBytes(1);
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException) { }
    }

    // -----------------------------------------------------------------------
    // SkipBits
    // -----------------------------------------------------------------------

    [Fact]
    public void SkipBits_AdvancesBitOffset()
    {
        var reader = Make(0b10110000);
        reader.SkipBits(2);
        // remaining bits of byte 0: 1 1 0 0 0 0
        Assert.Equal(0b110000u, reader.ReadBits(6));
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static FlacBitReader Make(params byte[] bytes) => new(bytes);
}
