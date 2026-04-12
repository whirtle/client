// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Decoded contents of a FLAC STREAMINFO metadata block.
/// This is the only metadata block the decoder requires.
/// </summary>
internal sealed record FlacStreamInfo(
    int    MinBlockSize,
    int    MaxBlockSize,
    int    MinFrameSize,
    int    MaxFrameSize,
    int    SampleRate,
    int    Channels,
    int    BitsPerSample,
    long   TotalSamples,
    byte[] Md5Signature);
