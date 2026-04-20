// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Serilog;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Clock;

/// <summary>
/// Performs NTP-style clock synchronisation over a <see cref="ProtocolClient"/>
/// and maintains a Kalman filter to estimate clock offset and drift.
///
/// Four-timestamp model
/// ────────────────────
///  T1  client sends   client/time(client_transmitted = T1)  [Unix µs]
///  T2  server records server_received = T2, replies server/time(T1, T2, T3)
///  T3  server records server_transmitted = T3 in the reply
///  T4  client records current time = T4
///
///  offset    = ((T2 − T1) + (T3 − T4)) / 2
///  delay     = (T4 − T1) − (T3 − T2)
///  max_error = delay / 2
///
/// Usage
/// ─────
/// Call <see cref="RunAsync"/> to maintain a continuous sync loop.
/// While it is running, route every incoming <see cref="ServerTimeMessage"/>
/// from the main receive loop to <see cref="Deliver"/>.
/// Use <see cref="ClientToServerUs"/> / <see cref="ServerToClientUs"/> to convert
/// timestamps between the two time domains using the Kalman-filtered state.
/// </summary>
public sealed class ClockSynchronizer : IDisposable
{
    // Per-round sync timeout — disabled for now while we iterate on rate control.
    // The timeout logic is still honoured when a finite TimeSpan is passed in
    // (e.g. from tests); the infinite default just means steady-state sync rounds
    // wait indefinitely for the server reply rather than giving up at 10 s.
    private static readonly TimeSpan DefaultSyncTimeout = Timeout.InfiniteTimeSpan;

    /// <summary>Default interval between sync rounds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly ProtocolClient  _client;
    private readonly ISystemClock    _clock;
    private readonly TimeSpan        _syncTimeout;
    private readonly TimeSpan        _rapidSyncInterval;

    // Ensures at most one sync round is in-flight at a time.
    private readonly SemaphoreSlim   _syncLock = new(1, 1);

    // Kalman filter — all clock estimation state lives here.
    private readonly KalmanClockFilter _filter = new();

    // Non-null while a sync is waiting for the server reply.
    private sealed record PendingSync(long T0, TaskCompletionSource<ClockSyncResult> Tcs);
    private PendingSync? _pending;

    // Non-null while a caller is waiting for clock convergence.
    private TaskCompletionSource<bool>? _convergenceTcs;
    private double _convergenceTargetUs;
    private int    _convergenceMinSamples;

    public ClockSynchronizer(ProtocolClient client)
        : this(client, SystemClock.Instance) { }

    internal ClockSynchronizer(
        ProtocolClient client,
        ISystemClock   clock,
        TimeSpan?      syncTimeout       = null,
        TimeSpan?      rapidSyncInterval = null)
    {
        _client            = client;
        _clock             = clock;
        _syncTimeout       = syncTimeout       ?? DefaultSyncTimeout;
        _rapidSyncInterval = rapidSyncInterval ?? RapidSyncInterval;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Delivers a <see cref="ServerTimeMessage"/> reply from the main receive loop,
    /// completing any in-flight <see cref="SyncOnceAsync"/> call.
    /// </summary>
    /// <returns><see langword="true"/> if a pending sync was completed.</returns>
    public bool Deliver(ServerTimeMessage msg)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is null) return false;

        var t4 = _clock.UtcNowMicroseconds;
        pending.Tcs.TrySetResult(Compute(pending.T0, msg.ServerReceived, msg.ServerTransmitted, t4));
        return true;
    }

    /// <summary>
    /// Sends one <c>client/time</c> message and returns the raw
    /// <see cref="ClockSyncResult"/> once <see cref="Deliver"/> is called with
    /// the server's reply.
    /// </summary>
    public async Task<ClockSyncResult> SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(_syncTimeout);
            using var linked   = CancellationTokenSource.CreateLinkedTokenSource(
                                     cancellationToken, deadline.Token);
            var token = linked.Token;

            var tcs = new TaskCompletionSource<ClockSyncResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            using var reg = token.Register(
                () => tcs.TrySetCanceled(token), useSynchronizationContext: false);

            var t0 = _clock.UtcNowMicroseconds;
            Interlocked.Exchange(ref _pending, new PendingSync(t0, tcs));

            await _client.SendAsync(new ClientTimeMessage(t0), token).ConfigureAwait(false);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _pending, null);
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Returns a task that completes when <see cref="RunAsync"/>'s Kalman filter
    /// estimate has converged to within <paramref name="targetStdDevUs"/> microseconds,
    /// or when <paramref name="timeout"/> elapses. The task result is
    /// <see langword="true"/> on convergence, <see langword="false"/> on timeout.
    /// Must be called before <see cref="RunAsync"/> to avoid missing early results.
    /// </summary>
    public Task<bool> WaitForConvergenceAsync(
        double            targetStdDevUs,
        TimeSpan          timeout,
        CancellationToken cancellationToken = default,
        int               minSamples        = 1)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _convergenceTcs         = tcs;
        _convergenceTargetUs    = targetStdDevUs;
        _convergenceMinSamples  = minSamples;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        cts.Token.Register(
            () => tcs.TrySetResult(false), useSynchronizationContext: false);

        return tcs.Task;
    }

    /// <summary>
    /// Continuously syncs the clock, invoking <paramref name="onSync"/> after each
    /// successful measurement. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// Runs in two phases:
    /// <list type="number">
    ///   <item><b>Rapid phase</b> — performs <see cref="RapidSyncCount"/> rounds at
    ///   <see cref="RapidSyncInterval"/> to seed the Kalman filter quickly.</item>
    ///   <item><b>Steady-state phase</b> — continues at <paramref name="interval"/>
    ///   (default <see cref="DefaultInterval"/>) indefinitely.</item>
    /// </list>
    /// The caller must route every incoming <see cref="ServerTimeMessage"/> to
    /// <see cref="Deliver"/> while this method is active.
    /// </remarks>
    public async Task RunAsync(
        Action<ClockSyncResult, ClockSyncStats> onSync,
        TimeSpan?                               interval          = null,
        CancellationToken                       cancellationToken = default)
    {
        var period = interval ?? DefaultInterval;

        // Phase 1: rapid convergence — seed the filter quickly.
        for (int i = 0; i < RapidSyncCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
                FeedFilter(result);
                NotifySync(result, onSync);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* transient failure (incl. sync timeout) — continue */ }

            if (i < RapidSyncCount - 1)
                await Task.Delay(_rapidSyncInterval, cancellationToken).ConfigureAwait(false);
        }

        // Phase 2: steady-state.
        while (true)
        {
            await Task.Delay(period, cancellationToken).ConfigureAwait(false);

            try
            {
                var result = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
                FeedFilter(result);
                NotifySync(result, onSync);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { /* transient failure — retry after interval */ }
        }
    }

    public void Dispose() => _syncLock.Dispose();

    /// <summary>
    /// Converts a client-domain timestamp (Unix µs) to the server domain using
    /// the current Kalman-filtered offset and drift.
    /// </summary>
    public long ClientToServerUs(long clientUs) => _filter.ClientToServerUs(clientUs);

    /// <summary>
    /// Converts a server-domain timestamp (Unix µs) to the client domain using
    /// the current Kalman-filtered offset and drift.
    /// </summary>
    public long ServerToClientUs(long serverUs) => _filter.ServerToClientUs(serverUs);

    // ── Internal helpers ───────────────────────────────────────────────────────

    // Number of rapid syncs performed immediately after connecting.
    private const int RapidSyncCount = 3;

    // Interval between rapid syncs.
    private static readonly TimeSpan RapidSyncInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Calls <paramref name="onSync"/> with the latest filter snapshot and, if a
    /// convergence waiter is pending, signals it when the standard deviation has
    /// dropped below the target.
    /// </summary>
    private void NotifySync(ClockSyncResult result, Action<ClockSyncResult, ClockSyncStats> onSync)
    {
        var stats = GetStats();
        onSync(result, stats);
        if (_convergenceTcs is { Task.IsCompleted: false } tcs &&
            stats.UpdateCount >= _convergenceMinSamples &&
            stats.OffsetStdDevUs < _convergenceTargetUs)
        {
            tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Feeds a completed sync measurement into the Kalman filter and logs a
    /// per-round summary at Information level.
    /// </summary>
    private void FeedFilter(ClockSyncResult result)
    {
        var offsetUs   = result.ClockOffset.TotalMicroseconds;
        var maxErrorUs = result.MaxError.TotalMicroseconds;

        _filter.Update(offsetUs, maxErrorUs, result.ClientReceivedUs);

        Log.Debug(
            "ClockSynchronizer: raw_offset={RawOff:+0.000;-0.000} ms, " +
            "filtered_offset={FiltOff:+0.000;-0.000} ms ±{Sigma:F3} ms, " +
            "drift={Drift:+0.000;-0.000} ms/s {DriftSig}, " +
            "RTT={Rtt:F3} ms, max_err={MaxErr:F3} ms",
            offsetUs / 1_000.0,
            _filter.OffsetUs / 1_000.0, _filter.OffsetStdDevUs / 1_000.0,
            _filter.DriftUsPerS / 1_000.0, _filter.DriftIsSignificant ? "(applied)" : "(suppressed)",
            result.RoundTripTime.TotalMilliseconds,
            maxErrorUs / 1_000.0);
    }

    /// <summary>
    /// Returns a snapshot of the current Kalman filter state.
    /// </summary>
    internal ClockSyncStats GetStats() => new(
        FilteredOffsetUs:       _filter.OffsetUs,
        DriftUsPerS:            _filter.DriftUsPerS,
        OffsetStdDevUs:         _filter.OffsetStdDevUs,
        DriftStdDevUsPerS:      _filter.DriftStdDevUsPerS,
        DriftIsSignificant:     _filter.DriftIsSignificant,
        UpdateCount:            _filter.UpdateCount,
        ForgetCount:            _filter.ForgetCount,
        LastSyncUtcMicroseconds: _filter.LastUpdateUs);

    // T0=T1 (client_transmitted), t1=T2 (server_received),
    // t2=T3 (server_transmitted), t3=T4 (client_received).
    private static ClockSyncResult Compute(long t0, long t1, long t2, long t3)
    {
        var rtt      = TimeSpan.FromMicroseconds(t3 - t0);
        var offset   = TimeSpan.FromMicroseconds(((t1 - t0) + (t2 - t3)) / 2);
        var maxError = TimeSpan.FromMicroseconds(((t3 - t0) - (t2 - t1)) / 2);
        return new ClockSyncResult(offset, rtt, maxError, t3);
    }
}
