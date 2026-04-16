// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

namespace Whirtle.Client.Clock;

/// <summary>
/// Result of a single NTP-style clock synchronisation round trip (four-timestamp model).
/// </summary>
/// <param name="ClockOffset">
/// Estimated offset to add to the local clock to obtain server time: ((T2-T1)+(T3-T4))/2.
/// Positive means the server clock is ahead of the client clock.
/// </param>
/// <param name="RoundTripTime">Total elapsed time for the round trip (T4-T1).</param>
/// <param name="MaxError">
/// Half the adjusted network delay: ((T4-T1)-(T3-T2))/2.
/// Represents the worst-case offset uncertainty due to asymmetric network delays.
/// </param>
/// <param name="ClientReceivedUs">
/// Client clock value (Unix µs) at T4 — the moment the server reply was received.
/// Passed to <see cref="KalmanClockFilter.Update"/> as <c>time_added</c>.
/// </param>
public sealed record ClockSyncResult(
    TimeSpan ClockOffset,
    TimeSpan RoundTripTime,
    TimeSpan MaxError,
    long     ClientReceivedUs);
