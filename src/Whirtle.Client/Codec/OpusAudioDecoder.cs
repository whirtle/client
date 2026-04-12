// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Concentus;

namespace Whirtle.Client.Codec;

/// <summary>
/// Decodes Opus packets to 16-bit PCM using the Concentus pure-C# Opus implementation.
/// </summary>
public sealed class OpusAudioDecoder : IAudioDecoder
{
    // Maximum frame size for Opus: 120 ms at 48 kHz = 5760 samples per channel.
    private const int MaxFrameSize = 5760;

    private readonly IOpusDecoder _decoder;

    public AudioFormat Format     => AudioFormat.Opus;
    public int         SampleRate { get; }
    public int         Channels   { get; }

    public OpusAudioDecoder(int sampleRate = 48_000, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels,   1);
        SampleRate = sampleRate;
        Channels   = channels;
        _decoder   = OpusCodecFactory.CreateDecoder(sampleRate, channels);
    }

    public AudioFrame Decode(ReadOnlyMemory<byte> data)
    {
        var pcm = new short[MaxFrameSize * Channels];
        int samplesPerChannel = _decoder.Decode(
            data.Span,
            pcm.AsSpan(),
            MaxFrameSize,
            false);

        return new AudioFrame(pcm[..(samplesPerChannel * Channels)], SampleRate, Channels);
    }

    public void Dispose()
    {
        // Dispose the underlying decoder if the Concentus version being used
        // holds native resources (e.g. via OpusCodecFactory on supported platforms).
        (_decoder as IDisposable)?.Dispose();
    }
}
