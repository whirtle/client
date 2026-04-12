// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Decodes all channel subframes for one FLAC frame and reconstructs the
/// original per-channel audio samples.
///
/// For <see cref="ChannelAssignment.Independent"/> each subframe is decoded
/// at the nominal <c>bitsPerSample</c>.  For the three stereo difference-coding
/// modes the "side" subframe uses one extra bit because side samples have a
/// wider range than the original signal.
///
/// Stereo reconstruction formulae:
///
///   LeftSide  (ch0 = left, ch1 = side = left − right):
///     right[i] = left[i] − side[i]
///
///   RightSide (ch0 = side = left − right, ch1 = right):
///     left[i]  = right[i] + side[i]
///
///   MidSide   (ch0 = mid = ⌊(left+right)/2⌋, ch1 = side = left − right):
///     The encoder truncates the mid value, losing the LSB when left+right is
///     odd.  That bit is stored in the LSB of the side sample and must be
///     restored before reconstruction:
///       mid′  = (mid &lt;&lt; 1) | (side &amp; 1)
///       left  = (mid′ + side) >> 1
///       right = (mid′ − side) >> 1
/// </summary>
internal static class FlacChannelDecoder
{
    /// <summary>
    /// Decodes all channel subframes and returns one <c>int[]</c> per channel,
    /// each of length <c>header.BlockSize</c>.
    /// </summary>
    /// <param name="reader">
    /// Bit reader positioned at the first bit of the first subframe.
    /// </param>
    /// <param name="header">Decoded frame header for this frame.</param>
    public static int[][] Decode(ref FlacBitReader reader, FlacFrameHeader header)
    {
        int blockSize     = header.BlockSize;
        int bitsPerSample = header.BitsPerSample;

        var ch = new int[header.Channels][];

        switch (header.ChannelAssignment)
        {
            case ChannelAssignment.Independent:
                for (int c = 0; c < header.Channels; c++)
                    ch[c] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample);
                break;

            case ChannelAssignment.LeftSide:
                // ch[0] = left  (nominal bps)
                // ch[1] = side  (bps + 1)
                ch[0] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample);
                ch[1] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample + 1);
                // Recover right from left − side.
                for (int i = 0; i < blockSize; i++)
                    ch[1][i] = ch[0][i] - ch[1][i];
                break;

            case ChannelAssignment.RightSide:
                // ch[0] = side  (bps + 1)
                // ch[1] = right (nominal bps)
                ch[0] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample + 1);
                ch[1] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample);
                // Recover left from right + side.
                for (int i = 0; i < blockSize; i++)
                    ch[0][i] = ch[1][i] + ch[0][i];
                break;

            case ChannelAssignment.MidSide:
                // ch[0] = mid   (nominal bps)
                // ch[1] = side  (bps + 1)
                ch[0] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample);
                ch[1] = FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample + 1);
                // Restore the LSB that was lost during mid-averaging, then reconstruct.
                for (int i = 0; i < blockSize; i++)
                {
                    int mid  = (ch[0][i] << 1) | (ch[1][i] & 1);
                    int side = ch[1][i];
                    ch[0][i] = (mid + side) >> 1;   // left
                    ch[1][i] = (mid - side) >> 1;   // right
                }
                break;
        }

        return ch;
    }
}
