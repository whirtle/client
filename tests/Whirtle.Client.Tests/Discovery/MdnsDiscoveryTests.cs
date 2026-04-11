using System.Net;
using Whirtle.Client.Discovery;

namespace Whirtle.Client.Tests.Discovery;

public class MdnsDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_SendsQueryOnStart()
    {
        var socket   = new FakeMulticastSocket();
        var discovery = new MdnsDiscovery(socket);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Even with an immediately-cancelled token the query is sent first
        try { await foreach (var _ in discovery.DiscoverAsync(cts.Token)) { } }
        catch (OperationCanceledException) { }

        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task DiscoverAsync_YieldsEndpoint_FromValidResponse()
    {
        var socket    = new FakeMulticastSocket();
        var discovery = new MdnsDiscovery(socket);

        socket.EnqueueResponse(BuildSrvResponse("myhost.local.", 4242));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var endpoints = new List<ServiceEndpoint>();

        await foreach (var ep in discovery.DiscoverAsync(cts.Token))
        {
            endpoints.Add(ep);
            cts.Cancel(); // stop after first result
        }

        Assert.Single(endpoints);
        Assert.Equal(4242, endpoints[0].Port);
    }

    [Fact]
    public async Task DiscoverAsync_IgnoresMalformedPackets()
    {
        var socket    = new FakeMulticastSocket();
        var discovery = new MdnsDiscovery(socket);

        socket.EnqueueResponse([0xFF, 0xFE]);  // garbage
        socket.EnqueueResponse(BuildSrvResponse("myhost.local.", 9000));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        ServiceEndpoint? found = null;

        await foreach (var ep in discovery.DiscoverAsync(cts.Token))
        {
            found = ep;
            cts.Cancel();
        }

        Assert.NotNull(found);
        Assert.Equal(9000, found!.Port);
    }

    [Fact]
    public async Task DiscoverFirstAsync_ReturnsFirstEndpoint()
    {
        var socket    = new FakeMulticastSocket();
        var discovery = new MdnsDiscovery(socket);

        socket.EnqueueResponse(BuildSrvResponse("myhost.local.", 7777));

        var ep = await discovery.DiscoverFirstAsync(new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);

        Assert.NotNull(ep);
        Assert.Equal(7777, ep!.Port);
    }

    [Fact]
    public void ServiceEndpoint_ToWebSocketUri_FormatsCorrectly()
    {
        var ep  = new ServiceEndpoint("192.168.1.1", 8080);
        var uri = ep.ToWebSocketUri();
        Assert.Equal("ws://192.168.1.1:8080/", uri.ToString());
    }

    // ── Helper: build a response with one PTR + one SRV + one A record ────────

    private static byte[] BuildSrvResponse(string target, int port)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        void WriteUInt16(ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)(v & 0xFF)); }
        void WriteUInt32(uint v)   { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)(v & 0xFF)); }
        void WriteName(string n)
        {
            foreach (var label in n.TrimEnd('.').Split('.'))
            {
                var b = System.Text.Encoding.ASCII.GetBytes(label);
                w.Write((byte)b.Length); w.Write(b);
            }
            w.Write((byte)0);
        }

        var instanceName = $"Whirtle Server.{MdnsDiscovery.ServiceType}";

        WriteUInt16(0x0000); // ID
        WriteUInt16(0x8400); // response | authoritative
        WriteUInt16(0);      // QDCOUNT
        WriteUInt16(1);      // ANCOUNT  (PTR)
        WriteUInt16(0);      // NSCOUNT
        WriteUInt16(2);      // ARCOUNT  (SRV + A)

        // ── PTR answer ───────────────────────────────────────────────────
        WriteName(MdnsDiscovery.ServiceType);
        WriteUInt16(12); WriteUInt16(1); WriteUInt32(120);
        using (var rd = new MemoryStream())
        {
            foreach (var label in instanceName.TrimEnd('.').Split('.'))
            { var b = System.Text.Encoding.ASCII.GetBytes(label); rd.WriteByte((byte)b.Length); rd.Write(b); }
            rd.WriteByte(0);
            var rdBytes = rd.ToArray();
            WriteUInt16((ushort)rdBytes.Length); w.Write(rdBytes);
        }

        // ── SRV additional ───────────────────────────────────────────────
        WriteName(instanceName);
        WriteUInt16(33); WriteUInt16(1); WriteUInt32(120);
        using (var rd = new MemoryStream())
        {
            // priority=0, weight=0, port
            rd.WriteByte(0); rd.WriteByte(0);
            rd.WriteByte(0); rd.WriteByte(0);
            rd.WriteByte((byte)(port >> 8)); rd.WriteByte((byte)(port & 0xFF));
            foreach (var label in target.TrimEnd('.').Split('.'))
            { var b = System.Text.Encoding.ASCII.GetBytes(label); rd.WriteByte((byte)b.Length); rd.Write(b); }
            rd.WriteByte(0);
            var rdBytes = rd.ToArray();
            WriteUInt16((ushort)rdBytes.Length); w.Write(rdBytes);
        }

        // ── A additional ─────────────────────────────────────────────────
        WriteName(target);
        WriteUInt16(1); WriteUInt16(1); WriteUInt32(120);
        WriteUInt16(4);
        w.Write(new byte[] { 192, 168, 1, 42 });

        return ms.ToArray();
    }
}
