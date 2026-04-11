using System.Buffers.Binary;
using System.Text;

namespace Whirtle.Client.Discovery;

/// <summary>
/// Minimal DNS wire-format encoder/decoder covering only the record
/// types used by mDNS discovery: PTR, SRV, A, and AAAA.
/// </summary>
internal static class DnsMessage
{
    private const ushort TypePTR  = 12;
    private const ushort TypeSRV  = 33;
    private const ushort TypeA    = 1;
    private const ushort TypeAAAA = 28;
    private const ushort ClassIN  = 1;
    // mDNS cache-flush bit OR'd into the class field on responses
    private const ushort ClassCacheFlush = 0x8000;

    // ── Encoding ─────────────────────────────────────────────────────────────

    /// <summary>Builds a DNS query for PTR records on <paramref name="serviceName"/>.</summary>
    public static byte[] BuildQuery(string serviceName, ushort transactionId = 0)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        // Header
        WriteUInt16(w, transactionId); // ID
        WriteUInt16(w, 0x0000);        // Flags: standard query
        WriteUInt16(w, 1);             // QDCOUNT
        WriteUInt16(w, 0);             // ANCOUNT
        WriteUInt16(w, 0);             // NSCOUNT
        WriteUInt16(w, 0);             // ARCOUNT

        // Question
        WriteName(w, serviceName);
        WriteUInt16(w, TypePTR);
        WriteUInt16(w, ClassIN);

        return ms.ToArray();
    }

    // ── Decoding ─────────────────────────────────────────────────────────────

    public sealed record PtrRecord(string InstanceName);
    public sealed record SrvRecord(string Target, int Port);
    public sealed record ARecord(string Address);

    public sealed class ParsedResponse
    {
        public List<PtrRecord> PtrRecords  { get; } = [];
        public List<SrvRecord> SrvRecords  { get; } = [];
        public List<ARecord>   ARecords    { get; } = [];
    }

    /// <summary>
    /// Parses the answer, authority and additional sections of a DNS response.
    /// Returns <c>null</c> if the datagram is malformed.
    /// </summary>
    public static ParsedResponse? TryParse(byte[] data)
    {
        try
        {
            return Parse(data);
        }
        catch
        {
            return null;
        }
    }

    private static ParsedResponse Parse(byte[] data)
    {
        int pos = 0;

        ushort id      = ReadUInt16(data, ref pos);
        ushort flags   = ReadUInt16(data, ref pos);
        ushort qdCount = ReadUInt16(data, ref pos);
        ushort anCount = ReadUInt16(data, ref pos);
        ushort nsCount = ReadUInt16(data, ref pos);
        ushort arCount = ReadUInt16(data, ref pos);

        // Skip questions
        for (int i = 0; i < qdCount; i++)
        {
            SkipName(data, ref pos);
            pos += 4; // TYPE + CLASS
        }

        var result = new ParsedResponse();
        int total  = anCount + nsCount + arCount;

        for (int i = 0; i < total; i++)
        {
            string name  = ReadName(data, ref pos);
            ushort type  = ReadUInt16(data, ref pos);
            ushort cls   = ReadUInt16(data, ref pos);
            uint   ttl   = ReadUInt32(data, ref pos);
            ushort rdLen = ReadUInt16(data, ref pos);
            int    rdEnd = pos + rdLen;

            switch (type)
            {
                case TypePTR:
                    result.PtrRecords.Add(new PtrRecord(ReadName(data, ref pos)));
                    break;

                case TypeSRV:
                    pos += 4; // priority + weight
                    int port   = ReadUInt16(data, ref pos);
                    string tgt = ReadName(data, ref pos);
                    result.SrvRecords.Add(new SrvRecord(tgt, port));
                    break;

                case TypeA when rdLen == 4:
                    result.ARecords.Add(new ARecord(
                        $"{data[pos]}.{data[pos+1]}.{data[pos+2]}.{data[pos+3]}"));
                    break;

                case TypeAAAA when rdLen == 16:
                    // Format as bracketed IPv6 address
                    var sb = new StringBuilder("[");
                    for (int b = 0; b < 16; b += 2)
                        sb.Append((b > 0 ? ":" : "") + $"{data[pos+b]:x2}{data[pos+b+1]:x2}");
                    sb.Append(']');
                    result.ARecords.Add(new ARecord(sb.ToString()));
                    break;
            }

            pos = rdEnd;
        }

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteName(BinaryWriter w, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            w.Write((byte)bytes.Length);
            w.Write(bytes);
        }
        w.Write((byte)0);
    }

    private static string ReadName(byte[] data, ref int pos)
    {
        var parts   = new List<string>();
        bool jumped = false;
        int  saved  = 0;

        while (true)
        {
            byte len = data[pos++];

            if (len == 0)
                break;

            if ((len & 0xC0) == 0xC0)
            {
                // Pointer
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
}
