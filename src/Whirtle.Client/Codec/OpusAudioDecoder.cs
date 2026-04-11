// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Concentus.Structs;

namespace Whirtle.Client.Codec;

/// <summary>
/// Decodes Opus packets to 16-bit PCM using the Concentus pure-C# Opus implementation.
/// </summary>
public sealed class OpusAudioDecoder : IAudioDecoder
{
    // Maximum frame size for Opus: 120 ms at 48 kHz = 5760 samples per channel.
    private const int MaxFrameSize = 5760;

    private readonly OpusDecoder _decoder;

    public AudioFormat Format     => AudioFormat.Opus;
    public int         SampleRate { get; }
    public int         Channels   { get; }

    public OpusAudioDecoder(int sampleRate = 48_000, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels,   1);
        SampleRate = sampleRate;
        Channels   = channels;
        _decoder   = new OpusDecoder(sampleRate, channels);
    }

    public AudioFrame Decode(ReadOnlyMemory<byte> data)
    {
        var encoded = data.ToArray();
        var pcm     = new short[MaxFrameSize * Channels];

        int samplesPerChannel = _decoder.Decode(
            encoded, 0, encoded.Length,
            pcm,     0, MaxFrameSize,
            false);

        return new AudioFrame(pcm[..(samplesPerChannel * Channels)], SampleRate, Channels);
    }

    public void Dispose() { }
}
