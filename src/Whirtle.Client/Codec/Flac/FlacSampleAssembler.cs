// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Converts the decoded per-channel sample arrays produced by
/// <see cref="FlacChannelDecoder"/> into a flat interleaved <see cref="short"/> array
/// suitable for <see cref="Whirtle.Client.Codec.AudioFrame"/>.
///
/// Scaling to 16 bits:
///   bitsPerSample &gt; 16 — right-shift by (bitsPerSample − 16), discarding LSBs.
///   bitsPerSample == 16 — no change.
///   bitsPerSample &lt; 16 — left-shift by (16 − bitsPerSample), filling LSBs with zeros.
///
/// Interleaving order matches <see cref="Whirtle.Client.Codec.AudioFrame"/>:
///   [ch0[0], ch1[0], …, chN[0], ch0[1], ch1[1], …]
/// </summary>
internal static class FlacSampleAssembler
{
    /// <summary>
    /// Scales and interleaves all channel samples into one <see cref="short"/> array.
    /// </summary>
    /// <param name="channelSamples">
    /// Per-channel arrays, each of the same length (blockSize).
    /// </param>
    /// <param name="bitsPerSample">
    /// Native bit depth of the decoded samples (4–32).
    /// </param>
    public static short[] Assemble(int[][] channelSamples, int bitsPerSample)
    {
        int channels  = channelSamples.Length;
        int blockSize = channelSamples[0].Length;
        var output    = new short[blockSize * channels];

        int shift = bitsPerSample - 16;

        for (int i = 0; i < blockSize; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int scaled = shift >= 0
                    ? channelSamples[c][i] >> shift
                    : channelSamples[c][i] << -shift;

                output[i * channels + c] = (short)scaled;
            }
        }

        return output;
    }
}
