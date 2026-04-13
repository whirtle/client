using System.Net.WebSockets;
using Whirtle.Client.Transport;

namespace Whirtle.Client.Tests.Transport;

public class WebSocketTransportTests
{
    [Fact]
    public void IsConnected_Initially_False()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_SetsIsConnected_True()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());

        await transport.ConnectAsync(new Uri("ws://localhost"));

        Assert.True(transport.IsConnected);
    }

    [Fact]
    public async Task SendAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendAsync(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public async Task SendAsync_WhenConnected_DoesNotThrow()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());
        await transport.ConnectAsync(new Uri("ws://localhost"));

        await transport.SendAsync(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task ReceiveAsync_YieldsEnqueuedMessages()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        var expected = new byte[] { 10, 20, 30 };
        fake.EnqueueMessage(expected);
        fake.EnqueueClose();

        var received = new List<byte[]>();
        await foreach (var msg in transport.ReceiveAsync())
            received.Add(msg);

        Assert.Single(received);
        Assert.Equal(expected, received[0]);
    }

    [Fact]
    public async Task ReceiveAsync_StopsOnCloseMessage()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        fake.EnqueueClose();

        var count = 0;
        await foreach (var _ in transport.ReceiveAsync())
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ReceiveAsync_StopsOnCancellation()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync(cts.Token)) { }
        });
    }

    [Fact]
    public async Task ReceiveAsync_YieldsMultipleMessages()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        fake.EnqueueMessage([1]);
        fake.EnqueueMessage([2]);
        fake.EnqueueMessage([3]);
        fake.EnqueueClose();

        var received = new List<byte[]>();
        await foreach (var msg in transport.ReceiveAsync())
            received.Add(msg);

        Assert.Equal(3, received.Count);
        Assert.Equal([1], received[0]);
        Assert.Equal([2], received[1]);
        Assert.Equal([3], received[2]);
    }

    [Fact]
    public async Task DisconnectAsync_WhenConnected_SetsIsConnected_False()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());
        await transport.ConnectAsync(new Uri("ws://localhost"));

        await transport.DisconnectAsync();

        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_DoesNotThrow()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());

        await transport.DisconnectAsync();
    }

    [Fact]
    public void Constructor_ZeroBufferSize_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebSocketTransport(new FakeClientWebSocket(), receiveBufferSize: 0));
    }

    [Fact]
    public async Task DisposeAsync_WhenConnected_DisconnectsCleanly()
    {
        var transport = new WebSocketTransport(new FakeClientWebSocket());
        await transport.ConnectAsync(new Uri("ws://localhost"));

        await transport.DisposeAsync();

        Assert.False(transport.IsConnected);
    }

    // ── ConnectAsync timeout ─────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_ThrowsTimeoutException_WhenServerDoesNotRespond()
    {
        var fake = new FakeClientWebSocket();
        fake.BlockConnect();
        var transport = new WebSocketTransport(fake, connectTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.ConnectAsync(new Uri("ws://localhost")));
    }

    [Fact]
    public async Task ConnectAsync_PropagatesOperationCanceled_WhenCallerCancels()
    {
        var fake = new FakeClientWebSocket();
        fake.BlockConnect();
        var transport = new WebSocketTransport(fake, connectTimeout: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ConnectAsync(new Uri("ws://localhost"), cts.Token));
    }

    // ── Connection-loss / error propagation ──────────────────────────────────

    [Fact]
    public async Task ReceiveAsync_PropagatesSocketException()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        fake.EnqueueException(new WebSocketException("connection reset by peer"));

        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync()) { }
        });
    }

    [Fact]
    public async Task ReceiveAsync_AfterSuccessfulMessages_PropagatesSocketException()
    {
        var fake = new FakeClientWebSocket();
        var transport = new WebSocketTransport(fake);
        await transport.ConnectAsync(new Uri("ws://localhost"));

        fake.EnqueueMessage([1]);
        fake.EnqueueMessage([2]);
        fake.EnqueueException(new WebSocketException("mid-stream failure"));

        var received = new List<byte[]>();
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await foreach (var msg in transport.ReceiveAsync())
                received.Add(msg);
        });

        Assert.Equal(2, received.Count); // received good messages before the error
    }
}
