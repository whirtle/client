// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacChannelDecoderTests
{
    // -----------------------------------------------------------------------
    // Independent
    // -----------------------------------------------------------------------

    [Fact]
    public void Independent_Mono_SingleChannelDecoded()
    {
        // VERBATIM subframe, 4 samples at 8 bps.
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 7, bitsPerSample: 8);

        var header = MakeHeader(blockSize: 3, bitsPerSample: 8, channels: 1,
                                assignment: ChannelAssignment.Independent);
        var result = Decode(bs, header);

        Assert.Single(result);
        Assert.Equal(new[] { 7, 7, 7 }, result[0]);
    }

    [Fact]
    public void Independent_Stereo_TwoChannelsDecodedSeparately()
    {
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 10, bitsPerSample: 16);
        WriteConstantSubframe(bs, value: -3, bitsPerSample: 16);

        var header = MakeHeader(blockSize: 4, bitsPerSample: 16, channels: 2,
                                assignment: ChannelAssignment.Independent);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 10, 10, 10, 10 }, result[0]);
        Assert.Equal(new[] { -3, -3, -3, -3 }, result[1]);
    }

    [Fact]
    public void Independent_EightChannels_AllDecoded()
    {
        var bs = new BitStream();
        for (int c = 0; c < 8; c++)
            WriteConstantSubframe(bs, value: c * 10, bitsPerSample: 16);

        var header = MakeHeader(blockSize: 2, bitsPerSample: 16, channels: 8,
                                assignment: ChannelAssignment.Independent);
        var result = Decode(bs, header);

        Assert.Equal(8, result.Length);
        for (int c = 0; c < 8; c++)
            Assert.Equal(new[] { c * 10, c * 10 }, result[c]);
    }

    // -----------------------------------------------------------------------
    // LeftSide
    // -----------------------------------------------------------------------

    [Fact]
    public void LeftSide_ReconstrucstRight_FromLeftMinusSide()
    {
        // left=100, side=10 (decoded at bps+1=9 bits) → right=90
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 100, bitsPerSample: 8);      // left at 8 bps
        WriteConstantSubframe(bs, value: 10,  bitsPerSample: 9);      // side at 9 bps

        var header = MakeHeader(blockSize: 4, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.LeftSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 100, 100, 100, 100 }, result[0]); // left unchanged
        Assert.Equal(new[] { 90, 90, 90, 90 },     result[1]); // right = left - side
    }

    [Fact]
    public void LeftSide_NegativeSide_RightGreaterThanLeft()
    {
        // left=5, side=-3 → right = 5 − (−3) = 8
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 5,  bitsPerSample: 8);
        WriteConstantSubframe(bs, value: -3, bitsPerSample: 9);

        var header = MakeHeader(blockSize: 3, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.LeftSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 5, 5, 5 }, result[0]);
        Assert.Equal(new[] { 8, 8, 8 }, result[1]);
    }

    // -----------------------------------------------------------------------
    // RightSide
    // -----------------------------------------------------------------------

    [Fact]
    public void RightSide_ReconstructsLeft_FromRightPlusSide()
    {
        // side=5, right=50 → left = 50 + 5 = 55
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 5,  bitsPerSample: 9);  // side at bps+1
        WriteConstantSubframe(bs, value: 50, bitsPerSample: 8);  // right at nominal bps

        var header = MakeHeader(blockSize: 4, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.RightSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 55, 55, 55, 55 }, result[0]); // left = right + side
        Assert.Equal(new[] { 50, 50, 50, 50 }, result[1]); // right unchanged
    }

    [Fact]
    public void RightSide_NegativeSide_LeftLessThanRight()
    {
        // side=-10, right=30 → left = 30 + (−10) = 20
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: -10, bitsPerSample: 9);
        WriteConstantSubframe(bs, value: 30,  bitsPerSample: 8);

        var header = MakeHeader(blockSize: 2, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.RightSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 20, 20 }, result[0]);
        Assert.Equal(new[] { 30, 30 }, result[1]);
    }

    // -----------------------------------------------------------------------
    // MidSide — even sum (no LSB recovery needed)
    // -----------------------------------------------------------------------

    [Fact]
    public void MidSide_EvenSum_ReconstructsCorrectly()
    {
        // left=8, right=4 → mid=(8+4)>>1=6, side=8-4=4 (even)
        // Restore: mid′ = 6<<1 | (4&1) = 12 | 0 = 12
        //   left  = (12+4)>>1 = 8
        //   right = (12-4)>>1 = 4
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 6, bitsPerSample: 8);      // mid at 8 bps
        WriteConstantSubframe(bs, value: 4, bitsPerSample: 9);      // side at 9 bps

        var header = MakeHeader(blockSize: 3, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.MidSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 8, 8, 8 }, result[0]); // left
        Assert.Equal(new[] { 4, 4, 4 }, result[1]); // right
    }

    // -----------------------------------------------------------------------
    // MidSide — odd sum (LSB recovery is critical)
    // -----------------------------------------------------------------------

    [Fact]
    public void MidSide_OddSum_LsbRestoredCorrectly()
    {
        // left=7, right=4 → mid=(7+4)>>1=5, side=7-4=3 (odd)
        // Restore: mid′ = 5<<1 | (3&1) = 10 | 1 = 11
        //   left  = (11+3)>>1 = 7
        //   right = (11-3)>>1 = 4
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 5, bitsPerSample: 8);      // mid
        WriteConstantSubframe(bs, value: 3, bitsPerSample: 9);      // side

        var header = MakeHeader(blockSize: 3, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.MidSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 7, 7, 7 }, result[0]); // left
        Assert.Equal(new[] { 4, 4, 4 }, result[1]); // right
    }

    [Fact]
    public void MidSide_NegativeSide_ReconstructsCorrectly()
    {
        // left=2, right=6 → mid=(2+6)>>1=4, side=2-6=-4 (even)
        // Restore: mid′ = 4<<1 | (-4&1) = 8 | 0 = 8
        //   left  = (8 + -4)>>1 = 2
        //   right = (8 - -4)>>1 = 6
        var bs = new BitStream();
        WriteConstantSubframe(bs, value: 4,  bitsPerSample: 8);   // mid
        WriteConstantSubframe(bs, value: -4, bitsPerSample: 9);   // side

        var header = MakeHeader(blockSize: 2, bitsPerSample: 8, channels: 2,
                                assignment: ChannelAssignment.MidSide);
        var result = Decode(bs, header);

        Assert.Equal(new[] { 2, 2 }, result[0]); // left
        Assert.Equal(new[] { 6, 6 }, result[1]); // right
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int[][] Decode(BitStream bs, FlacFrameHeader header)
    {
        var bytes  = bs.ToBytes();
        var reader = new FlacBitReader(bytes);
        return FlacChannelDecoder.Decode(ref reader, header);
    }

    private static FlacFrameHeader MakeHeader(
        int blockSize, int bitsPerSample, int channels, ChannelAssignment assignment)
        => new(blockSize, sampleRate: 44100, channels, assignment, bitsPerSample,
               frameOrSampleNumber: 0, isVariableBlockSize: false);

    /// <summary>
    /// Writes a CONSTANT subframe whose single value fits in <paramref name="bitsPerSample"/> bits.
    /// </summary>
    private static void WriteConstantSubframe(BitStream bs, int value, int bitsPerSample)
    {
        bs.Write(0, 1);          // zero padding
        bs.Write(0x00, 6);       // CONSTANT type
        bs.Write(0, 1);          // no wasted bits
        bs.Write(value, bitsPerSample);
    }

    // -----------------------------------------------------------------------
    // BitStream — packs bits MSB-first, mirror of FlacBitReader.
    // -----------------------------------------------------------------------

    private sealed class BitStream
    {
        private readonly List<int> _bits = [];

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                _bits.Add((value >> i) & 1);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                bytes[i >> 3] |= (byte)(_bits[i] << (7 - (i & 7)));
            return bytes;
        }
    }
}
