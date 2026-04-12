// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec;
using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

/// <summary>
/// End-to-end tests for <see cref="FlacAudioDecoder"/>.
///
/// <see cref="FlacFileBuilder"/> constructs minimal but fully-valid FLAC byte
/// sequences — complete with STREAMINFO, per-frame CRC-8, and CRC-16 — so
/// these tests exercise the entire decode pipeline on real bitstreams.
/// </summary>
public class FlacAudioDecoderTests
{
    // -----------------------------------------------------------------------
    // Single frame
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_SingleFrame_VerbatimSamples_Roundtrip()
    {
        int[] expected = [100, -100, 200, -200, 0, 32767, -32768, 1];
        var bytes = FlacFileBuilder.BuildMono16(expected);

        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);

        Assert.Equal(1, frame.Channels);
        Assert.Equal(44_100, frame.SampleRate);
        Assert.Equal(expected.Length, frame.SamplesPerChannel);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal((short)expected[i], frame.Samples[i]);
    }

    [Fact]
    public void Decode_AllZeros_ProducesSilence()
    {
        int[] samples = new int[256];
        var bytes = FlacFileBuilder.BuildMono16(samples);

        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);

        Assert.Equal(256, frame.SamplesPerChannel);
        Assert.All(frame.Samples, s => Assert.Equal(0, s));
    }

    // -----------------------------------------------------------------------
    // Multiple frames
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_TwoFrames_SamplesContiguous()
    {
        int[] block0 = [10, -10, 20, -20];
        int[] block1 = [30, -30, 40, -40];
        var bytes = FlacFileBuilder.BuildMono16MultiFrame([block0, block1]);

        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);

        int[] expected = [.. block0, .. block1];
        Assert.Equal(expected.Length, frame.SamplesPerChannel);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal((short)expected[i], frame.Samples[i]);
    }

    [Fact]
    public void Decode_ThreeFrames_SamplesContiguous()
    {
        int[] b0 = [1, -1];
        int[] b1 = [2, -2];
        int[] b2 = [3, -3];
        var bytes = FlacFileBuilder.BuildMono16MultiFrame([b0, b1, b2]);

        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);

        Assert.Equal(6, frame.SamplesPerChannel);
        Assert.Equal((short)1,  frame.Samples[0]);
        Assert.Equal((short)-1, frame.Samples[1]);
        Assert.Equal((short)2,  frame.Samples[2]);
        Assert.Equal((short)-2, frame.Samples[3]);
        Assert.Equal((short)3,  frame.Samples[4]);
        Assert.Equal((short)-3, frame.Samples[5]);
    }

    // -----------------------------------------------------------------------
    // CRC validation
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_CorruptFrameCrc16_ThrowsInvalidData()
    {
        var bytes = FlacFileBuilder.BuildMono16([1, 2, 3, 4]).ToArray();

        // Flip a bit in the last two bytes (the CRC-16 footer).
        bytes[^1] ^= 0xFF;

        using var decoder = new FlacAudioDecoder();
        Assert.Throws<InvalidDataException>(() => decoder.Decode(bytes));
    }

    [Fact]
    public void Decode_InvalidSyncWord_ThrowsInvalidData()
    {
        // Four zero bytes fail the 0x3FFE sync check.
        using var decoder = new FlacAudioDecoder();
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[32]));
    }

    // -----------------------------------------------------------------------
    // AudioFrame metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void Decode_SampleRateReflectsStreamInfo()
    {
        var bytes = FlacFileBuilder.BuildMono16([0, 0], sampleRate: 48_000);
        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);
        Assert.Equal(48_000, frame.SampleRate);
    }

    [Fact]
    public void Decode_EmptyFrameList_ReturnsEmptyAudioFrame()
    {
        // A valid fLaC file with STREAMINFO but no audio frames.
        var bytes = FlacFileBuilder.BuildEmptyFlacFile(sampleRate: 44_100);
        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(bytes);
        Assert.Equal(0, frame.Samples.Length);
    }

    // -----------------------------------------------------------------------
    // IETF FLAC conformance test files (network, skipped by default)
    //
    // To run manually:  dotnet test --filter FlacAudioDecoderTests
    //   then un-skip this test or pass --filter on the method name.
    //
    // The test files live at:
    //   https://github.com/ietf-wg-cellar/flac-test-files
    // -----------------------------------------------------------------------

    [Theory(Skip = "Downloads from the internet; run manually to validate conformance")]
    [InlineData("subset/01 - blocksize 4096.flac")]
    [InlineData("subset/02 - blocksize 4608.flac")]
    [InlineData("subset/03 - blocksize 16.flac")]
    [InlineData("subset/04 - blocksize 192.flac")]
    [InlineData("subset/05 - blocksize 254.flac")]
    public async Task IetfConformance_SubsetFile_DecodesCorrectly(string relativePath)
    {
        // Download from the IETF CELLAR working group FLAC test repository.
        var encoded = Uri.EscapeDataString(relativePath).Replace("%2F", "/");
        var url = $"https://raw.githubusercontent.com/ietf-wg-cellar/flac-test-files/main/{encoded}";

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var fileBytes = await http.GetByteArrayAsync(url);

        // Parse STREAMINFO independently for validation.
        var streamInfo = FlacMetadataReader.Read(fileBytes, out _);

        using var decoder = new FlacAudioDecoder();
        var frame = decoder.Decode(fileBytes);

        // Structural checks against STREAMINFO.
        Assert.Equal(streamInfo.SampleRate, frame.SampleRate);
        Assert.Equal(streamInfo.Channels,   frame.Channels);
        Assert.Equal(streamInfo.TotalSamples, frame.SamplesPerChannel);
    }

    // -----------------------------------------------------------------------
    // FlacFileBuilder — constructs minimal valid FLAC byte sequences.
    //
    // Uses FlacCrc (accessible via InternalsVisibleTo) so CRC-8 and CRC-16
    // are computed rather than hard-coded, keeping the builder self-validating.
    // -----------------------------------------------------------------------

    private static class FlacFileBuilder
    {
        // ── Public API ───────────────────────────────────────────────────────

        public static byte[] BuildMono16(int[] samples, int sampleRate = 44_100)
            => BuildFlacFile([samples], sampleRate, bitsPerSample: 16);

        public static byte[] BuildMono16MultiFrame(int[][] frames, int sampleRate = 44_100)
            => BuildFlacFile(frames, sampleRate, bitsPerSample: 16);

        public static byte[] BuildEmptyFlacFile(int sampleRate = 44_100)
        {
            var si = BuildStreamInfoBlock(
                minBlock: 0, maxBlock: 0, sampleRate, channels: 1,
                bitsPerSample: 16, totalSamples: 0);
            return [0x66, 0x4C, 0x61, 0x43, .. si];
        }

        // ── Implementation ───────────────────────────────────────────────────

        private static byte[] BuildFlacFile(int[][] frameSamples, int sampleRate, int bitsPerSample)
        {
            long totalSamples = frameSamples.Sum(f => (long)f.Length);
            int  minBlock     = frameSamples.Min(f => f.Length);
            int  maxBlock     = frameSamples.Max(f => f.Length);

            var si     = BuildStreamInfoBlock(minBlock, maxBlock, sampleRate, channels: 1,
                                               bitsPerSample, totalSamples);
            var result = new List<byte> { 0x66, 0x4C, 0x61, 0x43 };  // fLaC
            result.AddRange(si);
            for (int i = 0; i < frameSamples.Length; i++)
                result.AddRange(BuildVerbatimFrame(i, frameSamples[i], sampleRate, bitsPerSample));
            return result.ToArray();
        }

        // STREAMINFO metadata block (IsLast=1, type=0, 34-byte payload).
        private static byte[] BuildStreamInfoBlock(
            int minBlock, int maxBlock, int sampleRate,
            int channels, int bitsPerSample, long totalSamples)
        {
            var payload = new BitStream();
            payload.Write(minBlock,         16);
            payload.Write(maxBlock,         16);
            payload.Write(0,                24);  // MinFrameSize
            payload.Write(0,                24);  // MaxFrameSize
            payload.Write(sampleRate,       20);
            payload.Write(channels - 1,      3);
            payload.Write(bitsPerSample - 1, 5);
            payload.Write((int)(totalSamples >> 32), 4);   // TotalSamples high 4 bits
            payload.Write((int) totalSamples,       32);   // TotalSamples low 32 bits
            for (int i = 0; i < 16; i++) payload.Write(0, 8);  // MD5 (all zeros)
            var data = payload.ToBytes();                        // must be 34 bytes

            // Block header: IsLast=1 (0x80), type=0 (STREAMINFO), 24-bit length
            return [0x80, 0x00, 0x00, (byte)data.Length, .. data];
        }

        // One VERBATIM audio frame, using a 16-bit block-size tail (code 7)
        // so any block size 1–65536 is supported.
        private static byte[] BuildVerbatimFrame(
            int frameNumber, int[] samples, int sampleRate, int bitsPerSample)
        {
            // ── Frame header (before CRC-8) ──
            var hdr = new List<byte>
            {
                0xFF, 0xF8,  // sync(14) + reserved(0) + fixed-block-size(0)
                (byte)((7 << 4) | SampleRateCode(sampleRate)),  // blockSizeCode=7 | srCode
                (byte)((0 << 4) | (SampleSizeCode(bitsPerSample) << 1)),  // 1ch | bpsCode
                (byte)frameNumber,  // UTF-8 coded frame number (works for 0–127)
                (byte)((samples.Length - 1) >> 8),
                (byte)((samples.Length - 1) & 0xFF),  // 16-bit block-size tail
            };
            byte crc8 = FlacCrc.ComputeCrc8(hdr.ToArray());
            hdr.Add(crc8);

            // ── VERBATIM subframe ──
            var sub = new List<byte> { 0x02 };  // zero + VERBATIM(000001) + no-wasted-bits
            foreach (int s in samples)
            {
                sub.Add((byte)(s >> (bitsPerSample - 8)));                // high byte
                if (bitsPerSample > 8)
                    sub.Add((byte)(s >> (bitsPerSample - 16)));           // low byte
            }
            // For 16-bit samples the subframe total is always byte-aligned
            // (8 header bits + n×16 sample bits).

            // ── CRC-16 over header + subframe ──
            var content = hdr.Concat(sub).ToArray();
            ushort crc16 = FlacCrc.ComputeCrc16(content);
            return [.. content, (byte)(crc16 >> 8), (byte)(crc16 & 0xFF)];
        }

        // Maps well-known sample rates to their FLAC 4-bit codes.
        private static int SampleRateCode(int sampleRate) => sampleRate switch
        {
            88_200 => 1,
            176_400 => 2,
            192_000 => 3,
              8_000 => 4,
             16_000 => 5,
             22_050 => 6,
             24_000 => 7,
             32_000 => 8,
             44_100 => 9,
             48_000 => 10,
             96_000 => 11,
            _ => throw new ArgumentException($"No fixed code for sample rate {sampleRate}; "
                                              + "add a 12–14 tail-coded entry if needed."),
        };

        // Maps bit depths to their FLAC 3-bit sample-size codes.
        private static int SampleSizeCode(int bitsPerSample) => bitsPerSample switch
        {
             8 => 1,
            12 => 2,
            16 => 4,
            20 => 5,
            24 => 6,
            32 => 7,
            _  => throw new ArgumentException($"No fixed code for bit depth {bitsPerSample}."),
        };

        // ── Minimal MSB-first bit packer (mirrors FlacBitReader) ─────────────

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
}
