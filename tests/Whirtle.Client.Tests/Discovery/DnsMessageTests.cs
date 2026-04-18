using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

public class DnsMessageTests
{
    // ── Query building ────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_HasCorrectQuestionCount()
    {
        var query = DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType);
        Assert.Equal(0, query[4]);
        Assert.Equal(1, query[5]); // QDCOUNT = 1
    }

    [Fact]
    public void BuildQuery_IsStandardQuery()
    {
        var query = DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType);
        Assert.Equal(0, query[2]);
        Assert.Equal(0, query[3]); // Flags = 0x0000
    }

    [Fact]
    public void BuildQuery_UsesProvidedTransactionId()
    {
        var query = DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType, transactionId: 0x1234);
        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);
    }

    // ── Query parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_Query_IsQueryTrue()
    {
        var query  = DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType);
        var parsed = DnsMessage.TryParse(query);

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsQuery);
    }

    [Fact]
    public void TryParse_Query_RecordsQuestionName()
    {
        var query  = DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType);
        var parsed = DnsMessage.TryParse(query);

        Assert.NotNull(parsed);
        Assert.Single(parsed!.Questions);
        Assert.Equal("_sendspin._tcp.local", parsed.Questions[0]);
    }

    // ── Advertisement building + round-trip ───────────────────────────────────

    [Fact]
    public void BuildAdvertisement_IsResponse()
    {
        var data   = BuildSample();
        var parsed = DnsMessage.TryParse(data);

        Assert.NotNull(parsed);
        Assert.False(parsed!.IsQuery);
    }

    [Fact]
    public void BuildAdvertisement_ContainsPtrRecord()
    {
        var parsed = DnsMessage.TryParse(BuildSample())!;
        Assert.Single(parsed.PtrRecords);
    }

    [Fact]
    public void BuildAdvertisement_ContainsSrvWithCorrectPort()
    {
        var parsed = DnsMessage.TryParse(BuildSample())!;
        Assert.Single(parsed.SrvRecords);
        Assert.Equal(8928, parsed.SrvRecords[0].Port);
    }

    [Fact]
    public void BuildAdvertisement_ContainsTxtWithPathKey()
    {
        var parsed = DnsMessage.TryParse(BuildSample())!;
        Assert.Single(parsed.TxtRecords);
        Assert.True(parsed.TxtRecords[0].Entries.ContainsKey("path"));
        Assert.Equal("/sendspin", parsed.TxtRecords[0].Entries["path"]);
    }

    [Fact]
    public void BuildAdvertisement_ContainsTxtWithNameKey_WhenProvided()
    {
        var data   = DnsMessage.BuildAdvertisement(
            "Player._sendspin._tcp.local.", "myhost", "192.168.1.1",
            8928, "/sendspin", friendlyName: "Living Room");
        var parsed = DnsMessage.TryParse(data)!;

        Assert.True(parsed.TxtRecords[0].Entries.ContainsKey("name"));
        Assert.Equal("Living Room", parsed.TxtRecords[0].Entries["name"]);
    }

    [Fact]
    public void BuildAdvertisement_OmitsTxtNameKey_WhenNull()
    {
        var parsed = DnsMessage.TryParse(BuildSample())!;
        Assert.False(parsed.TxtRecords[0].Entries.ContainsKey("name"));
    }

    [Fact]
    public void BuildAdvertisement_ContainsARecord()
    {
        var parsed = DnsMessage.TryParse(BuildSample())!;
        Assert.Single(parsed.ARecords);
        Assert.Equal("192.168.1.1", parsed.ARecords[0].Address);
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_EmptyBuffer_ReturnsNull()
    {
        Assert.Null(DnsMessage.TryParse([]));
    }

    [Fact]
    public void TryParse_TooShort_ReturnsNull()
    {
        Assert.Null(DnsMessage.TryParse([0xFF, 0xFE]));
    }

    [Fact]
    public void TryParse_OversizedPacket_ReturnsNull()
    {
        Assert.Null(DnsMessage.TryParse(new byte[4097]));
    }

    [Fact]
    public void TryParse_ExcessiveRecordCount_ReturnsNull()
    {
        // Claim 51 answers in ANCOUNT but provide no actual records.
        var data = new byte[12];
        data[6] = 0x00; data[7] = 51; // ANCOUNT = 51, exceeds MaxRecordsPerSection
        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_CircularPointer_ReturnsNull()
    {
        // Build a minimal DNS response header followed by a PTR answer whose
        // RDATA contains a compression pointer that points back to itself.
        var data = new byte[]
        {
            0x00, 0x00, // ID
            0x84, 0x00, // Flags: QR=1, AA=1
            0x00, 0x00, // QDCOUNT
            0x00, 0x01, // ANCOUNT = 1
            0x00, 0x00, // NSCOUNT
            0x00, 0x00, // ARCOUNT
            // Answer name: single label "x" + null terminator
            0x01, 0x78, 0x00,
            0x00, 0x0C,         // TYPE = PTR
            0x00, 0x01,         // CLASS = IN
            0x00, 0x00, 0x11, 0x94, // TTL
            0x00, 0x02,         // RDLENGTH = 2
            // RDATA: compression pointer that points back to itself (offset 22)
            0xC0, 0x16,
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_TruncatedPointer_ReturnsNull()
    {
        // Compression pointer byte with no following byte
        var data = new byte[]
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x78, 0x00,
            0x00, 0x0C, 0x00, 0x01,
            0x00, 0x00, 0x11, 0x94,
            0x00, 0x01,
            0xC0, // pointer first byte with no second byte
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_OutOfBoundsPointer_ReturnsNull()
    {
        // Compression pointer that points past the end of the buffer.
        var data = new byte[]
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x78, 0x00,
            0x00, 0x0C, 0x00, 0x01,
            0x00, 0x00, 0x11, 0x94,
            0x00, 0x02,
            0xC0, 0xFF, // pointer to offset 255, well past buffer end
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_ReservedLabelType_ReturnsNull()
    {
        // Label byte 0x41 has top 2 bits = 01, which is a reserved EDNS label type.
        var data = new byte[]
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x41, 0x78, 0x00, // reserved label type in answer name
            0x00, 0x0C, 0x00, 0x01,
            0x00, 0x00, 0x11, 0x94,
            0x00, 0x00,
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_RdataExceedsBuffer_ReturnsNull()
    {
        // RDLENGTH claims more bytes than remain in the packet.
        var data = new byte[]
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x78, 0x00,         // name "x"
            0x00, 0x0C,               // TYPE PTR
            0x00, 0x01,               // CLASS IN
            0x00, 0x00, 0x11, 0x94,   // TTL
            0x00, 0x64,               // RDLENGTH = 100, but no bytes follow
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    [Fact]
    public void TryParse_TxtStringExceedsRdata_ReturnsNull()
    {
        // TXT string length byte claims more bytes than the RDATA boundary allows.
        var data = new byte[]
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x78, 0x00,         // name "x"
            0x00, 0x10,               // TYPE TXT
            0x00, 0x01,               // CLASS IN
            0x00, 0x00, 0x11, 0x94,   // TTL
            0x00, 0x02,               // RDLENGTH = 2
            0x05, 0x61,               // sLen=5 but only 1 byte of data follows — exceeds rdEnd
        };

        Assert.Null(DnsMessage.TryParse(data));
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static byte[] BuildSample() =>
        DnsMessage.BuildAdvertisement(
            instanceName: "myhost._sendspin._tcp.local.",
            hostname:     "myhost",
            ipAddress:    "192.168.1.1",
            port:         MdnsAdvertiser.DefaultPort,
            path:         MdnsAdvertiser.DefaultPath,
            friendlyName: null);
}
