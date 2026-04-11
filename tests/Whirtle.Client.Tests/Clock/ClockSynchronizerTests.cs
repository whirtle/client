using Whirtle.Client.Clock;
using Whirtle.Client.Protocol;
using Whirtle.Client.Tests.Protocol;

namespace Whirtle.Client.Tests.Clock;

public class ClockSynchronizerTests
{
    private static readonly MessageSerializer Serializer = new();

    private static (ClockSynchronizer syncer, FakeClock clock, FakeTransport transport) Build()
    {
        var transport = new FakeTransport();
        var client    = new ProtocolClient(transport);
        var clock     = new FakeClock();
        return (new ClockSynchronizer(client, clock), clock, transport);
    }

    [Fact]
    public async Task SyncOnceAsync_ReturnsCorrectRtt()
    {
        // t0=0, t2=200 → RTT = 200 ticks
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new SyncReplyMessage(
            ClientSentAt:     0,
            ServerReceivedAt: 100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromTicks(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ZeroOffset_WhenClocksAgree()
    {
        // Symmetric RTT: t0=0, t1=100, t2=200
        // offset = t1 - t0 - RTT/2 = 100 - 0 - 100 = 0
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new SyncReplyMessage(0, 100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.Zero, result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_PositiveOffset_WhenServerAhead()
    {
        // t0=0, t1=600, t2=200 → RTT=200, offset = 600 - 0 - 100 = +500
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new SyncReplyMessage(0, 600)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromTicks(500), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_NegativeOffset_WhenClientAhead()
    {
        // t0=0, t1=50, t2=200 → RTT=200, offset = 50 - 0 - 100 = -50
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new SyncReplyMessage(0, 50)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromTicks(-50), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_SkipsNonSyncReplyMessages()
    {
        // A leading Ping should be ignored; the SyncReply is still found.
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new PingMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new SyncReplyMessage(0, 100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromTicks(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ConnectionClosedBeforeReply_Throws()
    {
        var (syncer, _, transport) = Build();
        transport.EnqueueInbound(Serializer.Serialize(new GoodbyeMessage("gone")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncer.SyncOnceAsync());
    }
}
