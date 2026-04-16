// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Clock;

/// <summary>
/// Snapshot of clock-synchronisation statistics accumulated by
/// <see cref="ClockSynchronizer"/> over its rolling measurement window.
/// </summary>
/// <param name="MeanOffset">
/// Mean clock offset across all accepted samples in the current window.
/// Positive means the server clock is ahead of the client.
/// </param>
/// <param name="SampleCount">
/// Total number of sync rounds whose results were accepted into the window
/// (i.e. not discarded as outliers).
/// </param>
/// <param name="LastSyncUtcMicroseconds">
/// <see cref="ISystemClock.UtcNowMicroseconds"/> value recorded at the time of
/// the most recent accepted sync. Zero if no sync has completed yet.
/// </param>
/// <param name="OutlierCount">
/// Running total of sync rounds whose results were discarded because the
/// round-trip time exceeded 2× the window median.
/// </param>
/// <param name="DriftMicrosecondsPerSecond">
/// Estimated rate of change of the clock offset in µs/s, derived from a
/// least-squares linear fit across the accepted samples in the current window.
/// Zero when fewer than two accepted samples are available.
/// Positive means the server clock is drifting ahead of the client.
/// </param>
public sealed record ClockSyncStats(
    TimeSpan MeanOffset,
    int      SampleCount,
    long     LastSyncUtcMicroseconds,
    int      OutlierCount,
    double   DriftMicrosecondsPerSecond);
