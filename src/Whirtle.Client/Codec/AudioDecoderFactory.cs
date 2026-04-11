// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Codec;

public static class AudioDecoderFactory
{
    /// <summary>
    /// Creates an <see cref="IAudioDecoder"/> for the given <paramref name="format"/>.
    /// </summary>
    /// <param name="sampleRate">
    /// Target sample rate in Hz. Ignored for <see cref="AudioFormat.Flac"/> (determined
    /// by the stream header).
    /// </param>
    /// <param name="channels">Number of output channels (1 or 2).</param>
    public static IAudioDecoder Create(
        AudioFormat format,
        int sampleRate = 48_000,
        int channels   = 2) => format switch
    {
        AudioFormat.Pcm  => new PcmAudioDecoder(sampleRate, channels),
        AudioFormat.Opus => new OpusAudioDecoder(sampleRate, channels),
        AudioFormat.Flac => new FlacAudioDecoder(sampleRate, channels),
        _                => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
