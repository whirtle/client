// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Decodes a single FLAC subframe (one channel of one audio frame).
///
/// Subframe header layout:
///   [1]   Zero padding bit
///   [6]   Subframe type code
///           0x00        CONSTANT  — one value repeated for all samples
///           0x01        VERBATIM  — all samples stored uncompressed
///           0x08–0x0C   FIXED     — fixed predictor, order = code − 0x08 (0–4)
///           0x20–0x3F   LPC       — FIR predictor, order = code − 0x20 + 1 (1–32)
///           all others  reserved
///   [1]   Wasted-bits-per-sample flag
///   [k-1 unary] k = wasted bits (only present when flag is 1)
///               Encoded as k−1 zero bits followed by a one bit.
///
/// For FIXED and LPC, <c>order</c> uncompressed warm-up samples precede the
/// residual block.  The residual is decoded by <see cref="FlacResidualDecoder"/>.
///
/// LPC-specific fields between warm-up and residual:
///   [4]   qlp_coeff_precision_minus_one  → actual precision = value + 1
///   [5]   qlp_shift  (signed)
///   [order × precision]  quantized LP coefficients (signed)
///
/// Wasted-bits samples are stored with <c>bitsPerSample − wastedBits</c> bits;
/// each decoded sample is left-shifted by <c>wastedBits</c> before being returned.
/// </summary>
internal static class FlacSubframeDecoder
{
    /// <summary>
    /// Decodes one subframe and returns <paramref name="blockSize"/> signed audio samples.
    /// </summary>
    /// <param name="reader">Bit reader positioned at the first bit of this subframe.</param>
    /// <param name="blockSize">Number of samples in this frame.</param>
    /// <param name="bitsPerSample">
    /// Nominal bit depth for this channel (side channels in stereo difference modes
    /// should pass <c>nominalBitsPerSample + 1</c>).
    /// </param>
    public static int[] Decode(ref FlacBitReader reader, int blockSize, int bitsPerSample)
    {
        // ── Subframe header ──────────────────────────────────────────────────
        reader.ReadBit();                       // zero padding — value not checked
        int typeCode = (int)reader.ReadBits(6);
        bool hasWastedBits = reader.ReadBit();

        int wastedBits = 0;
        if (hasWastedBits)
        {
            // k is encoded as k−1 zero bits followed by a one; minimum k = 1.
            wastedBits = 1;
            while (!reader.ReadBit())
                wastedBits++;
            bitsPerSample -= wastedBits;
        }

        // ── Subframe body ────────────────────────────────────────────────────
        int[] samples = typeCode switch
        {
            0x00                        => DecodeConstant(ref reader, blockSize, bitsPerSample),
            0x01                        => DecodeVerbatim(ref reader, blockSize, bitsPerSample),
            >= 0x08 and <= 0x0C         => DecodeFixed(ref reader, blockSize, bitsPerSample, order: typeCode - 0x08),
            >= 0x20 and <= 0x3F         => DecodeLpc(ref reader, blockSize, bitsPerSample, order: typeCode - 0x20 + 1),
            _                           => throw new InvalidDataException($"Reserved subframe type: 0x{typeCode:X2}."),
        };

        // ── Restore wasted bits ──────────────────────────────────────────────
        if (wastedBits > 0)
        {
            for (int i = 0; i < samples.Length; i++)
                samples[i] <<= wastedBits;
        }

        return samples;
    }

    // ── CONSTANT ─────────────────────────────────────────────────────────────

    private static int[] DecodeConstant(ref FlacBitReader reader, int blockSize, int bitsPerSample)
    {
        int value = reader.ReadSignedBits(bitsPerSample);
        var samples = new int[blockSize];
        Array.Fill(samples, value);
        return samples;
    }

    // ── VERBATIM ─────────────────────────────────────────────────────────────

    private static int[] DecodeVerbatim(ref FlacBitReader reader, int blockSize, int bitsPerSample)
    {
        var samples = new int[blockSize];
        for (int i = 0; i < blockSize; i++)
            samples[i] = reader.ReadSignedBits(bitsPerSample);
        return samples;
    }

    // ── FIXED ─────────────────────────────────────────────────────────────────

    private static readonly int[][] FixedCoeffs =
    [
        [],                         // order 0: no prediction
        [1],                        // order 1: s[n-1]
        [2, -1],                    // order 2: 2s[n-1] − s[n-2]
        [3, -3, 1],                 // order 3: 3s[n-1] − 3s[n-2] + s[n-3]
        [4, -6, 4, -1],             // order 4: 4s[n-1] − 6s[n-2] + 4s[n-3] − s[n-4]
    ];

    private static int[] DecodeFixed(ref FlacBitReader reader, int blockSize, int bitsPerSample, int order)
    {
        var samples = new int[blockSize];

        for (int i = 0; i < order; i++)
            samples[i] = reader.ReadSignedBits(bitsPerSample);

        var residuals = FlacResidualDecoder.Decode(ref reader, blockSize, order);
        int[] coeffs  = FixedCoeffs[order];

        for (int i = 0; i < residuals.Length; i++)
        {
            int s = i + order;
            long prediction = 0;
            for (int j = 0; j < order; j++)
                prediction += (long)coeffs[j] * samples[s - 1 - j];
            samples[s] = (int)prediction + residuals[i];
        }

        return samples;
    }

    // ── LPC ──────────────────────────────────────────────────────────────────

    private static int[] DecodeLpc(ref FlacBitReader reader, int blockSize, int bitsPerSample, int order)
    {
        var samples = new int[blockSize];

        for (int i = 0; i < order; i++)
            samples[i] = reader.ReadSignedBits(bitsPerSample);

        int precision = (int)reader.ReadBits(4) + 1;
        int shift     = reader.ReadSignedBits(5);

        var coeffs = new int[order];
        for (int i = 0; i < order; i++)
            coeffs[i] = reader.ReadSignedBits(precision);

        var residuals = FlacResidualDecoder.Decode(ref reader, blockSize, order);

        for (int i = 0; i < residuals.Length; i++)
        {
            int s = i + order;
            long sum = 0;
            for (int j = 0; j < order; j++)
                sum += (long)coeffs[j] * samples[s - 1 - j];

            long prediction = shift >= 0 ? sum >> shift : sum << -shift;
            samples[s] = (int)prediction + residuals[i];
        }

        return samples;
    }
}
