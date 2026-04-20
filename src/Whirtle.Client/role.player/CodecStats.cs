// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec;

namespace Whirtle.Client.Role;

/// <summary>
/// Accumulated statistics for a single codec observed during a stream.
/// </summary>
public record CodecStats(
    AudioFormat Format,
    long        ChunkCount,
    long        EncodedBytes,
    long        DecodedBytes)
{
    /// <summary>
    /// Ratio of decoded PCM bytes to encoded bytes.
    /// Values greater than 1.0 indicate the codec expanded the data (typical for lossless/PCM).
    /// Returns 0.0 when no data has been received.
    /// </summary>
    public double AverageCompressionRatio =>
        EncodedBytes > 0 ? (double)DecodedBytes / EncodedBytes : 0.0;
}
