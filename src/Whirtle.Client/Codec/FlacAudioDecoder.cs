// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Codec;

/// <summary>
/// FLAC decoder.  Accepts either a complete FLAC file (identified by the
/// four-byte "fLaC" marker) or a raw sequence of FLAC audio frames.
///
/// Full-file path:
///   1. <see cref="FlacMetadataReader"/> parses the file container and
///      returns a <see cref="FlacStreamInfo"/> with sample-rate, channel,
///      and bit-depth metadata.
///   2. Each audio frame is decoded in order:
///      a. <see cref="FlacFrameHeaderReader"/> parses the frame header and
///         validates CRC-8.
///      b. <see cref="FlacChannelDecoder"/> decodes every channel subframe
///         (CONSTANT / VERBATIM / FIXED / LPC) and un-decorrelates stereo
///         side channels.
///      c. The frame bit-stream is realigned to a byte boundary, and the
///         two-byte frame-footer CRC-16 is verified.
///      d. <see cref="FlacSampleAssembler"/> scales each channel from the
///         stream's native bit depth to 16 bits and interleaves channels.
///   3. All frame PCM blocks are concatenated and returned as one
///      <see cref="AudioFrame"/>.
///
/// Raw-frame path:
///   No fLaC container is present.  The constructor-supplied
///   <paramref name="sampleRate"/> and <paramref name="channels"/> are used
///   as STREAMINFO fallback values (required only when a frame-header field
///   uses code 0, which means "use STREAMINFO value").
/// </summary>
public sealed class FlacAudioDecoder : IAudioDecoder
{
    public AudioFormat Format     => AudioFormat.Flac;
    public int         SampleRate { get; }
    public int         Channels   { get; }

    public FlacAudioDecoder(int sampleRate = 44_100, int channels = 2)
    {
        SampleRate = sampleRate;
        Channels   = channels;
    }

    /// <inheritdoc/>
    public AudioFrame Decode(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;

        // ── Container detection ──────────────────────────────────────────────
        FlacStreamInfo streamInfo;
        int offset;

        if (IsFlacFile(span))
        {
            streamInfo = FlacMetadataReader.Read(span, out offset);
        }
        else
        {
            // Raw frames: build a synthetic STREAMINFO from constructor params.
            // BitsPerSample = 0 means "use whatever the frame header says".
            streamInfo = new FlacStreamInfo(
                MinBlockSize: 0, MaxBlockSize: 0,
                MinFrameSize: 0, MaxFrameSize: 0,
                SampleRate: SampleRate, Channels: Channels,
                BitsPerSample: 0,
                TotalSamples: 0, Md5Signature: []);
            offset = 0;
        }

        // ── Frame loop ───────────────────────────────────────────────────────
        var frameBuffers = new List<short[]>();

        while (offset < span.Length)
        {
            var frameSpan = span[offset..];

            // Header (CRC-8 validated inside Read).
            var header = FlacFrameHeaderReader.Read(frameSpan, streamInfo, out int headerBytes);

            // Subframes — one per channel.
            var subframeReader = new FlacBitReader(frameSpan[headerBytes..]);
            var channelSamples = FlacChannelDecoder.Decode(ref subframeReader, header);

            // Realign to byte boundary (zero-padding after the last subframe).
            subframeReader.AlignToByte();
            int subframeBytes = subframeReader.BytePosition;
            int contentBytes  = headerBytes + subframeBytes;

            // Frame footer: CRC-16 over every byte from the sync word through
            // the zero-padded subframes.
            ushort expectedCrc = FlacCrc.ComputeCrc16(frameSpan[..contentBytes]);
            ushort actualCrc   = (ushort)((subframeReader.ReadByte() << 8)
                                          | subframeReader.ReadByte());

            if (actualCrc != expectedCrc)
                throw new InvalidDataException(
                    $"Frame CRC-16 mismatch: expected 0x{expectedCrc:X4}, " +
                    $"got 0x{actualCrc:X4}.");

            frameBuffers.Add(FlacSampleAssembler.Assemble(channelSamples, header.BitsPerSample));
            offset += contentBytes + 2;
        }

        // ── Concatenate all frames ───────────────────────────────────────────
        int totalLen = frameBuffers.Sum(b => b.Length);
        var output   = new short[totalLen];
        int pos      = 0;
        foreach (var buf in frameBuffers)
        {
            buf.CopyTo(output, pos);
            pos += buf.Length;
        }

        return new AudioFrame(output, streamInfo.SampleRate, streamInfo.Channels);
    }

    public void Dispose() { }

    private static bool IsFlacFile(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && data[0] == 0x66 && data[1] == 0x4C
        && data[2] == 0x61 && data[3] == 0x43;  // 'f','L','a','C'
}
