// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace Whirtle.Client.Codec;

/// <summary>
/// Passthrough decoder for raw 16-bit little-endian interleaved PCM.
/// No decompression is performed; bytes are reinterpreted directly as <c>short[]</c>.
/// </summary>
public sealed class PcmAudioDecoder : IAudioDecoder
{
    public AudioFormat Format     => AudioFormat.Pcm;
    public int         SampleRate { get; }
    public int         Channels   { get; }

    public PcmAudioDecoder(int sampleRate = 48_000, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels,   1);
        SampleRate = sampleRate;
        Channels   = channels;
    }

    public AudioFrame Decode(ReadOnlyMemory<byte> data)
    {
        if (data.Length % 2 != 0)
            throw new ArgumentException("PCM data length must be a multiple of 2 bytes.", nameof(data));

        var samples = new short[data.Length / 2];
        MemoryMarshal.Cast<byte, short>(data.Span).CopyTo(samples);
        return new AudioFrame(samples, SampleRate, Channels);
    }

    public void Dispose() { }
}
