// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Clock;

/// <summary>
/// Performs NTP-style clock synchronisation over a <see cref="ProtocolClient"/>.
///
/// Round-trip algorithm
/// ────────────────────
///  t0  client sends   client/time(client_transmitted = t0)  [Unix μs]
///  t1  server records server_received = t1, replies server/time(t0, t1)
///  t2  client records current time = t2
///
///  RTT         = t2 − t0             [μs → converted to TimeSpan]
///  ClockOffset = t1 − t0 − RTT/2    [μs → converted to TimeSpan]
/// </summary>
public sealed class ClockSynchronizer
{
    private static readonly TimeSpan DefaultSyncTimeout = TimeSpan.FromSeconds(10);

    private readonly ProtocolClient _client;
    private readonly ISystemClock   _clock;

    public ClockSynchronizer(ProtocolClient client)
        : this(client, SystemClock.Instance) { }

    internal ClockSynchronizer(ProtocolClient client, ISystemClock clock)
    {
        _client = client;
        _clock  = clock;
    }

    /// <summary>
    /// Executes one sync round trip and returns the measured
    /// <see cref="ClockSyncResult"/>.
    /// </summary>
    public async Task<ClockSyncResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(DefaultSyncTimeout);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(
                                 cancellationToken, deadline.Token);
        var token = linked.Token;

        var t0 = _clock.UtcNowMicroseconds;
        await _client.SendAsync(new ClientTimeMessage(t0), token);

        await foreach (var msg in _client.ReceiveAsync(token))
        {
            if (msg is not ServerTimeMessage reply)
                continue;

            var t2 = _clock.UtcNowMicroseconds;
            return Compute(reply.ClientTransmitted, reply.ServerReceived, t2);
        }

        throw new InvalidOperationException(
            "Connection closed before a server/time reply was received.");
    }

    private static ClockSyncResult Compute(long t0, long t1, long t2)
    {
        var rtt    = TimeSpan.FromMicroseconds(t2 - t0);
        var offset = TimeSpan.FromMicroseconds(t1 - t0 - (t2 - t0) / 2);
        return new ClockSyncResult(offset, rtt);
    }
}
