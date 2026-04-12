// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacMetadataReaderTests
{
    // -----------------------------------------------------------------------
    // Happy-path parsing
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_BasicStreamInfo_ParsesAllFields()
    {
        var md5 = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var stream = BuildStream(
            minBlockSize:  4096,
            maxBlockSize:  4096,
            minFrameSize:  0,
            maxFrameSize:  0,
            sampleRate:    44_100,
            channels:      2,
            bitsPerSample: 16,
            totalSamples:  0,
            md5:           md5);

        var info = FlacMetadataReader.Read(stream, out _);

        Assert.Equal(4096,   info.MinBlockSize);
        Assert.Equal(4096,   info.MaxBlockSize);
        Assert.Equal(0,      info.MinFrameSize);
        Assert.Equal(0,      info.MaxFrameSize);
        Assert.Equal(44_100, info.SampleRate);
        Assert.Equal(2,      info.Channels);
        Assert.Equal(16,     info.BitsPerSample);
        Assert.Equal(0,      info.TotalSamples);
        Assert.Equal(md5,    info.Md5Signature);
    }

    [Fact]
    public void Read_NonZeroTotalSamples_ParsedCorrectly()
    {
        // TotalSamples = 44100 fits in the low 32 bits; high 4 bits are zero.
        var stream = BuildStream(totalSamples: 44_100);
        var info   = FlacMetadataReader.Read(stream, out _);
        Assert.Equal(44_100L, info.TotalSamples);
    }

    [Fact]
    public void Read_TotalSamplesWithHighBitsSet_ParsedCorrectly()
    {
        // 0xF_0000_0001 exercises the high 4-bit half of the 36-bit field.
        long expected = 0xF_0000_0001L;
        var stream = BuildStream(totalSamples: expected);
        var info   = FlacMetadataReader.Read(stream, out _);
        Assert.Equal(expected, info.TotalSamples);
    }

    [Fact]
    public void Read_MonoStream_ChannelsIsOne()
    {
        var stream = BuildStream(channels: 1);
        Assert.Equal(1, FlacMetadataReader.Read(stream, out _).Channels);
    }

    [Fact]
    public void Read_EightChannels_ParsedCorrectly()
    {
        var stream = BuildStream(channels: 8);
        Assert.Equal(8, FlacMetadataReader.Read(stream, out _).Channels);
    }

    [Fact]
    public void Read_24BitDepth_ParsedCorrectly()
    {
        var stream = BuildStream(bitsPerSample: 24);
        Assert.Equal(24, FlacMetadataReader.Read(stream, out _).BitsPerSample);
    }

    [Fact]
    public void Read_48kHz_ParsedCorrectly()
    {
        var stream = BuildStream(sampleRate: 48_000);
        Assert.Equal(48_000, FlacMetadataReader.Read(stream, out _).SampleRate);
    }

    // -----------------------------------------------------------------------
    // bytesConsumed
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_SingleBlock_BytesConsumedCoversHeaderAndBlock()
    {
        // 4 (fLaC) + 4 (block header) + 34 (STREAMINFO) = 42
        var stream = BuildStream();
        FlacMetadataReader.Read(stream, out int consumed);
        Assert.Equal(42, consumed);
    }

    [Fact]
    public void Read_WithTrailingPaddingBlock_BytesConsumedIncludesBothBlocks()
    {
        // STREAMINFO (not last) + PADDING (last, 8 bytes of zeros)
        // 4 + 4 + 34 + 4 + 8 = 54
        var stream = Concat(
            FlacMagic,
            BlockHeader(isLast: false, blockType: 0, length: 34),
            StreamInfoBytes(),
            BlockHeader(isLast: true,  blockType: 1, length: 8),
            new byte[8]);

        FlacMetadataReader.Read(stream, out int consumed);
        Assert.Equal(54, consumed);
    }

    [Fact]
    public void Read_BytesConsumed_PointsToFirstFrameByte()
    {
        // Append a sentinel byte after the metadata; it should be at [consumed].
        var stream = Concat(BuildStream(), [0xAB]);
        FlacMetadataReader.Read(stream, out int consumed);
        Assert.Equal(0xAB, stream[consumed]);
    }

    // -----------------------------------------------------------------------
    // Multiple metadata blocks
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_StreamInfoFollowedByPadding_ReturnsCorrectInfo()
    {
        var stream = Concat(
            FlacMagic,
            BlockHeader(isLast: false, blockType: 0, length: 34),
            StreamInfoBytes(sampleRate: 96_000, channels: 2, bitsPerSample: 24),
            BlockHeader(isLast: true,  blockType: 1, length: 4),
            new byte[4]);

        var info = FlacMetadataReader.Read(stream, out _);
        Assert.Equal(96_000, info.SampleRate);
        Assert.Equal(24, info.BitsPerSample);
    }

    [Fact]
    public void Read_UnknownBlockTypeBeforeStreamInfo_IsSkipped()
    {
        // Block type 5 (CUESHEET) before STREAMINFO — must be silently ignored.
        var stream = Concat(
            FlacMagic,
            BlockHeader(isLast: false, blockType: 5, length: 4),
            new byte[4],
            BlockHeader(isLast: true,  blockType: 0, length: 34),
            StreamInfoBytes());

        // Should not throw; STREAMINFO is still found.
        var info = FlacMetadataReader.Read(stream, out _);
        Assert.Equal(44_100, info.SampleRate);
    }

    [Fact]
    public void Read_SeekTableBlockIsSkipped()
    {
        // Block type 3 = SEEKTABLE; 18 bytes per seek point.
        // Placeholder point: sample number = 0xFFFFFFFFFFFFFFFF.
        byte[] seekTableBody = new byte[18]; // one placeholder (all 0xFF for sample number)
        for (int i = 0; i < 8; i++) seekTableBody[i] = 0xFF;

        var stream = Concat(
            FlacMagic,
            BlockHeader(isLast: false, blockType: 3, length: 18),
            seekTableBody,
            BlockHeader(isLast: true,  blockType: 0, length: 34),
            StreamInfoBytes());

        var info = FlacMetadataReader.Read(stream, out _);
        Assert.Equal(44_100, info.SampleRate);
    }

    // -----------------------------------------------------------------------
    // Error cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Read_WrongMagic_Throws()
    {
        // Last byte of magic is 0x44 instead of 0x43.
        byte[] bad = Concat([0x66, 0x4C, 0x61, 0x44], BlockHeader(true, 0, 34), StreamInfoBytes());
        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.Read(bad, out _));
    }

    [Fact]
    public void Read_EmptyBuffer_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.Read([], out _));
    }

    [Fact]
    public void Read_NoStreamInfoBlock_Throws()
    {
        // Only a PADDING block — no STREAMINFO anywhere.
        var stream = Concat(FlacMagic, BlockHeader(isLast: true, blockType: 1, length: 4), new byte[4]);
        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.Read(stream, out _));
    }

    [Fact]
    public void Read_TruncatedHeader_Throws()
    {
        // Only the fLaC magic, then 2 bytes of a header (need 4).
        var stream = Concat(FlacMagic, [0x80, 0x00]);
        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.Read(stream, out _));
    }

    [Fact]
    public void Read_BlockLengthExceedsBuffer_Throws()
    {
        // Header claims length = 100, but STREAMINFO payload is only 34 bytes.
        var stream = Concat(FlacMagic, BlockHeader(isLast: true, blockType: 0, length: 100), StreamInfoBytes());
        Assert.Throws<InvalidDataException>(() => FlacMetadataReader.Read(stream, out _));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly byte[] FlacMagic = [0x66, 0x4C, 0x61, 0x43];

    /// <summary>Builds the 4-byte metadata block header.</summary>
    private static byte[] BlockHeader(bool isLast, int blockType, int length) =>
    [
        (byte)(((isLast ? 1 : 0) << 7) | (blockType & 0x7F)),
        (byte)(length >> 16),
        (byte)(length >>  8),
        (byte)(length),
    ];

    /// <summary>
    /// Builds a 34-byte STREAMINFO payload with the given parameters,
    /// packing the bit-level fields MSB-first per the FLAC spec.
    /// </summary>
    private static byte[] StreamInfoBytes(
        int   minBlockSize  = 4096,
        int   maxBlockSize  = 4096,
        int   minFrameSize  = 0,
        int   maxFrameSize  = 0,
        int   sampleRate    = 44_100,
        int   channels      = 2,
        int   bitsPerSample = 16,
        long  totalSamples  = 0,
        byte[]? md5         = null)
    {
        md5 ??= new byte[16];

        // The first five fields are byte-aligned; the rest are bit-packed.
        var bw = new BitWriter();
        bw.Write(minBlockSize,      16);
        bw.Write(maxBlockSize,      16);
        bw.Write(minFrameSize,      24);
        bw.Write(maxFrameSize,      24);
        bw.Write(sampleRate,        20);
        bw.Write(channels - 1,       3);
        bw.Write(bitsPerSample - 1,  5);
        bw.Write((int)(totalSamples >> 32),  4);   // high 4 bits of TotalSamples
        bw.Write((int)(totalSamples & 0xFFFFFFFFL), 32); // low 32 bits

        var bytes = bw.ToArray();
        // Append MD5 (already byte-aligned at this point: 16+16+24+24+20+3+5+36 = 144 bits = 18 bytes)
        return [..bytes, ..md5];
    }

    /// <summary>Wraps a STREAMINFO payload in the fLaC magic + single last block header.</summary>
    private static byte[] BuildStream(
        int   minBlockSize  = 4096,
        int   maxBlockSize  = 4096,
        int   minFrameSize  = 0,
        int   maxFrameSize  = 0,
        int   sampleRate    = 44_100,
        int   channels      = 2,
        int   bitsPerSample = 16,
        long  totalSamples  = 0,
        byte[]? md5         = null) =>
        Concat(
            FlacMagic,
            BlockHeader(isLast: true, blockType: 0, length: 34),
            StreamInfoBytes(minBlockSize, maxBlockSize, minFrameSize, maxFrameSize,
                            sampleRate, channels, bitsPerSample, totalSamples, md5));

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }

    // -----------------------------------------------------------------------
    // BitWriter — packs integer values MSB-first, used only for test data.
    // -----------------------------------------------------------------------

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _currentByte;
        private int _bitsInCurrentByte;

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/>, MSB first.</summary>
        public void Write(long value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                int bit = (int)((value >> i) & 1);
                _currentByte = (_currentByte << 1) | bit;
                _bitsInCurrentByte++;
                if (_bitsInCurrentByte == 8)
                {
                    _bytes.Add((byte)_currentByte);
                    _currentByte        = 0;
                    _bitsInCurrentByte  = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_bitsInCurrentByte > 0)
                _bytes.Add((byte)(_currentByte << (8 - _bitsInCurrentByte)));
            return [.._bytes];
        }
    }
}
