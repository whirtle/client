// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Clock;
using Whirtle.Client.Protocol;
using Whirtle.Client.Transport;

namespace Whirtle.Client.IntegrationTests;

/// <summary>
/// End-to-end tests against a live <c>uvx sendspin serve --demo</c> instance.
///
/// All tests pass trivially when the server is unavailable, so the suite
/// stays green in CI environments where uvx is not installed.
/// Run manually with <c>uvx sendspin serve --demo</c> in another terminal
/// to exercise the live protocol path.
/// </summary>
[Collection(SensspinCollection.Name)]
public sealed class SensspinIntegrationTests
{
    private static readonly Uri ServerUri =
        new($"ws://127.0.0.1:{SensspinServerFixture.Port}{SensspinServerFixture.Path}");

    private readonly SensspinServerFixture _server;

    public SensspinIntegrationTests(SensspinServerFixture server)
        => _server = server;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<ProtocolClient> ConnectAsync(CancellationToken ct = default)
    {
        var transport = new WebSocketTransport();
        var protocol  = new ProtocolClient(transport);
        await protocol.ConnectAsync(ServerUri, ct);
        return protocol;
    }

    // ── Handshake ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handshake_ReceivesServerHello()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        var hello = await protocol.HandshakeAsync(
            "test-client", "Integration Test", cancellationToken: cts.Token);

        Assert.NotEmpty(hello.ServerId);
        Assert.NotNull(hello.ConnectionReason);
        Assert.NotNull(hello.ActiveRoles);
    }

    [Fact]
    public async Task Handshake_ConnectionReasonIsKnownValue()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        var hello = await protocol.HandshakeAsync(
            "test-client-2", "Integration Test 2", cancellationToken: cts.Token);

        Assert.Contains(hello.ConnectionReason,
            new[] { "discovery", "playback", "manual" });
    }

    // ── Clock sync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClockSync_ReturnsReasonableOffset()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        await protocol.HandshakeAsync(
            "test-clock", "Clock Sync Test", cancellationToken: cts.Token);

        var syncer = new ClockSynchronizer(protocol);
        var result = await syncer.SyncOnceAsync(cts.Token);

        // RTT to localhost should be well under 1 second.
        Assert.True(result.RoundTripTime < TimeSpan.FromSeconds(1),
            $"RTT {result.RoundTripTime.TotalMilliseconds:0} ms is unexpectedly large");

        // Offset to a local server should be within ±5 seconds.
        Assert.True(Math.Abs(result.ClockOffset.TotalSeconds) < 5,
            $"Offset {result.ClockOffset.TotalMilliseconds:+0;-0} ms is unexpectedly large");
    }

    // ── Receive messages ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReceiveAsync_YieldsAtLeastOneMessage()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        await protocol.HandshakeAsync(
            "test-recv", "Receive Test", cancellationToken: cts.Token);

        // The demo server emits server/state and group/update immediately.
        Message? first = null;
        await foreach (var msg in protocol.ReceiveAsync(cts.Token))
        {
            first = msg;
            break;
        }

        Assert.NotNull(first);
    }

    [Fact]
    public async Task ReceiveAsync_ServerStateContainsMetadata()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        await protocol.HandshakeAsync(
            "test-meta", "Metadata Test", cancellationToken: cts.Token);

        ServerStateMessage? state = null;
        await foreach (var msg in protocol.ReceiveAsync(cts.Token))
        {
            if (msg is ServerStateMessage s)
            {
                state = s;
                break;
            }
        }

        Assert.NotNull(state);
        // Demo server should supply at least some metadata.
        Assert.NotNull(state.Metadata);
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisconnectAsync_SendsGoodbyeWithoutError()
    {
        if (_server.Unavailable) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var protocol = await ConnectAsync(cts.Token);

        await protocol.HandshakeAsync(
            "test-bye", "Goodbye Test", cancellationToken: cts.Token);

        // Should not throw.
        await protocol.DisconnectAsync("test_complete", cts.Token);
    }
}
