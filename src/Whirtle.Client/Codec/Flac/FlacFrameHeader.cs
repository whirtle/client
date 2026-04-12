// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// How the audio channels are stored in a FLAC frame.
/// Independent means each channel is stored separately with no inter-channel
/// prediction; the three stereo modes use difference coding to improve compression.
/// </summary>
internal enum ChannelAssignment
{
    /// <summary>1–8 channels, each stored independently.</summary>
    Independent,

    /// <summary>
    /// Two channels: channel 0 = left, channel 1 = side (left − right).
    /// The side subframe is decoded with <c>BitsPerSample + 1</c> bits.
    /// </summary>
    LeftSide,

    /// <summary>
    /// Two channels: channel 0 = side (left − right), channel 1 = right.
    /// The side subframe is decoded with <c>BitsPerSample + 1</c> bits.
    /// </summary>
    RightSide,

    /// <summary>
    /// Two channels: channel 0 = mid ((left+right)÷2), channel 1 = side (left − right).
    /// The side subframe is decoded with <c>BitsPerSample + 1</c> bits.
    /// </summary>
    MidSide,
}

/// <summary>
/// Decoded FLAC frame header. Describes one audio frame (a block of inter-channel samples).
/// </summary>
internal sealed record FlacFrameHeader(
    /// <summary>Number of inter-channel samples in this frame.</summary>
    int BlockSize,

    /// <summary>Sample rate in Hz for this frame.</summary>
    int SampleRate,

    /// <summary>Total number of channels in this frame.</summary>
    int Channels,

    /// <summary>How the channels are stored (independent or difference-coded).</summary>
    ChannelAssignment ChannelAssignment,

    /// <summary>
    /// Nominal bits per sample for independent channels.
    /// Side channels in stereo difference modes use <c>BitsPerSample + 1</c>.
    /// </summary>
    int BitsPerSample,

    /// <summary>Frame number (fixed block size) or first sample number (variable block size).</summary>
    long FrameOrSampleNumber,

    /// <summary><c>true</c> when blocking strategy is variable; <c>false</c> for fixed.</summary>
    bool IsVariableBlockSize);
