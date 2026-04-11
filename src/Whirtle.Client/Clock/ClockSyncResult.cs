namespace Whirtle.Client.Clock;

/// <summary>
/// Result of a single NTP-style clock synchronisation round trip.
/// </summary>
/// <param name="ClockOffset">
/// Estimated offset to add to the local clock to obtain server time.
/// Positive means the server clock is ahead of the client clock.
/// </param>
/// <param name="RoundTripTime">Total elapsed time for the round trip.</param>
public sealed record ClockSyncResult(TimeSpan ClockOffset, TimeSpan RoundTripTime);
