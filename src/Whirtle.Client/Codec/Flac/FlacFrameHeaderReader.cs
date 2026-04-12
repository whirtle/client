// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Parses a FLAC frame header from a byte span.
///
/// Frame header layout (all fields big-endian / MSB-first):
///   [14]  Sync code — must be 0x3FFE (14 ones followed by a zero)
///   [1]   Reserved
///   [1]   Blocking strategy (0 = fixed, 1 = variable)
///   [4]   Block size code
///   [4]   Sample rate code
///   [4]   Channel assignment code
///   [3]   Sample size code
///   [1]   Reserved
///   [?]   Frame/sample number (UTF-8 coded variable-length integer)
///   [0/8/16] Block size tail  (present when block size code is 6 or 7)
///   [0/8/16] Sample rate tail (present when sample rate code is 12, 13, or 14)
///   [8]   CRC-8 of all preceding header bytes
/// </summary>
internal static class FlacFrameHeaderReader
{
    private const uint SyncCode = 0x3FFE;

    // Block-size lookup for codes 2–5 and 8–15 (codes 6/7 read a tail; 0 is reserved).
    private static readonly int[] BlockSizeLookup =
    [
        0,    // 0: reserved — never stored here
        192,  // 1
        576,  576 * 2,  576 * 4,  576 * 8,  // 2–5
        0, 0,           // 6, 7: read from tail
        256,  256 * 2,  256 * 4,  256 * 8,  256 * 16,  256 * 32,  256 * 64,  256 * 128,  // 8–15
    ];

    // Sample-rate lookup for codes 1–11 (0 = STREAMINFO, 12–14 read a tail, 15 is invalid).
    private static readonly int[] SampleRateLookup =
        [0, 88_200, 176_400, 192_000, 8_000, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 96_000];

    // Bit-depth lookup for sample size codes 1–7 (0 = STREAMINFO, 3 is reserved).
    private static readonly int[] BitDepthLookup = [0, 8, 12, 0, 16, 20, 24, 32];

    /// <summary>
    /// Reads and validates a frame header from the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Buffer beginning at the first sync byte of the frame.</param>
    /// <param name="streamInfo">STREAMINFO, used as fallback for code 0 fields.</param>
    /// <param name="bytesConsumed">
    /// On return, the number of bytes consumed (i.e. the first subframe byte is at
    /// <c>data[bytesConsumed..]</c>).
    /// </param>
    /// <exception cref="InvalidDataException">
    /// Thrown on sync mismatch, reserved code, or CRC-8 failure.
    /// </exception>
    public static FlacFrameHeader Read(
        ReadOnlySpan<byte> data,
        FlacStreamInfo     streamInfo,
        out int            bytesConsumed)
    {
        var reader = new FlacBitReader(data);

        // ── Sync + blocking strategy ─────────────────────────────────────────
        uint sync = reader.ReadBits(14);
        if (sync != SyncCode)
            throw new InvalidDataException(
                $"FLAC frame sync not found: expected 0x{SyncCode:X4}, got 0x{sync:X4}.");

        reader.ReadBit();                               // reserved bit — value not checked
        bool isVariableBlockSize = reader.ReadBit();

        // ── Codes (2 bytes) ──────────────────────────────────────────────────
        int blockSizeCode  = (int)reader.ReadBits(4);
        int sampleRateCode = (int)reader.ReadBits(4);
        int channelCode    = (int)reader.ReadBits(4);
        int sampleSizeCode = (int)reader.ReadBits(3);
        reader.ReadBit();                               // reserved bit — value not checked

        // ── Frame/sample number (UTF-8 coded integer) ────────────────────────
        long frameOrSampleNumber = ReadUtf8CodedInt(ref reader);

        // ── Optional block-size tail (codes 6 and 7) ─────────────────────────
        int blockSize = blockSizeCode switch
        {
            0              => throw new InvalidDataException("Block size code 0 is reserved."),
            6              => (int)reader.ReadBits(8)  + 1,    // uint8  + 1
            7              => (int)reader.ReadBits(16) + 1,    // uint16 + 1
            _              => BlockSizeLookup[blockSizeCode],
        };

        // ── Optional sample-rate tail (codes 12–14) ──────────────────────────
        int sampleRate = sampleRateCode switch
        {
            0              => streamInfo.SampleRate,
            >= 1 and <= 11 => SampleRateLookup[sampleRateCode],
            12             => (int)reader.ReadBits(8)  * 1_000,  // kHz
            13             => (int)reader.ReadBits(16),           // Hz
            14             => (int)reader.ReadBits(16) * 10,      // tens of Hz
            _              => throw new InvalidDataException("Sample rate code 0xF is invalid."),
        };

        // ── CRC-8 ─────────────────────────────────────────────────────────────
        // Covers all header bytes up to (but not including) the CRC byte itself.
        int  headerByteCount = reader.BytePosition;
        byte expectedCrc     = FlacCrc.ComputeCrc8(data[..headerByteCount]);
        byte actualCrc       = reader.ReadByte();

        if (actualCrc != expectedCrc)
            throw new InvalidDataException(
                $"Frame header CRC-8 mismatch: expected 0x{expectedCrc:X2}, got 0x{actualCrc:X2}.");

        // ── Channel assignment ────────────────────────────────────────────────
        ChannelAssignment channelAssignment;
        int channels;

        switch (channelCode)
        {
            case 0x8: channelAssignment = ChannelAssignment.LeftSide;  channels = 2; break;
            case 0x9: channelAssignment = ChannelAssignment.RightSide; channels = 2; break;
            case 0xA: channelAssignment = ChannelAssignment.MidSide;   channels = 2; break;
            case >= 0x0 and <= 0x7:
                channelAssignment = ChannelAssignment.Independent;
                channels = channelCode + 1;
                break;
            default:
                throw new InvalidDataException(
                    $"Reserved channel assignment code: 0x{channelCode:X1}.");
        }

        // ── Bit depth ─────────────────────────────────────────────────────────
        if (sampleSizeCode == 3)
            throw new InvalidDataException("Sample size code 3 is reserved.");

        int bitsPerSample = sampleSizeCode == 0
            ? streamInfo.BitsPerSample
            : BitDepthLookup[sampleSizeCode];

        bytesConsumed = reader.BytePosition;

        return new FlacFrameHeader(
            blockSize, sampleRate, channels,
            channelAssignment, bitsPerSample,
            frameOrSampleNumber, isVariableBlockSize);
    }

    // ── UTF-8 coded integer ───────────────────────────────────────────────────
    // FLAC uses the UTF-8 multi-byte encoding scheme to store the frame or
    // sample number in 1–7 bytes.  Fixed-block-size streams encode the frame
    // number (≤ 2^31−1, up to 6 bytes); variable-block-size streams encode the
    // first sample number (≤ 2^36−1, up to 7 bytes).

    private static long ReadUtf8CodedInt(ref FlacBitReader reader)
    {
        byte first = reader.ReadByte();

        // 1-byte: 0xxxxxxx
        if ((first & 0x80) == 0)
            return first;

        int  extraBytes;
        long value;

        // Test from most leading-ones (longest) to fewest, so each check is unambiguous.
        if      (first == 0xFE)              { extraBytes = 6; value = 0;            }  // 11111110
        else if ((first & 0xFE) == 0xFC)     { extraBytes = 5; value = first & 0x01; }  // 1111110x
        else if ((first & 0xFC) == 0xF8)     { extraBytes = 4; value = first & 0x03; }  // 111110xx
        else if ((first & 0xF8) == 0xF0)     { extraBytes = 3; value = first & 0x07; }  // 11110xxx
        else if ((first & 0xF0) == 0xE0)     { extraBytes = 2; value = first & 0x0F; }  // 1110xxxx
        else if ((first & 0xE0) == 0xC0)     { extraBytes = 1; value = first & 0x1F; }  // 110xxxxx
        else throw new InvalidDataException(
            $"Invalid UTF-8 coded integer first byte: 0x{first:X2}.");

        for (int i = 0; i < extraBytes; i++)
        {
            byte cont = reader.ReadByte();
            if ((cont & 0xC0) != 0x80)
                throw new InvalidDataException(
                    $"Invalid UTF-8 continuation byte: 0x{cont:X2}.");
            value = (value << 6) | (uint)(cont & 0x3F);
        }

        return value;
    }
}
