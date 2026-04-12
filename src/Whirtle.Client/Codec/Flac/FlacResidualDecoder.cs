// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Decodes the residual (error) signal that follows the warm-up samples in a FIXED
/// or LPC subframe, using FLAC's partitioned Rice coding.
///
/// Layout inside the bit stream:
///   [2]  Coding method — 00 = Rice (4-bit param), 01 = Rice2 (5-bit param)
///   [4]  Partition order — there are 2^order partitions
///   For each partition:
///     [4 or 5]  Rice parameter (or escape code if all bits are 1)
///     If escape:
///       [5]    Bits per sample for unencoded residuals in this partition
///       [n×bps] 2's-complement residual samples
///     Else (normal Rice partition):
///       For each residual in this partition:
///         [1+]  Unary-coded quotient (run of 0-bits terminated by a 1-bit)
///         [k]   Rice remainder (k = Rice parameter)
///         Decoded via ZigZag fold: decoded = (raw >> 1) ^ -(raw & 1)
///
/// Partition-0 residual count = (blockSize >> partitionOrder) − predictorOrder.
/// All other partitions: blockSize >> partitionOrder.
/// When partitionOrder == 0 the single partition holds blockSize − predictorOrder residuals.
/// </summary>
internal static class FlacResidualDecoder
{
    /// <summary>
    /// Decodes all residual samples for one subframe.
    /// </summary>
    /// <param name="reader">
    /// Bit reader positioned at the first bit of the residual block (immediately after
    /// the subframe warm-up samples).
    /// </param>
    /// <param name="blockSize">Total inter-channel samples in this frame.</param>
    /// <param name="predictorOrder">Number of warm-up samples (equals the FIXED or LPC order).</param>
    /// <returns>
    /// Array of <c>blockSize − predictorOrder</c> signed residual values.
    /// </returns>
    public static int[] Decode(ref FlacBitReader reader, int blockSize, int predictorOrder)
    {
        // ── Coding method ───────────────────────────────────────────────────
        int codingMethod = (int)reader.ReadBits(2);
        if (codingMethod > 1)
            throw new InvalidDataException(
                $"Reserved residual coding method: {codingMethod}.");

        // Rice uses a 4-bit parameter; Rice2 uses a 5-bit parameter.
        int paramBits  = codingMethod == 0 ? 4 : 5;
        int escapeCode = (1 << paramBits) - 1;   // 0xF for Rice, 0x1F for Rice2

        // ── Partition order ─────────────────────────────────────────────────
        int partitionOrder = (int)reader.ReadBits(4);
        int numPartitions  = 1 << partitionOrder;

        // Total residuals = all samples minus the warm-up samples.
        var residuals     = new int[blockSize - predictorOrder];
        int residualIndex = 0;

        for (int p = 0; p < numPartitions; p++)
        {
            int riceParam = (int)reader.ReadBits(paramBits);

            // Number of residuals in this partition.
            int partitionSize = partitionOrder == 0
                ? blockSize - predictorOrder
                : p == 0
                    ? (blockSize >> partitionOrder) - predictorOrder
                    : blockSize >> partitionOrder;

            if (riceParam == escapeCode)
            {
                // ── Escape: unencoded 2's-complement binary ─────────────────
                int bitsPerSample = (int)reader.ReadBits(5);
                for (int i = 0; i < partitionSize; i++)
                    residuals[residualIndex++] = reader.ReadSignedBits(bitsPerSample);
            }
            else
            {
                // ── Normal Rice partition ────────────────────────────────────
                for (int i = 0; i < partitionSize; i++)
                {
                    // Unary-coded quotient: count 0-bits, then consume the 1-bit stop.
                    int quotient = 0;
                    while (!reader.ReadBit())
                        quotient++;

                    // Unsigned remainder of riceParam bits.
                    uint remainder = riceParam > 0 ? reader.ReadBits(riceParam) : 0u;

                    // Reconstruct the non-negative ZigZag value and fold to signed.
                    uint raw = ((uint)quotient << riceParam) | remainder;
                    residuals[residualIndex++] = (raw & 1) == 0
                        ? (int)(raw >> 1)       // even → non-negative
                        : ~(int)(raw >> 1);     // odd  → negative (bitwise NOT = -(n+1))
                }
            }
        }

        return residuals;
    }
}
