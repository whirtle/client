// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Protocol;

namespace Whirtle.Client.Clock;

/// <summary>
/// Performs NTP-style clock synchronisation over a <see cref="ProtocolClient"/>.
///
/// Round-trip algorithm
/// ────────────────────
///  t0  client sends   SyncRequest(clientSentAt = t0)
///  t1  server records serverReceivedAt = t1, replies SyncReply(t0, t1)
///  t2  client records clientReceivedAt = t2
///
///  RTT         = t2 − t0
///  ClockOffset = t1 − t0 − RTT/2
///              = (t1 − t0 − t2 + t0) / 2         (simplified NTP formula)
///              = (t1 − t2) / 2                    (one-way assumption: RTT symmetric)
/// </summary>
public sealed class ClockSynchronizer
{
    private readonly ProtocolClient _client;
    private readonly ISystemClock _clock;

    public ClockSynchronizer(ProtocolClient client)
        : this(client, SystemClock.Instance) { }

    internal ClockSynchronizer(ProtocolClient client, ISystemClock clock)
    {
        _client = client;
        _clock = clock;
    }

    /// <summary>
    /// Executes one sync round trip and returns the measured
    /// <see cref="ClockSyncResult"/>.
    /// </summary>
    /// <remarks>
    /// Reads the next message from the shared receive stream, so the caller
    /// must not be concurrently consuming <see cref="ProtocolClient.ReceiveAsync"/>.
    /// </remarks>
    public async Task<ClockSyncResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var t0 = _clock.UtcNowTicks;

        await _client.SendAsync(new SyncRequestMessage(t0), cancellationToken);

        await foreach (var msg in _client.ReceiveAsync(cancellationToken))
        {
            if (msg is not SyncReplyMessage reply)
                continue;

            var t2 = _clock.UtcNowTicks;
            return Compute(reply.ClientSentAt, reply.ServerReceivedAt, t2);
        }

        throw new InvalidOperationException(
            "Connection closed before a SyncReply was received.");
    }

    private static ClockSyncResult Compute(long t0, long t1, long t2)
    {
        var rtt    = TimeSpan.FromTicks(t2 - t0);
        var offset = TimeSpan.FromTicks(t1 - t0 - rtt.Ticks / 2);
        return new ClockSyncResult(offset, rtt);
    }
}
