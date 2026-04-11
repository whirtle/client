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
    public void TryParse_MalformedData_ReturnsNull()
    {
        Assert.Null(DnsMessage.TryParse([0xFF, 0xFE]));
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
