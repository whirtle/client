using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

public class DnsMessageTests
{
    [Fact]
    public void BuildQuery_HasCorrectQuestionCount()
    {
        var query = DnsMessage.BuildQuery("_whirtle._tcp.local.");
        // QDCOUNT is at bytes 4–5 (big-endian)
        Assert.Equal(0, query[4]);
        Assert.Equal(1, query[5]);
    }

    [Fact]
    public void BuildQuery_IsStandardQuery()
    {
        var query = DnsMessage.BuildQuery("_whirtle._tcp.local.");
        // Flags at bytes 2–3; standard query = 0x0000
        Assert.Equal(0, query[2]);
        Assert.Equal(0, query[3]);
    }

    [Fact]
    public void TryParse_RoundTrip_PtrRecord()
    {
        // Build a minimal PTR response for "_whirtle._tcp.local." → "myhost._whirtle._tcp.local."
        var payload = BuildPtrResponse("_whirtle._tcp.local.", "myhost._whirtle._tcp.local.");
        var result  = DnsMessage.TryParse(payload);

        Assert.NotNull(result);
        Assert.Single(result!.PtrRecords);
        Assert.Equal("myhost._whirtle._tcp.local.", result.PtrRecords[0].InstanceName);
    }

    [Fact]
    public void TryParse_MalformedData_ReturnsNull()
    {
        var result = DnsMessage.TryParse([0xFF, 0xFE]);
        Assert.Null(result);
    }

    [Fact]
    public void BuildQuery_UsesProvidedTransactionId()
    {
        var query = DnsMessage.BuildQuery("_whirtle._tcp.local.", transactionId: 0x1234);
        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
    }

    // ── Helper: hand-craft a minimal DNS response with one PTR answer ─────────

    private static byte[] BuildPtrResponse(string questionName, string ptrTarget)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        void WriteUInt16(ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)(v & 0xFF)); }
        void WriteName(string n)
        {
            foreach (var label in n.TrimEnd('.').Split('.'))
            {
                var b = System.Text.Encoding.ASCII.GetBytes(label);
                w.Write((byte)b.Length);
                w.Write(b);
            }
            w.Write((byte)0);
        }

        WriteUInt16(0x0000); // ID
        WriteUInt16(0x8400); // Flags: response, authoritative
        WriteUInt16(0);      // QDCOUNT
        WriteUInt16(1);      // ANCOUNT
        WriteUInt16(0);      // NSCOUNT
        WriteUInt16(0);      // ARCOUNT

        // PTR answer
        WriteName(questionName);
        WriteUInt16(12);     // TYPE PTR
        WriteUInt16(1);      // CLASS IN

        // TTL
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); w.Write((byte)120);

        // RDATA: encode target name into a temporary buffer to get length
        using var rdBuf = new MemoryStream();
        using var rdW   = new BinaryWriter(rdBuf);
        foreach (var label in ptrTarget.TrimEnd('.').Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(label);
            rdW.Write((byte)b.Length); rdW.Write(b);
        }
        rdW.Write((byte)0);
        var rd = rdBuf.ToArray();

        WriteUInt16((ushort)rd.Length);
        w.Write(rd);

        return ms.ToArray();
    }
}
