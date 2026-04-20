// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

namespace Whirtle.Client.Clock;

/// <summary>
/// Snapshot of the Kalman filter state produced by <see cref="ClockSynchronizer"/>
/// after each sync round and surfaced to callers via the <c>onSync</c> callback.
/// </summary>
/// <param name="FilteredOffsetUs">
/// Kalman-estimated clock offset in µs (server − client).
/// Positive means the server clock is ahead of the client.
/// </param>
/// <param name="DriftUsPerS">
/// Kalman-estimated drift in µs/s: the rate at which the offset is changing.
/// Positive means the server clock is pulling ahead.
/// </param>
/// <param name="OffsetStdDevUs">
/// Standard deviation of the offset estimate in µs (√P_OO).
/// Decreases as the filter converges; a proxy for synchronisation accuracy.
/// Equivalent to C++ <c>get_error()</c>.
/// </param>
/// <param name="DriftStdDevUsPerS">
/// Standard deviation of the drift estimate in µs/s (√P_DD).
/// </param>
/// <param name="DriftIsSignificant">
/// <see langword="true"/> when |drift| &gt; 2σ_drift and the estimate is reliable
/// enough to apply drift compensation in time-conversion calls.
/// </param>
/// <param name="UpdateCount">
/// Total number of filter updates processed since the session started.
/// </param>
/// <param name="ForgetCount">
/// Cumulative number of times the adaptive forgetting factor was applied.
/// Spikes indicate sudden changes in network latency or clock behaviour.
/// </param>
/// <param name="LastSyncUtcMicroseconds">
/// Client Unix µs timestamp of the most-recent filter update.
/// Zero if no sync has completed yet.
/// </param>
public sealed record ClockSyncStats(
    double FilteredOffsetUs,
    double DriftUsPerS,
    double OffsetStdDevUs,
    double DriftStdDevUsPerS,
    bool   DriftIsSignificant,
    int    UpdateCount,
    int    ForgetCount,
    long   LastSyncUtcMicroseconds)
{
    /// <summary>Kalman-estimated offset as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan FilteredOffset => TimeSpan.FromMicroseconds(FilteredOffsetUs);
}
