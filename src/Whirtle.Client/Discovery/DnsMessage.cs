// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Minimal DNS wire-format encoder/decoder covering the record types
/// used by mDNS advertisement: PTR, SRV, TXT, A, and AAAA.
/// </summary>
internal static class DnsMessage
{
    private const ushort TypePTR  = 12;
    private const ushort TypeTXT  = 16;
    private const ushort TypeSRV  = 33;
    private const ushort TypeA    = 1;
    private const ushort TypeAAAA = 28;
    private const ushort ClassIN  = 1;

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>Builds a DNS query for PTR records on <paramref name="serviceName"/>.</summary>
    public static byte[] BuildQuery(string serviceName, ushort transactionId = 0)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        WriteUInt16(w, transactionId);
        WriteUInt16(w, 0x0000); // Flags: standard query
        WriteUInt16(w, 1);      // QDCOUNT
        WriteUInt16(w, 0);      // ANCOUNT
        WriteUInt16(w, 0);      // NSCOUNT
        WriteUInt16(w, 0);      // ARCOUNT

        WriteName(w, serviceName);
        WriteUInt16(w, TypePTR);
        WriteUInt16(w, ClassIN);

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an mDNS advertisement response containing PTR, SRV, TXT, and A records
    /// for a <c>_sendspin._tcp.local.</c> service.
    /// </summary>
    public static byte[] BuildAdvertisement(
        string  instanceName,
        string  hostname,
        string  ipAddress,
        int     port,
        string  path,
        string? friendlyName)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        // Header: authoritative response, 1 answer, 3 additional records
        WriteUInt16(w, 0x0000); // ID (mDNS uses 0)
        WriteUInt16(w, 0x8400); // QR=1, AA=1
        WriteUInt16(w, 0);      // QDCOUNT
        WriteUInt16(w, 1);      // ANCOUNT  – PTR
        WriteUInt16(w, 0);      // NSCOUNT
        WriteUInt16(w, 3);      // ARCOUNT  – SRV + TXT + A

        // ── PTR answer ──────────────────────────────────────────────────────
        WriteName(w, MdnsAdvertiser.ServiceType);
        WriteUInt16(w, TypePTR); WriteUInt16(w, ClassIN); WriteUInt32(w, 4500);
        var ptrRdata = EncodeName(instanceName);
        WriteUInt16(w, (ushort)ptrRdata.Length);
        w.Write(ptrRdata);

        // ── SRV additional ──────────────────────────────────────────────────
        var hostLocal = hostname.TrimEnd('.') + ".local.";
        WriteName(w, instanceName);
        WriteUInt16(w, TypeSRV); WriteUInt16(w, ClassIN); WriteUInt32(w, 120);
        var srvTarget = EncodeName(hostLocal);
        WriteUInt16(w, (ushort)(6 + srvTarget.Length)); // priority + weight + port + target
        WriteUInt16(w, 0);               // priority
        WriteUInt16(w, 0);               // weight
        WriteUInt16(w, (ushort)port);
        w.Write(srvTarget);

        // ── TXT additional ──────────────────────────────────────────────────
        WriteName(w, instanceName);
        WriteUInt16(w, TypeTXT); WriteUInt16(w, ClassIN); WriteUInt32(w, 4500);
        var txtStrings = new List<byte[]> { Encoding.ASCII.GetBytes($"path={path}") };
        if (friendlyName is not null)
            txtStrings.Add(Encoding.ASCII.GetBytes($"name={friendlyName}"));
        int txtLen = txtStrings.Sum(s => 1 + s.Length);
        WriteUInt16(w, (ushort)txtLen);
        foreach (var s in txtStrings) { w.Write((byte)s.Length); w.Write(s); }

        // ── A additional ────────────────────────────────────────────────────
        WriteName(w, hostLocal);
        WriteUInt16(w, TypeA); WriteUInt16(w, ClassIN); WriteUInt32(w, 120);
        WriteUInt16(w, 4);
        w.Write(IPAddress.Parse(ipAddress).GetAddressBytes());

        return ms.ToArray();
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    public sealed record PtrRecord(string InstanceName);
    public sealed record SrvRecord(string Target, int Port);
    public sealed record ARecord(string Address);
    public sealed record TxtRecord(IReadOnlyDictionary<string, string> Entries);

    public sealed class ParsedResponse
    {
        /// <summary>True when the QR bit is 0 (this is a query, not a response).</summary>
        public bool         IsQuery    { get; internal set; }
        /// <summary>Names from the question section.</summary>
        public List<string> Questions  { get; } = [];
        public List<PtrRecord> PtrRecords { get; } = [];
        public List<SrvRecord> SrvRecords { get; } = [];
        public List<ARecord>   ARecords   { get; } = [];
        public List<TxtRecord> TxtRecords { get; } = [];
    }

    /// <summary>
    /// Parses a DNS datagram (queries and responses).
    /// Returns <c>null</c> if the datagram is malformed.
    /// </summary>
    public static ParsedResponse? TryParse(byte[] data)
    {
        try   { return Parse(data); }
        catch { return null; }
    }

    private static ParsedResponse Parse(byte[] data)
    {
        int pos = 0;

        /* ushort id  = */ ReadUInt16(data, ref pos);
        ushort flags   = ReadUInt16(data, ref pos);
        ushort qdCount = ReadUInt16(data, ref pos);
        ushort anCount = ReadUInt16(data, ref pos);
        ushort nsCount = ReadUInt16(data, ref pos);
        ushort arCount = ReadUInt16(data, ref pos);

        var result   = new ParsedResponse { IsQuery = (flags & 0x8000) == 0 };

        // Parse questions (record names for query detection)
        for (int i = 0; i < qdCount; i++)
        {
            result.Questions.Add(ReadName(data, ref pos));
            pos += 4; // TYPE + CLASS
        }

        // Parse resource records
        int total = anCount + nsCount + arCount;
        for (int i = 0; i < total; i++)
        {
            string name  = ReadName(data, ref pos);
            ushort type  = ReadUInt16(data, ref pos);
            /* ushort cls = */ ReadUInt16(data, ref pos);
            /* uint   ttl = */ ReadUInt32(data, ref pos);
            ushort rdLen = ReadUInt16(data, ref pos);
            int    rdEnd = pos + rdLen;

            switch (type)
            {
                case TypePTR:
                    result.PtrRecords.Add(new PtrRecord(ReadName(data, ref pos)));
                    break;

                case TypeSRV:
                    pos += 4; // priority + weight
                    int    srvPort = ReadUInt16(data, ref pos);
                    string srvTgt  = ReadName(data, ref pos);
                    result.SrvRecords.Add(new SrvRecord(srvTgt, srvPort));
                    break;

                case TypeA when rdLen == 4:
                    result.ARecords.Add(new ARecord(
                        $"{data[pos]}.{data[pos+1]}.{data[pos+2]}.{data[pos+3]}"));
                    break;

                case TypeAAAA when rdLen == 16:
                    var sb = new StringBuilder("[");
                    for (int b = 0; b < 16; b += 2)
                        sb.Append((b > 0 ? ":" : "") + $"{data[pos+b]:x2}{data[pos+b+1]:x2}");
                    sb.Append(']');
                    result.ARecords.Add(new ARecord(sb.ToString()));
                    break;

                case TypeTXT:
                    var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int txtEnd  = pos + rdLen;
                    while (pos < txtEnd)
                    {
                        byte sLen = data[pos++];
                        if (sLen == 0) continue;
                        var entry  = Encoding.ASCII.GetString(data, pos, sLen);
                        pos       += sLen;
                        int eq     = entry.IndexOf('=');
                        if (eq > 0) entries[entry[..eq]] = entry[(eq + 1)..];
                    }
                    result.TxtRecords.Add(new TxtRecord(entries));
                    break;
            }

            pos = rdEnd;
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteName(BinaryWriter w, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }
        w.Write((byte)0);
    }

    /// <summary>Encodes a DNS name to a raw byte array (for embedding in RDATA).</summary>
    private static byte[] EncodeName(string name)
    {
        using var ms = new MemoryStream();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static string ReadName(byte[] data, ref int pos)
    {
        var  parts  = new List<string>();
        bool jumped = false;
        int  saved  = 0;

        while (true)
        {
            byte len = data[pos++];
            if (len == 0) break;

            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | data[pos++];
                if (!jumped) { saved = pos; jumped = true; }
                pos = ptr;
                continue;
            }

            parts.Add(Encoding.ASCII.GetString(data, pos, len));
            pos += len;
        }

        if (jumped) pos = saved;
        return string.Join('.', parts);
    }

    private static void SkipName(byte[] data, ref int pos)
    {
        while (true)
        {
            byte len = data[pos++];
            if (len == 0) return;
            if ((len & 0xC0) == 0xC0) { pos++; return; }
            pos += len;
        }
    }

    private static ushort ReadUInt16(byte[] data, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;
        return v;
    }

    private static uint ReadUInt32(byte[] data, ref int pos)
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static void WriteUInt16(BinaryWriter w, ushort value)
    {
        w.Write((byte)(value >> 8));
        w.Write((byte)(value & 0xFF));
    }

    private static void WriteUInt32(BinaryWriter w, uint value)
    {
        w.Write((byte)(value >> 24)); w.Write((byte)(value >> 16));
        w.Write((byte)(value >>  8)); w.Write((byte)(value & 0xFF));
    }
}
