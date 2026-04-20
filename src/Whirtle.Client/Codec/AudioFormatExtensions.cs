// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec;

public static class AudioFormatExtensions
{
    public static string ToCodecString(this AudioFormat format) => format switch
    {
        AudioFormat.Flac => "flac",
        AudioFormat.Pcm  => "pcm",
        _                => "opus",
    };

    public static AudioFormat FromCodecString(string codec) => codec.ToLowerInvariant() switch
    {
        "flac" => AudioFormat.Flac,
        "opus" => AudioFormat.Opus,
        _      => AudioFormat.Pcm,
    };
}
