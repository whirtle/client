using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

public class MdnsAdvertiserTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    [Fact]
    public void ServiceType_MatchesSpec()
        => Assert.Equal("_sendspin._tcp.local.", MdnsAdvertiser.ServiceType);

    [Fact]
    public void DefaultPort_MatchesSpec()
        => Assert.Equal(8928, MdnsAdvertiser.DefaultPort);

    [Fact]
    public void DefaultPath_MatchesSpec()
        => Assert.Equal("/sendspin", MdnsAdvertiser.DefaultPath);

    [Fact]
    public void TxtKeyPath_IsPath()
        => Assert.Equal("path", MdnsAdvertiser.TxtKeyPath);

    [Fact]
    public void TxtKeyName_IsName()
        => Assert.Equal("name", MdnsAdvertiser.TxtKeyName);

    // ── Behaviour ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdvertiseAsync_RespondsToServiceTypeQuery()
    {
        var socket     = new FakeMulticastSocket();
        var advertiser = new MdnsAdvertiser(socket, "myhost", port: 8928);

        // Enqueue a PTR query for _sendspin._tcp.local.
        socket.EnqueueResponse(DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType));

        using var cts = new CancellationTokenSource();
        _ = advertiser.AdvertiseAsync(cts.Token);

        // Give the loop one iteration to process and respond
        await Task.Delay(50);
        cts.Cancel();

        Assert.Single(socket.Sent); // one response sent
    }

    [Fact]
    public async Task AdvertiseAsync_IgnoresQueryForOtherServiceType()
    {
        var socket     = new FakeMulticastSocket();
        var advertiser = new MdnsAdvertiser(socket, "myhost");

        socket.EnqueueResponse(DnsMessage.BuildQuery("_other._tcp.local."));

        using var cts = new CancellationTokenSource();
        _ = advertiser.AdvertiseAsync(cts.Token);

        await Task.Delay(50);
        cts.Cancel();

        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task AdvertiseAsync_IgnoresMdnsResponses()
    {
        var socket     = new FakeMulticastSocket();
        var advertiser = new MdnsAdvertiser(socket, "myhost");

        // A response packet (QR=1) should be ignored
        var response = DnsMessage.BuildAdvertisement(
            "other._sendspin._tcp.local.", "other", "10.0.0.1",
            8928, "/sendspin", null);
        socket.EnqueueResponse(response);

        using var cts = new CancellationTokenSource();
        _ = advertiser.AdvertiseAsync(cts.Token);

        await Task.Delay(50);
        cts.Cancel();

        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task AdvertiseAsync_ResponseContainsPathTxtKey()
    {
        var socket     = new FakeMulticastSocket();
        var advertiser = new MdnsAdvertiser(socket, "myhost", path: "/sendspin");

        socket.EnqueueResponse(DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType));

        using var cts = new CancellationTokenSource();
        _ = advertiser.AdvertiseAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var response = DnsMessage.TryParse(socket.Sent[0].Datagram)!;
        Assert.Equal("/sendspin", response.TxtRecords[0].Entries["path"]);
    }

    [Fact]
    public async Task AdvertiseAsync_ResponseContainsNameTxtKey_WhenProvided()
    {
        var socket     = new FakeMulticastSocket();
        var advertiser = new MdnsAdvertiser(socket, "myhost", friendlyName: "Kitchen");

        socket.EnqueueResponse(DnsMessage.BuildQuery(MdnsAdvertiser.ServiceType));

        using var cts = new CancellationTokenSource();
        _ = advertiser.AdvertiseAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var response = DnsMessage.TryParse(socket.Sent[0].Datagram)!;
        Assert.Equal("Kitchen", response.TxtRecords[0].Entries["name"]);
    }

    // ── ServiceEndpoint ───────────────────────────────────────────────────────

    [Fact]
    public void ServiceEndpoint_ToWebSocketUri_UsesPathFromTxtRecord()
    {
        var ep = new ServiceEndpoint("192.168.1.1", 8928, "/sendspin");
        Assert.Equal("ws://192.168.1.1:8928/sendspin", ep.ToWebSocketUri().ToString());
    }

    [Fact]
    public void ServiceEndpoint_Defaults_MatchSpec()
    {
        var ep = new ServiceEndpoint("10.0.0.1");
        Assert.Equal(MdnsAdvertiser.DefaultPort, ep.Port);
        Assert.Equal(MdnsAdvertiser.DefaultPath, ep.Path);
        Assert.Null(ep.Name);
    }
}
