// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Codec;

/// <summary>
/// FLAC decoder stub.
///
/// NOTE: The originally specified "NFlac" package does not exist on NuGet.
/// Replace this stub with a real implementation once a FLAC decoding library
/// has been chosen (e.g. CUETools.Codecs.FLAKE-Reloaded, or a native libFLAC
/// binding). The <see cref="IAudioDecoder"/> contract is stable — only this
/// class needs updating.
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

    public AudioFrame Decode(ReadOnlyMemory<byte> data) =>
        throw new NotSupportedException(
            "FLAC decoding is not yet implemented. " +
            "Provide a concrete IAudioDecoder wrapping a FLAC library of your choice.");

    public void Dispose() { }
}
