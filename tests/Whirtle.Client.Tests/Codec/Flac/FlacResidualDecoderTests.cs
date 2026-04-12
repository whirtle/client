// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacResidualDecoderTests
{
    // -----------------------------------------------------------------------
    // ZigZag signed fold
    // -----------------------------------------------------------------------

    // The fold maps the non-negative ZigZag raw value to a signed integer:
    //   even raw → raw/2 (non-negative)
    //   odd  raw → ~(raw/2) = -(raw/2 + 1) (negative)

    [Theory]
    [InlineData(0,  0)]
    [InlineData(2,  1)]
    [InlineData(4,  2)]
    [InlineData(6,  3)]
    [InlineData(1, -1)]
    [InlineData(3, -2)]
    [InlineData(5, -3)]
    [InlineData(7, -4)]
    public void ZigZag_AllCases(uint raw, int expected)
    {
        // Construct a Rice param-0 stream encoding a single residual with the given raw value.
        // Rice param 0: quotient = raw, remainder = 0 → unary(raw) + stop bit.
        var bs = new BitStream();
        bs.Write(0, 2);     // coding method = Rice
        bs.Write(0, 4);     // partition order = 0
        bs.Write(0, 4);     // rice param = 0
        bs.WriteUnary((int)raw); // encodes the value

        var result = Decode(bs, blockSize: 1, predictorOrder: 0);
        Assert.Equal(expected, result[0]);
    }

    // -----------------------------------------------------------------------
    // Rice param 0 — all zeros
    // -----------------------------------------------------------------------

    [Fact]
    public void AllZeros_RiceParam0_AllResultsZero()
    {
        const int n = 8;
        var bs = new BitStream();
        bs.Write(0, 2);         // Rice
        bs.Write(0, 4);         // 1 partition
        bs.Write(0, 4);         // rice param 0
        for (int i = 0; i < n; i++)
            bs.WriteUnary(0);   // raw=0 → single stop bit

        var result = Decode(bs, blockSize: n, predictorOrder: 0);
        Assert.Equal(Enumerable.Repeat(0, n).ToArray(), result);
    }

    // -----------------------------------------------------------------------
    // Rice param 1 — mixed signs
    // -----------------------------------------------------------------------

    [Fact]
    public void RiceParam1_MixedValues()
    {
        // Residuals to encode: [0, 1, -1, 2, -2]
        // ZigZag:              [0, 2,  1, 4,  3]
        (int residual, uint raw)[] pairs =
            [(0, 0), (1, 2), (-1, 1), (2, 4), (-2, 3)];

        var bs = BuildRiceStream(riceParam: 1, pairs);
        var result = Decode(bs, blockSize: pairs.Length, predictorOrder: 0);

        Assert.Equal(pairs.Select(p => p.residual).ToArray(), result);
    }

    // -----------------------------------------------------------------------
    // Rice param 2
    // -----------------------------------------------------------------------

    [Fact]
    public void RiceParam2_CorrectValues()
    {
        // Residuals: [5, -6, 3, -1]
        // ZigZag:    [10, 11, 6, 1]
        (int residual, uint raw)[] pairs =
            [(5, 10), (-6, 11), (3, 6), (-1, 1)];

        var bs = BuildRiceStream(riceParam: 2, pairs);
        var result = Decode(bs, blockSize: pairs.Length, predictorOrder: 0);

        Assert.Equal(pairs.Select(p => p.residual).ToArray(), result);
    }

    // -----------------------------------------------------------------------
    // Partition order
    // -----------------------------------------------------------------------

    [Fact]
    public void PartitionOrder1_Partition0SizeSubtractsPredictorOrder()
    {
        // blockSize=8, predictorOrder=2, partitionOrder=1 → 2 partitions
        //   partition 0: (8>>1) - 2 = 2 residuals
        //   partition 1:  8>>1      = 4 residuals
        // All residuals are 0 (rice param 0).
        var bs = new BitStream();
        bs.Write(0, 2);   // Rice
        bs.Write(1, 4);   // partition order = 1
        // Partition 0 (2 residuals)
        bs.Write(0, 4);
        bs.WriteUnary(0); bs.WriteUnary(0);
        // Partition 1 (4 residuals)
        bs.Write(0, 4);
        bs.WriteUnary(0); bs.WriteUnary(0); bs.WriteUnary(0); bs.WriteUnary(0);

        var result = Decode(bs, blockSize: 8, predictorOrder: 2);
        Assert.Equal(6, result.Length);              // 8 - 2 = 6 total
        Assert.All(result, r => Assert.Equal(0, r));
    }

    [Fact]
    public void PartitionOrder2_FourPartitions_CorrectCounts()
    {
        // blockSize=16, predictorOrder=4, partitionOrder=2 → 4 partitions
        //   partition 0: (16>>2) - 4 = 0 residuals
        //   partitions 1–3: 16>>2 = 4 residuals each
        // Total residuals = 12 = 16 - 4
        const int blockSize = 16, order = 4;

        var bs = new BitStream();
        bs.Write(0, 2);   // Rice
        bs.Write(2, 4);   // partition order = 2
        // Partition 0: 0 residuals
        bs.Write(0, 4);
        // Partitions 1–3: 4 residuals each (all zero)
        for (int p = 1; p < 4; p++)
        {
            bs.Write(0, 4);
            for (int i = 0; i < 4; i++) bs.WriteUnary(0);
        }

        var result = Decode(bs, blockSize, order);
        Assert.Equal(blockSize - order, result.Length);
        Assert.All(result, r => Assert.Equal(0, r));
    }

    [Fact]
    public void PartitionOrder0_UsesEntireBlock()
    {
        // With partitionOrder=0 there is exactly one partition covering all
        // blockSize − predictorOrder residuals.
        const int blockSize = 6, predOrder = 2;
        int[] expected = [1, -1, 2, -2];  // 4 residuals

        var bs = BuildRiceStream(
            riceParam: 1, partitionOrder: 0, predOrder: predOrder,
            pairs: [(1, 2), (-1, 1), (2, 4), (-2, 3)]);

        Assert.Equal(expected, Decode(bs, blockSize, predOrder));
    }

    // -----------------------------------------------------------------------
    // Escape code (unencoded binary)
    // -----------------------------------------------------------------------

    [Fact]
    public void EscapeCode_UnencodedBinary_Signed4Bit()
    {
        // Residuals: [3, -1, -4]
        // Stored as 4-bit 2's complement (not ZigZag).
        var bs = new BitStream();
        bs.Write(0, 2);   // Rice
        bs.Write(0, 4);   // partition order = 0
        bs.Write(0xF, 4); // escape code
        bs.Write(4, 5);   // 4 bits per sample
        bs.Write(3,  4);  //  3 = 0b0011
        bs.Write(-1, 4);  // -1 = 0b1111
        bs.Write(-4, 4);  // -4 = 0b1100

        var result = Decode(bs, blockSize: 3, predictorOrder: 0);
        Assert.Equal([3, -1, -4], result);
    }

    [Fact]
    public void EscapeCode_Rice2_EscapeIs0x1F()
    {
        // With Rice2 (coding method 01), the escape code is 0x1F (all 5 bits set).
        var bs = new BitStream();
        bs.Write(1, 2);    // Rice2
        bs.Write(0, 4);    // partition order = 0
        bs.Write(0x1F, 5); // escape code (5-bit)
        bs.Write(8, 5);    // 8 bits per sample
        bs.Write(100, 8);  //  100 (8-bit signed, positive)
        bs.Write(-50, 8);  // -50 (8-bit signed, negative)

        var result = Decode(bs, blockSize: 2, predictorOrder: 0);
        Assert.Equal([100, -50], result);
    }

    // -----------------------------------------------------------------------
    // Rice2 coding method (5-bit parameter)
    // -----------------------------------------------------------------------

    [Fact]
    public void Rice2CodingMethod_FiveBitParam()
    {
        // Residuals: [2, -3] → ZigZag: [4, 5]
        // Rice param = 2, quotient = raw>>2, remainder = raw & 3
        var bs = new BitStream();
        bs.Write(1, 2);    // Rice2
        bs.Write(0, 4);    // partition order = 0
        bs.Write(2, 5);    // rice param = 2 (5-bit for Rice2)
        // raw=4: q=1, r=0
        bs.WriteUnary(1); bs.Write(0, 2);
        // raw=5: q=1, r=1
        bs.WriteUnary(1); bs.Write(1, 2);

        var result = Decode(bs, blockSize: 2, predictorOrder: 0);
        Assert.Equal([2, -3], result);
    }

    // -----------------------------------------------------------------------
    // Large unary quotient
    // -----------------------------------------------------------------------

    [Fact]
    public void LargeQuotient_QuotientOf7_DecodesCorrectly()
    {
        // raw=7 with rice param=0: 7 zero bits then stop → decoded = ~(7>>1) = ~3 = -4
        var bs = new BitStream();
        bs.Write(0, 2);      // Rice
        bs.Write(0, 4);      // partition order = 0
        bs.Write(0, 4);      // rice param = 0
        bs.WriteUnary(7);    // 7 zeros + stop bit

        var result = Decode(bs, blockSize: 1, predictorOrder: 0);
        Assert.Equal(-4, result[0]);
    }

    // -----------------------------------------------------------------------
    // Error cases
    // -----------------------------------------------------------------------

    [Fact]
    public void ReservedCodingMethod_10_Throws()
    {
        var bs = new BitStream();
        bs.Write(2, 2);  // coding method 10 = reserved
        bs.Write(0, 4);  // (padding so the reader has enough bytes)
        bs.Write(0, 16);
        Assert.Throws<InvalidDataException>(() => Decode(bs, blockSize: 1, predictorOrder: 0));
    }

    [Fact]
    public void ReservedCodingMethod_11_Throws()
    {
        var bs = new BitStream();
        bs.Write(3, 2);  // coding method 11 = reserved
        bs.Write(0, 20);
        Assert.Throws<InvalidDataException>(() => Decode(bs, blockSize: 1, predictorOrder: 0));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int[] Decode(BitStream bs, int blockSize, int predictorOrder)
    {
        var bytes  = bs.ToBytes();
        var reader = new FlacBitReader(bytes);
        return FlacResidualDecoder.Decode(ref reader, blockSize, predictorOrder);
    }

    /// <summary>
    /// Builds a single-partition Rice bit stream from (residual, raw) pairs.
    /// <paramref name="raw"/> must equal ZigZag(residual).
    /// </summary>
    private static BitStream BuildRiceStream(
        int riceParam,
        (int residual, uint raw)[] pairs,
        int partitionOrder = 0,
        int predOrder      = 0)
    {
        var bs = new BitStream();
        bs.Write(0, 2);              // Rice (coding method 0)
        bs.Write(partitionOrder, 4);
        bs.Write(riceParam, 4);      // single partition's rice param
        foreach (var (_, raw) in pairs)
        {
            int quotient = (int)(raw >> riceParam);
            uint remainder = raw & ((1u << riceParam) - 1);
            bs.WriteUnary(quotient);
            if (riceParam > 0) bs.Write((int)remainder, riceParam);
        }
        return bs;
    }

    // -----------------------------------------------------------------------
    // BitStream — packs bits MSB-first, the mirror of FlacBitReader.
    // Used only to construct test input byte arrays.
    // -----------------------------------------------------------------------

    private sealed class BitStream
    {
        private readonly List<int> _bits = [];

        /// <summary>Appends the low <paramref name="count"/> bits of <paramref name="value"/> MSB-first.</summary>
        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                _bits.Add((value >> i) & 1);
        }

        /// <summary>
        /// Writes the unary encoding of <paramref name="quotient"/>:
        /// <paramref name="quotient"/> zero bits followed by a one (the stop bit).
        /// </summary>
        public void WriteUnary(int quotient)
        {
            for (int i = 0; i < quotient; i++)
                _bits.Add(0);
            _bits.Add(1);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                bytes[i >> 3] |= (byte)(_bits[i] << (7 - (i & 7)));
            return bytes;
        }
    }
}
