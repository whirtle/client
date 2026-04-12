// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Codec.Flac;

/// <summary>
/// Reads the FLAC metadata section that precedes audio frames.
/// Verifies the four-byte <c>fLaC</c> stream marker, iterates every
/// metadata block until the last-block flag is seen, and extracts the
/// mandatory STREAMINFO block.  All other block types are skipped.
/// </summary>
internal static class FlacMetadataReader
{
    // FLAC stream marker: ASCII "fLaC"
    private static ReadOnlySpan<byte> Magic => [0x66, 0x4C, 0x61, 0x43];

    // Metadata block-type constants
    private const int BlockTypeStreamInfo    = 0;
    private const int StreamInfoLengthBytes  = 34;

    /// <summary>
    /// Reads the metadata section from the start of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Buffer containing a complete FLAC stream, starting at byte 0.</param>
    /// <param name="bytesConsumed">
    /// On return, the number of bytes consumed (past the last metadata block).
    /// Audio frame data begins at <c>data[bytesConsumed..]</c>.
    /// </param>
    /// <returns>The parsed <see cref="FlacStreamInfo"/>.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the stream marker is absent, STREAMINFO is missing, or the
    /// buffer is truncated.
    /// </exception>
    public static FlacStreamInfo Read(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        if (data.Length < 4 || !data[..4].SequenceEqual(Magic))
            throw new InvalidDataException(
                "Not a FLAC stream: missing or invalid fLaC marker.");

        int pos = 4;
        FlacStreamInfo? streamInfo = null;

        while (true)
        {
            if (pos + 4 > data.Length)
                throw new InvalidDataException(
                    "Unexpected end of data while reading a metadata block header.");

            byte headerByte = data[pos++];
            bool isLast   = (headerByte & 0x80) != 0;
            int  blockType = headerByte & 0x7F;

            int length = (data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2];
            pos += 3;

            if (pos + length > data.Length)
                throw new InvalidDataException(
                    $"Metadata block (type {blockType}, length {length}) extends beyond the data buffer.");

            if (blockType == BlockTypeStreamInfo)
                streamInfo = ParseStreamInfo(data.Slice(pos, length));
            // All other block types (PADDING, SEEKTABLE, VORBIS_COMMENT, …) are
            // intentionally skipped — the decoder only needs STREAMINFO.

            pos += length;

            if (isLast)
                break;
        }

        if (streamInfo is null)
            throw new InvalidDataException(
                "FLAC stream is missing the required STREAMINFO metadata block.");

        bytesConsumed = pos;
        return streamInfo;
    }

    // -----------------------------------------------------------------------

    private static FlacStreamInfo ParseStreamInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length != StreamInfoLengthBytes)
            throw new InvalidDataException(
                $"STREAMINFO block must be exactly {StreamInfoLengthBytes} bytes; got {data.Length}.");

        var reader = new FlacBitReader(data);

        // All fields are MSB-first per the FLAC spec.
        int  minBlockSize  = (int)reader.ReadBits(16);
        int  maxBlockSize  = (int)reader.ReadBits(16);
        int  minFrameSize  = (int)reader.ReadBits(24);
        int  maxFrameSize  = (int)reader.ReadBits(24);
        int  sampleRate    = (int)reader.ReadBits(20);
        int  channels      = (int)reader.ReadBits(3) + 1;   // stored as channels − 1
        int  bitsPerSample = (int)reader.ReadBits(5) + 1;   // stored as bitsPerSample − 1

        // TotalSamples is 36 bits — too wide for a single ReadBits call (max 32).
        long totalSamples  = ((long)reader.ReadBits(4) << 32) | (long)reader.ReadBits(32);

        byte[] md5 = reader.ReadBytes(16).ToArray();        // 128-bit MD5 signature

        return new FlacStreamInfo(
            minBlockSize, maxBlockSize,
            minFrameSize, maxFrameSize,
            sampleRate, channels, bitsPerSample,
            totalSamples, md5);
    }
}
