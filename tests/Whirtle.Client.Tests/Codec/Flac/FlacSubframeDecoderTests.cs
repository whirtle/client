// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Whirtle.Client.Codec.Flac;

namespace Whirtle.Client.Tests.Codec.Flac;

public class FlacSubframeDecoderTests
{
    // -----------------------------------------------------------------------
    // CONSTANT subframe
    // -----------------------------------------------------------------------

    [Fact]
    public void Constant_PositiveValue_AllSamplesEqual()
    {
        // type=0x00, no wasted bits, value=42 in 16 bits
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x00, wastedBits: 0);
        bs.Write(42, 16);

        var result = Decode(bs, blockSize: 4, bitsPerSample: 16);
        Assert.Equal(new[] { 42, 42, 42, 42 }, result);
    }

    [Fact]
    public void Constant_NegativeValue_AllSamplesEqual()
    {
        // value=-1, 8-bit 2's complement = 0xFF
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x00, wastedBits: 0);
        bs.Write(-1, 8);

        var result = Decode(bs, blockSize: 3, bitsPerSample: 8);
        Assert.Equal(new[] { -1, -1, -1 }, result);
    }

    // -----------------------------------------------------------------------
    // VERBATIM subframe
    // -----------------------------------------------------------------------

    [Fact]
    public void Verbatim_ReadsAllSamplesInOrder()
    {
        int[] expected = [10, -5, 0, 127, -128];
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x01, wastedBits: 0);
        foreach (int s in expected) bs.Write(s, 8);

        Assert.Equal(expected, Decode(bs, blockSize: expected.Length, bitsPerSample: 8));
    }

    // -----------------------------------------------------------------------
    // FIXED subframe
    // -----------------------------------------------------------------------

    [Fact]
    public void Fixed_Order0_SamplesEqualResiduals()
    {
        // order=0 → prediction=0 → sample[i] = residual[i]
        // Residuals (Rice param 0): [0, 0, 0, 0] → raw ZigZag [0,0,0,0]
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x08, wastedBits: 0); // FIXED order 0
        WriteRiceResiduals(bs, riceParam: 0, rawValues: [0, 0, 0, 0]);

        var result = Decode(bs, blockSize: 4, bitsPerSample: 16);
        Assert.Equal(new[] { 0, 0, 0, 0 }, result);
    }

    [Fact]
    public void Fixed_Order1_AppliesPrediction()
    {
        // warm-up: [100]
        // residuals (ZigZag): [0→0, 2→1, 1→-1, 4→2]
        // prediction order 1: s[n] = s[n-1] + residual[n]
        //   s[1] = 100 + 0  = 100
        //   s[2] = 100 + 1  = 101
        //   s[3] = 101 + -1 = 100
        //   s[4] = 100 + 2  = 102
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x09, wastedBits: 0); // FIXED order 1
        bs.Write(100, 16);                              // warm-up
        WriteRiceResiduals(bs, riceParam: 1, rawValues: [0, 2, 1, 4]);

        var result = Decode(bs, blockSize: 5, bitsPerSample: 16);
        Assert.Equal(new[] { 100, 100, 101, 100, 102 }, result);
    }

    [Fact]
    public void Fixed_Order2_AppliesSecondOrderPrediction()
    {
        // warm-up: [10, 15]
        // residuals: [0, 2, -1]
        // prediction: 2*s[n-1] - s[n-2]
        //   s[2] = 2*15 - 10     + 0  = 20
        //   s[3] = 2*20 - 15     + 2  = 27
        //   s[4] = 2*27 - 20     + -1 = 33
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x0A, wastedBits: 0); // FIXED order 2
        bs.Write(10, 16); bs.Write(15, 16);             // warm-up
        // ZigZag: 0→0, 2→1(no, 2 is even→2/2=1), wait: residual 0→raw 0, residual 2→raw 4, residual -1→raw 1
        WriteRiceResiduals(bs, riceParam: 1, rawValues: [0, 4, 1]);

        var result = Decode(bs, blockSize: 5, bitsPerSample: 16);
        Assert.Equal(new[] { 10, 15, 20, 27, 33 }, result);
    }

    [Fact]
    public void Fixed_Order4_LinearSequenceDecodesExactly()
    {
        // For a linear sequence a[n]=n+1, FIXED order 4 predicts perfectly (residuals=0).
        // warm-up: [1,2,3,4]
        // s[4] = 4*4 - 6*3 + 4*2 - 1 + 0 = 5
        // s[5] = 4*5 - 6*4 + 4*3 - 2 + 0 = 6
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x0C, wastedBits: 0); // FIXED order 4
        for (int i = 1; i <= 4; i++) bs.Write(i, 16);  // warm-up
        WriteRiceResiduals(bs, riceParam: 0, rawValues: [0, 0]);

        var result = Decode(bs, blockSize: 6, bitsPerSample: 16);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, result);
    }

    // -----------------------------------------------------------------------
    // LPC subframe
    // -----------------------------------------------------------------------

    [Fact]
    public void Lpc_Order2_DecodesArithmeticSequence()
    {
        // warm-up: [4, 7]   (d=3)
        // qlp_precision=4 (stored as 3), qlp_shift=0
        // coeffs: [2, -1]  → prediction = 2*s[n-1] - s[n-2]
        //   s[2] = (2*7 - 4) >> 0 + 0 = 10
        //   s[3] = (2*10 - 7) >> 0 + 0 = 13
        // residuals: [0, 0]
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x21, wastedBits: 0); // LPC order 2 (0x21 = 0x20 + 1)
        bs.Write(4, 16); bs.Write(7, 16);              // warm-up
        bs.Write(3, 4);                                // qlp_precision - 1 = 3 → precision=4
        bs.Write(0, 5);                                // qlp_shift = 0
        bs.Write(2, 4);                                // coeff[0] = 2
        bs.Write(-1, 4);                               // coeff[1] = -1
        WriteRiceResiduals(bs, riceParam: 0, rawValues: [0, 0]);

        var result = Decode(bs, blockSize: 4, bitsPerSample: 16);
        Assert.Equal(new[] { 4, 7, 10, 13 }, result);
    }

    [Fact]
    public void Lpc_Order1_WithShift_AppliesRightShift()
    {
        // warm-up: [8]
        // qlp_precision=4 (stored as 3), qlp_shift=1
        // coeffs: [2]  → prediction = (2*s[n-1]) >> 1 = s[n-1]
        //   s[1] = 8 + 0 = 8
        //   s[2] = 8 + 0 = 8
        // residuals: [0, 0]
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x20, wastedBits: 0); // LPC order 1
        bs.Write(8, 16);                               // warm-up
        bs.Write(3, 4);                                // qlp_precision - 1 = 3 → precision=4
        bs.Write(1, 5);                                // qlp_shift = 1
        bs.Write(2, 4);                                // coeff[0] = 2
        WriteRiceResiduals(bs, riceParam: 0, rawValues: [0, 0]);

        var result = Decode(bs, blockSize: 3, bitsPerSample: 16);
        Assert.Equal(new[] { 8, 8, 8 }, result);
    }

    // -----------------------------------------------------------------------
    // Wasted bits
    // -----------------------------------------------------------------------

    [Fact]
    public void WastedBits_ConstantSubframe_SamplesShifted()
    {
        // CONSTANT with 2 wasted bits, bitsPerSample=8.
        // Stored value uses 8-2=6 bits; store 5 → actual = 5<<2 = 20.
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x00, wastedBits: 2);
        bs.Write(5, 6); // 6 effective bits

        var result = Decode(bs, blockSize: 3, bitsPerSample: 8);
        Assert.Equal(new[] { 20, 20, 20 }, result);
    }

    [Fact]
    public void WastedBits_VerbatimSubframe_SamplesShifted()
    {
        // VERBATIM with 1 wasted bit, bitsPerSample=8 → 7 effective bits.
        // Stored values: [3, -2, 5]; actual: [6, -4, 10].
        var bs = new BitStream();
        WriteHeader(bs, typeCode: 0x01, wastedBits: 1);
        bs.Write(3, 7); bs.Write(-2, 7); bs.Write(5, 7);

        var result = Decode(bs, blockSize: 3, bitsPerSample: 8);
        Assert.Equal(new[] { 6, -4, 10 }, result);
    }

    // -----------------------------------------------------------------------
    // Reserved type codes
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0x02)] // 000010
    [InlineData(0x07)] // 000111
    [InlineData(0x0D)] // 001101 — above FIXED max (0x0C)
    [InlineData(0x1F)] // 011111 — below LPC start (0x20)
    public void ReservedType_Throws(int typeCode)
    {
        var bs = new BitStream();
        WriteHeader(bs, typeCode: typeCode, wastedBits: 0);
        bs.Write(0, 16); // padding so reader doesn't underflow
        Assert.Throws<InvalidDataException>(() => Decode(bs, blockSize: 1, bitsPerSample: 16));
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int[] Decode(BitStream bs, int blockSize, int bitsPerSample)
    {
        var bytes  = bs.ToBytes();
        var reader = new FlacBitReader(bytes);
        return FlacSubframeDecoder.Decode(ref reader, blockSize, bitsPerSample);
    }

    /// <summary>
    /// Writes a subframe header: [0] zero + [typeCode] 6 bits + wasted-bits flag + optional unary k.
    /// If wastedBits > 0 the flag is 1 and k-1 zeros followed by a one are written.
    /// </summary>
    private static void WriteHeader(BitStream bs, int typeCode, int wastedBits)
    {
        bs.Write(0, 1);          // zero padding
        bs.Write(typeCode, 6);   // type
        if (wastedBits == 0)
        {
            bs.Write(0, 1);      // no wasted bits
        }
        else
        {
            bs.Write(1, 1);      // wasted bits present
            // k-1 zeros then a one
            for (int i = 0; i < wastedBits - 1; i++) bs.Write(0, 1);
            bs.Write(1, 1);
        }
    }

    /// <summary>
    /// Appends a Rice (coding method 0, partition order 0) residual block.
    /// <paramref name="rawValues"/> are the non-negative ZigZag-encoded values.
    /// </summary>
    private static void WriteRiceResiduals(BitStream bs, int riceParam, uint[] rawValues)
    {
        bs.Write(0, 2);          // coding method = Rice
        bs.Write(0, 4);          // partition order = 0
        bs.Write(riceParam, 4);  // rice param
        foreach (uint raw in rawValues)
        {
            int  quotient  = (int)(raw >> riceParam);
            uint remainder = raw & ((1u << riceParam) - 1);
            bs.WriteUnary(quotient);
            if (riceParam > 0) bs.Write((int)remainder, riceParam);
        }
    }

    // -----------------------------------------------------------------------
    // BitStream — packs bits MSB-first, the mirror of FlacBitReader.
    // -----------------------------------------------------------------------

    private sealed class BitStream
    {
        private readonly List<int> _bits = [];

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
                _bits.Add((value >> i) & 1);
        }

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
