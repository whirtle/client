// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// A forward-only, MSB-first bit reader over a <see cref="ReadOnlySpan{T}"/> of bytes.
/// FLAC streams are bit-addressed with the most-significant bit of each byte first.
/// </summary>
internal ref struct FlacBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitOffset; // total bits consumed so far

    public FlacBitReader(ReadOnlySpan<byte> data)
    {
        _data      = data;
        _bitOffset = 0;
    }

    /// <summary>Total bits consumed so far.</summary>
    public readonly int BitsConsumed => _bitOffset;

    /// <summary>Number of complete bytes consumed (i.e. the current byte index when byte-aligned).</summary>
    public readonly int BytePosition => _bitOffset >> 3;

    /// <summary>Whether the reader is currently on a byte boundary.</summary>
    public readonly bool IsAligned => (_bitOffset & 7) == 0;

    /// <summary>
    /// Reads <paramref name="count"/> bits (1–32) MSB-first and returns them as the
    /// low-order bits of a <see cref="uint"/>.
    /// </summary>
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be between 0 and 32.");

        uint result = 0;
        int  remaining = count;

        while (remaining > 0)
        {
            int byteIdx              = _bitOffset >> 3;
            int bitIdxInByte         = _bitOffset & 7;         // 0 = MSB of this byte
            int bitsAvailableInByte  = 8 - bitIdxInByte;
            int bitsToRead           = remaining < bitsAvailableInByte
                                        ? remaining
                                        : bitsAvailableInByte;

            // Shift the relevant bits of this byte to the LSB position, then mask.
            uint bits = (uint)(_data[byteIdx] >> (bitsAvailableInByte - bitsToRead))
                        & ((1u << bitsToRead) - 1);

            result     = (result << bitsToRead) | bits;
            _bitOffset += bitsToRead;
            remaining  -= bitsToRead;
        }

        return result;
    }

    /// <summary>
    /// Reads <paramref name="count"/> bits (1–32) as a sign-extended <see cref="int"/>.
    /// The MSB of the <paramref name="count"/>-bit field is treated as the sign bit.
    /// </summary>
    public int ReadSignedBits(int count)
    {
        if ((uint)count > 32)
            throw new ArgumentOutOfRangeException(nameof(count), "count must be between 1 and 32.");

        int raw = (int)ReadBits(count);
        // Arithmetic right shift sign-extends from the count-bit representation.
        return (raw << (32 - count)) >> (32 - count);
    }

    /// <summary>Reads a single bit; returns <c>true</c> for 1, <c>false</c> for 0.</summary>
    public bool ReadBit() => ReadBits(1) != 0;

    /// <summary>Reads exactly 8 bits and returns them as a <see cref="byte"/>.</summary>
    public byte ReadByte() => (byte)ReadBits(8);

    /// <summary>Skips <paramref name="count"/> bits without returning a value.</summary>
    public void SkipBits(int count)
    {
        if ((uint)count > (uint)((_data.Length << 3) - _bitOffset))
            throw new ArgumentOutOfRangeException(nameof(count), "Not enough bits remaining to skip.");
        _bitOffset += count;
    }

    /// <summary>
    /// Advances the reader to the next byte boundary.
    /// If already aligned, this is a no-op.
    /// </summary>
    public void AlignToByte()
    {
        int remainder = _bitOffset & 7;
        if (remainder != 0)
            _bitOffset += 8 - remainder;
    }

    /// <summary>
    /// Reads the next <paramref name="length"/> bytes as a span, consuming
    /// them from the stream. The reader must be byte-aligned.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        if (!IsAligned)
            throw new InvalidOperationException("Reader must be byte-aligned before calling ReadBytes.");
        var slice = _data.Slice(BytePosition, length);
        _bitOffset += length << 3;
        return slice;
    }
}
