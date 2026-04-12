// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacFrameHeaderReaderTests
{
    // Default STREAMINFO used as fallback for "get from STREAMINFO" codes.
    private static readonly FlacStreamInfo DefaultInfo = new(
        MinBlockSize:  4096, MaxBlockSize:  4096,
        MinFrameSize:  0,    MaxFrameSize:  0,
        SampleRate:    44_100, Channels: 2, BitsPerSample: 16,
        TotalSamples:  0,    Md5Signature: new byte[16]);

    // -----------------------------------------------------------------------
    // Block size codes
    // -----------------------------------------------------------------------

    [Fact]
    public void BlockSize_Code1_Returns192()
    {
        var h = BuildFrameHeader(blockSizeCode: 1);
        Assert.Equal(192, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BlockSize);
    }

    [Theory]
    [InlineData(2,  576)]
    [InlineData(3, 1152)]
    [InlineData(4, 2304)]
    [InlineData(5, 4608)]
    public void BlockSize_Codes2to5_ReturnMultiplesOf576(int code, int expected)
    {
        var h = BuildFrameHeader(blockSizeCode: code);
        Assert.Equal(expected, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BlockSize);
    }

    [Fact]
    public void BlockSize_Code6_ReadsTailBytePlusOne()
    {
        // tail byte 0xFF → block size = 255 + 1 = 256
        var h = BuildFrameHeader(blockSizeCode: 6, blockSizeTail: [0xFF]);
        Assert.Equal(256, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BlockSize);
    }

    [Fact]
    public void BlockSize_Code7_ReadsTailUint16PlusOne()
    {
        // tail 0x270F → 9999 + 1 = 10000
        var h = BuildFrameHeader(blockSizeCode: 7, blockSizeTail: [0x27, 0x0F]);
        Assert.Equal(10_000, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BlockSize);
    }

    [Theory]
    [InlineData(0x8,  256)]
    [InlineData(0x9,  512)]
    [InlineData(0xA, 1024)]
    [InlineData(0xB, 2048)]
    [InlineData(0xC, 4096)]
    [InlineData(0xD, 8192)]
    [InlineData(0xE, 16384)]
    [InlineData(0xF, 32768)]
    public void BlockSize_Codes8to15_ReturnMultiplesOf256(int code, int expected)
    {
        var h = BuildFrameHeader(blockSizeCode: code);
        Assert.Equal(expected, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BlockSize);
    }

    [Fact]
    public void BlockSize_Code0_Throws()
    {
        var h = BuildFrameHeader(blockSizeCode: 0);
        Assert.Throws<InvalidDataException>(() => FlacFrameHeaderReader.Read(h, DefaultInfo, out _));
    }

    // -----------------------------------------------------------------------
    // Sample rate codes
    // -----------------------------------------------------------------------

    [Fact]
    public void SampleRate_Code0_FallsBackToStreamInfo()
    {
        var info = DefaultInfo with { SampleRate = 96_000 };
        var h    = BuildFrameHeader(sampleRateCode: 0);
        Assert.Equal(96_000, FlacFrameHeaderReader.Read(h, info, out _).SampleRate);
    }

    [Theory]
    [InlineData(1,  88_200)]
    [InlineData(2, 176_400)]
    [InlineData(3, 192_000)]
    [InlineData(4,   8_000)]
    [InlineData(5,  16_000)]
    [InlineData(6,  22_050)]
    [InlineData(7,  24_000)]
    [InlineData(8,  32_000)]
    [InlineData(9,  44_100)]
    [InlineData(10, 48_000)]
    [InlineData(11, 96_000)]
    public void SampleRate_Codes1to11_LookupTable(int code, int expected)
    {
        var h = BuildFrameHeader(sampleRateCode: code);
        Assert.Equal(expected, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).SampleRate);
    }

    [Fact]
    public void SampleRate_Code12_ReadsTailKilohertz()
    {
        // tail byte 44 → 44 * 1000 = 44000 Hz
        var h = BuildFrameHeader(sampleRateCode: 12, sampleRateTail: [44]);
        Assert.Equal(44_000, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).SampleRate);
    }

    [Fact]
    public void SampleRate_Code13_ReadsTailHertz()
    {
        // tail 0xAC 0x44 → 44100 Hz
        var h = BuildFrameHeader(sampleRateCode: 13, sampleRateTail: [0xAC, 0x44]);
        Assert.Equal(44_100, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).SampleRate);
    }

    [Fact]
    public void SampleRate_Code14_ReadsTailTensOfHertz()
    {
        // tail for 44100 Hz: 44100 / 10 = 4410 = 0x113A
        var h = BuildFrameHeader(sampleRateCode: 14, sampleRateTail: [0x11, 0x3A]);
        Assert.Equal(44_100, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).SampleRate);
    }

    [Fact]
    public void SampleRate_Code15_Throws()
    {
        // code 15 is invalid
        var h = BuildFrameHeader(sampleRateCode: 15);
        Assert.Throws<InvalidDataException>(() => FlacFrameHeaderReader.Read(h, DefaultInfo, out _));
    }

    // -----------------------------------------------------------------------
    // Channel assignment codes
    // -----------------------------------------------------------------------

    [Fact]
    public void ChannelCode_0x0_Mono_Independent()
    {
        var h    = BuildFrameHeader(channelCode: 0x0);
        var hdr  = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.Independent, hdr.ChannelAssignment);
        Assert.Equal(1, hdr.Channels);
    }

    [Fact]
    public void ChannelCode_0x1_Stereo_Independent()
    {
        var h    = BuildFrameHeader(channelCode: 0x1);
        var hdr  = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.Independent, hdr.ChannelAssignment);
        Assert.Equal(2, hdr.Channels);
    }

    [Fact]
    public void ChannelCode_0x7_EightChannels_Independent()
    {
        var h    = BuildFrameHeader(channelCode: 0x7);
        var hdr  = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.Independent, hdr.ChannelAssignment);
        Assert.Equal(8, hdr.Channels);
    }

    [Fact]
    public void ChannelCode_0x8_LeftSide()
    {
        var h   = BuildFrameHeader(channelCode: 0x8);
        var hdr = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.LeftSide, hdr.ChannelAssignment);
        Assert.Equal(2, hdr.Channels);
    }

    [Fact]
    public void ChannelCode_0x9_RightSide()
    {
        var h   = BuildFrameHeader(channelCode: 0x9);
        var hdr = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.RightSide, hdr.ChannelAssignment);
        Assert.Equal(2, hdr.Channels);
    }

    [Fact]
    public void ChannelCode_0xA_MidSide()
    {
        var h   = BuildFrameHeader(channelCode: 0xA);
        var hdr = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(ChannelAssignment.MidSide, hdr.ChannelAssignment);
        Assert.Equal(2, hdr.Channels);
    }

    // -----------------------------------------------------------------------
    // Sample size codes
    // -----------------------------------------------------------------------

    [Fact]
    public void SampleSize_Code0_FallsBackToStreamInfo()
    {
        var info = DefaultInfo with { BitsPerSample = 24 };
        var h    = BuildFrameHeader(sampleSizeCode: 0);
        Assert.Equal(24, FlacFrameHeaderReader.Read(h, info, out _).BitsPerSample);
    }

    [Theory]
    [InlineData(1,  8)]
    [InlineData(2, 12)]
    [InlineData(4, 16)]
    [InlineData(5, 20)]
    [InlineData(6, 24)]
    [InlineData(7, 32)]
    public void SampleSize_TableCodes_ReturnCorrectDepth(int code, int expected)
    {
        var h = BuildFrameHeader(sampleSizeCode: code);
        Assert.Equal(expected, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).BitsPerSample);
    }

    [Fact]
    public void SampleSize_Code3_Throws()
    {
        var h = BuildFrameHeader(sampleSizeCode: 3);
        Assert.Throws<InvalidDataException>(() => FlacFrameHeaderReader.Read(h, DefaultInfo, out _));
    }

    // -----------------------------------------------------------------------
    // Frame / sample number (UTF-8 coded integer)
    // -----------------------------------------------------------------------

    [Fact]
    public void FrameNumber_0_Fixed_Decoded()
    {
        var h   = BuildFrameHeader(frameOrSampleNumber: 0, variableBlockSize: false);
        var hdr = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(0L, hdr.FrameOrSampleNumber);
        Assert.False(hdr.IsVariableBlockSize);
    }

    [Fact]
    public void FrameNumber_1Byte_Max127_Decoded()
    {
        var h = BuildFrameHeader(frameOrSampleNumber: 127);
        Assert.Equal(127L, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).FrameOrSampleNumber);
    }

    [Fact]
    public void FrameNumber_2Byte_128_Decoded()
    {
        // 128 needs a 2-byte UTF-8 sequence: 0xC2 0x80
        var h = BuildFrameHeader(frameOrSampleNumber: 128);
        Assert.Equal(128L, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).FrameOrSampleNumber);
    }

    [Fact]
    public void FrameNumber_3Byte_2048_Decoded()
    {
        var h = BuildFrameHeader(frameOrSampleNumber: 2048);
        Assert.Equal(2048L, FlacFrameHeaderReader.Read(h, DefaultInfo, out _).FrameOrSampleNumber);
    }

    [Fact]
    public void SampleNumber_Variable_Decoded()
    {
        var h   = BuildFrameHeader(frameOrSampleNumber: 44_100, variableBlockSize: true);
        var hdr = FlacFrameHeaderReader.Read(h, DefaultInfo, out _);
        Assert.Equal(44_100L, hdr.FrameOrSampleNumber);
        Assert.True(hdr.IsVariableBlockSize);
    }

    // -----------------------------------------------------------------------
    // bytesConsumed
    // -----------------------------------------------------------------------

    [Fact]
    public void BytesConsumed_MinimalHeader_Is6()
    {
        // sync(2) + codes(2) + frame_number(1 byte for 0) + crc(1) = 6
        var h = BuildFrameHeader(frameOrSampleNumber: 0);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(6, consumed);
    }

    [Fact]
    public void BytesConsumed_With1ByteBlockSizeTail_Is7()
    {
        var h = BuildFrameHeader(blockSizeCode: 6, blockSizeTail: [0x00]);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(7, consumed);
    }

    [Fact]
    public void BytesConsumed_With2ByteBlockSizeTail_Is8()
    {
        var h = BuildFrameHeader(blockSizeCode: 7, blockSizeTail: [0x00, 0x00]);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(8, consumed);
    }

    [Fact]
    public void BytesConsumed_With1ByteSampleRateTail_Is7()
    {
        var h = BuildFrameHeader(sampleRateCode: 12, sampleRateTail: [44]);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(7, consumed);
    }

    [Fact]
    public void BytesConsumed_BothTails_Is10()
    {
        // block tail 2 bytes + sample rate tail 2 bytes → 6 + 2 + 2 = 10
        var h = BuildFrameHeader(
            blockSizeCode:  7, blockSizeTail:  [0x00, 0x01],
            sampleRateCode: 13, sampleRateTail: [0x00, 0x01]);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(10, consumed);
    }

    [Fact]
    public void BytesConsumed_PointsToFirstSubframeByte()
    {
        var h = Concat(BuildFrameHeader(), [0xAB]);
        FlacFrameHeaderReader.Read(h, DefaultInfo, out int consumed);
        Assert.Equal(0xAB, h[consumed]);
    }

    // -----------------------------------------------------------------------
    // Error cases
    // -----------------------------------------------------------------------

    [Fact]
    public void InvalidSync_Throws()
    {
        // 0xFF 0xF0 → sync bits read as 0x3FFC (last 2 bits differ from 0x3FFE)
        byte[] h = [0xFF, 0xF0, 0xC9, 0x18, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => FlacFrameHeaderReader.Read(h, DefaultInfo, out _));
    }

    [Fact]
    public void CrcMismatch_Throws()
    {
        var h = BuildFrameHeader().ToArray();
        h[^1] ^= 0xFF;   // corrupt the CRC byte
        Assert.Throws<InvalidDataException>(() => FlacFrameHeaderReader.Read(h, DefaultInfo, out _));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a complete, CRC-valid frame header byte array from semantic parameters.
    /// Callers supply only the fields they care about; the rest default to values
    /// that produce a valid, parseable header.
    /// </summary>
    private static byte[] BuildFrameHeader(
        bool    variableBlockSize  = false,
        int     blockSizeCode      = 0xC,    // 4096 samples
        int     sampleRateCode     = 9,      // 44100 Hz
        int     channelCode        = 0x1,    // stereo independent
        int     sampleSizeCode     = 4,      // 16-bit
        long    frameOrSampleNumber = 0,
        byte[]? blockSizeTail      = null,
        byte[]? sampleRateTail     = null)
    {
        var buf = new List<byte>();

        // Sync code (14 bits of 1) + reserved(0) + blocking strategy
        buf.Add(0xFF);
        buf.Add((byte)(0xF8 | (variableBlockSize ? 1 : 0)));

        // Block size code (high nibble) + sample rate code (low nibble)
        buf.Add((byte)((blockSizeCode << 4) | (sampleRateCode & 0xF)));

        // Channel assignment (high nibble) + sample size code (bits 3–1) + reserved(0)
        buf.Add((byte)((channelCode << 4) | (sampleSizeCode << 1)));

        // Frame/sample number — UTF-8 coded integer
        buf.AddRange(EncodeUtf8Int(frameOrSampleNumber));

        // Optional block-size tail
        if (blockSizeTail is not null) buf.AddRange(blockSizeTail);

        // Optional sample-rate tail
        if (sampleRateTail is not null) buf.AddRange(sampleRateTail);

        // CRC-8 over all preceding bytes
        buf.Add(FlacCrc.ComputeCrc8(buf.ToArray()));

        return [..buf];
    }

    /// <summary>Encodes a non-negative integer using the UTF-8 multi-byte scheme.</summary>
    private static byte[] EncodeUtf8Int(long value)
    {
        if (value < 0x80)
            return [(byte)value];
        if (value < 0x800)
            return [(byte)(0xC0 | (value >> 6)),
                    (byte)(0x80 | (value & 0x3F))];
        if (value < 0x10000)
            return [(byte)(0xE0 | (value >> 12)),
                    (byte)(0x80 | ((value >> 6)  & 0x3F)),
                    (byte)(0x80 | ( value         & 0x3F))];
        if (value < 0x200000)
            return [(byte)(0xF0 | (value >> 18)),
                    (byte)(0x80 | ((value >> 12) & 0x3F)),
                    (byte)(0x80 | ((value >> 6)  & 0x3F)),
                    (byte)(0x80 | ( value         & 0x3F))];
        if (value < 0x4000000)
            return [(byte)(0xF8 | (value >> 24)),
                    (byte)(0x80 | ((value >> 18) & 0x3F)),
                    (byte)(0x80 | ((value >> 12) & 0x3F)),
                    (byte)(0x80 | ((value >> 6)  & 0x3F)),
                    (byte)(0x80 | ( value         & 0x3F))];
        if (value < 0x80000000L)
            return [(byte)(0xFC | (value >> 30)),
                    (byte)(0x80 | ((value >> 24) & 0x3F)),
                    (byte)(0x80 | ((value >> 18) & 0x3F)),
                    (byte)(0x80 | ((value >> 12) & 0x3F)),
                    (byte)(0x80 | ((value >> 6)  & 0x3F)),
                    (byte)(0x80 | ( value         & 0x3F))];
        // 7-byte: up to 2^36−1 (variable block size streams)
        return [0xFE,
                (byte)(0x80 | ((value >> 30) & 0x3F)),
                (byte)(0x80 | ((value >> 24) & 0x3F)),
                (byte)(0x80 | ((value >> 18) & 0x3F)),
                (byte)(0x80 | ((value >> 12) & 0x3F)),
                (byte)(0x80 | ((value >> 6)  & 0x3F)),
                (byte)(0x80 | ( value         & 0x3F))];
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }
}
