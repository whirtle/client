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
        // t0 = 0 μs, t2 = 200 μs → RTT = 200 μs
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new ServerTimeMessage(
            ClientTransmitted: 0,
            ServerReceived:    100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromMicroseconds(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ZeroOffset_WhenClocksAgree()
    {
        // Symmetric RTT: t0=0, t1=100, t2=200 μs
        // offset = t1 - t0 - RTT/2 = 100 - 0 - 100 = 0
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new ServerTimeMessage(0, 100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.Zero, result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_PositiveOffset_WhenServerAhead()
    {
        // t0=0, t1=600, t2=200 μs → RTT=200, offset = 600 - 0 - 100 = +500 μs
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new ServerTimeMessage(0, 600)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromMicroseconds(500), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_NegativeOffset_WhenClientAhead()
    {
        // t0=0, t1=50, t2=200 μs → RTT=200, offset = 50 - 0 - 100 = -50 μs
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new ServerTimeMessage(0, 50)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromMicroseconds(-50), result.ClockOffset);
    }

    [Fact]
    public async Task SyncOnceAsync_SkipsNonSyncReplyMessages()
    {
        // A leading server/state should be ignored; the server/time is still found.
        var (syncer, clock, transport) = Build();
        clock.Set(0);
        transport.EnqueueInbound(Serializer.Serialize(new ServerStateMessage()));
        transport.EnqueueInbound(Serializer.Serialize(new ServerTimeMessage(0, 100)));
        clock.Set(200);

        var result = await syncer.SyncOnceAsync();

        Assert.Equal(TimeSpan.FromMicroseconds(200), result.RoundTripTime);
    }

    [Fact]
    public async Task SyncOnceAsync_ConnectionClosedBeforeReply_Throws()
    {
        var (syncer, _, transport) = Build();
        transport.CloseInbound();

        await Assert.ThrowsAsync<InvalidOperationException>(() => syncer.SyncOnceAsync());
    }
}
