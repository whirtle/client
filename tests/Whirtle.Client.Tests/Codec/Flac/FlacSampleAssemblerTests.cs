// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacSampleAssemblerTests
{
    // -----------------------------------------------------------------------
    // 16-bit — identity (no scaling)
    // -----------------------------------------------------------------------

    [Fact]
    public void Bps16_Mono_NoScaling()
    {
        int[][] ch = [[0, 32767, -32768, -1]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 16);
        Assert.Equal(new short[] { 0, 32767, -32768, -1 }, result);
    }

    [Fact]
    public void Bps16_Stereo_InterleavedLR()
    {
        int[][] ch = [[100, 200], [-100, -200]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 16);
        // Expect L0, R0, L1, R1
        Assert.Equal(new short[] { 100, -100, 200, -200 }, result);
    }

    // -----------------------------------------------------------------------
    // Scale up (bitsPerSample < 16)
    // -----------------------------------------------------------------------

    [Fact]
    public void Bps8_ScalesUpByShifting8()
    {
        // 8-bit range: [-128, 127] → after <<8: [-32768, 32512]
        int[][] ch = [[64, -64, 127, -128]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 8);
        Assert.Equal(new short[] { 64 << 8, -64 << 8, 127 << 8, -128 << 8 }, result);
    }

    [Fact]
    public void Bps12_ScalesUpByShifting4()
    {
        // 12-bit: max = 2047, min = -2048 → after <<4: 32752, -32768
        int[][] ch = [[2047, -2048, 0]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 12);
        Assert.Equal(new short[] { 2047 << 4, -2048 << 4, 0 }, result);
    }

    // -----------------------------------------------------------------------
    // Scale down (bitsPerSample > 16)
    // -----------------------------------------------------------------------

    [Fact]
    public void Bps20_ScalesDownByShifting4()
    {
        // 20-bit: 0x7FFFF = 524287 → >>4 = 0x7FFF = 32767
        //         0x80000 = -524288 → >>4 = -32768
        int[][] ch = [[524287, -524288, 0]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 20);
        Assert.Equal(new short[] { 32767, -32768, 0 }, result);
    }

    [Fact]
    public void Bps24_ScalesDownByShifting8()
    {
        // 24-bit max/min: ±2^23
        int[][] ch = [[8388607, -8388608]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 24);
        Assert.Equal(new short[] { 32767, -32768 }, result);
    }

    [Fact]
    public void Bps32_ScalesDownByShifting16()
    {
        int[][] ch = [[int.MaxValue, int.MinValue, 0]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 32);
        Assert.Equal(new short[] { short.MaxValue, short.MinValue, 0 }, result);
    }

    // -----------------------------------------------------------------------
    // Interleaving — multi-channel ordering
    // -----------------------------------------------------------------------

    [Fact]
    public void FourChannels_InterleavedInChannelOrder()
    {
        // 4 channels, 2 samples each; confirm interleave order
        int[][] ch =
        [
            [10, 11],   // ch0
            [20, 21],   // ch1
            [30, 31],   // ch2
            [40, 41],   // ch3
        ];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 16);
        // sample 0: 10, 20, 30, 40 — sample 1: 11, 21, 31, 41
        Assert.Equal(new short[] { 10, 20, 30, 40, 11, 21, 31, 41 }, result);
    }

    // -----------------------------------------------------------------------
    // Zero / silence
    // -----------------------------------------------------------------------

    [Fact]
    public void AllZeros_ProduceSilence()
    {
        int[][] ch = [[0, 0, 0], [0, 0, 0]];
        var result = FlacSampleAssembler.Assemble(ch, bitsPerSample: 16);
        Assert.All(result, s => Assert.Equal(0, s));
    }
}
