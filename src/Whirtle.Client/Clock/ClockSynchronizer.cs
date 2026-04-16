// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Serilog;
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
///
/// Usage
/// ─────
/// Call <see cref="RunAsync"/> to maintain a continuous sync.
/// While it is running, route every incoming <see cref="ServerTimeMessage"/>
/// from the main receive loop to <see cref="Deliver"/>.
/// </summary>
public sealed class ClockSynchronizer
{
    private static readonly TimeSpan DefaultSyncTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Default interval between sync rounds.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    // Number of past measurements retained for min-RTT selection.
    private const int WindowSize = 8;

    private readonly ProtocolClient _client;
    private readonly ISystemClock   _clock;
    private readonly TimeSpan       _syncTimeout;
    private readonly TimeSpan       _rapidSyncInterval;

    // Ensures at most one sync round is in-flight at a time.
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // Rolling window of recent measurements. Accessed only from RunAsync's single loop.
    private readonly Queue<ClockSyncResult> _window = new(WindowSize);

    // Stats counters — accessed only from RunAsync's single async loop; no locking needed.
    private int  _sampleCount  = 0;
    private int  _outlierCount = 0;
    private long _lastSyncUtcUs = 0;

    // Parallel time series for drift estimation: (timestampUs, offsetUs) for each
    // accepted sample.  Evicted in lock-step with _window.
    private readonly Queue<(long TimestampUs, long OffsetUs)> _timeSeries = new(WindowSize);

    // Non-null while a sync is waiting for the server reply.
    private sealed record PendingSync(long T0, TaskCompletionSource<ClockSyncResult> Tcs);
    private PendingSync? _pending;

    public ClockSynchronizer(ProtocolClient client)
        : this(client, SystemClock.Instance) { }

    internal ClockSynchronizer(
        ProtocolClient  client,
        ISystemClock    clock,
        TimeSpan?       syncTimeout       = null,
        TimeSpan?       rapidSyncInterval = null)
    {
        _client            = client;
        _clock             = clock;
        _syncTimeout       = syncTimeout       ?? DefaultSyncTimeout;
        _rapidSyncInterval = rapidSyncInterval ?? RapidSyncInterval;
    }

    /// <summary>
    /// Delivers a <see cref="ServerTimeMessage"/> reply from the main receive loop,
    /// completing any in-flight <see cref="SyncOnceAsync"/> call.
    /// </summary>
    /// <returns><see langword="true"/> if a pending sync was completed.</returns>
    public bool Deliver(ServerTimeMessage msg)
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        if (pending is null) return false;

        var t2 = _clock.UtcNowMicroseconds;
        pending.Tcs.TrySetResult(Compute(pending.T0, msg.ServerReceived, t2));
        return true;
    }

    /// <summary>
    /// Sends one <c>client/time</c> message and returns the measured
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
    /// Continuously syncs the clock, invoking <paramref name="onSync"/> after each
    /// successful measurement. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs in two phases:
    /// <list type="number">
    ///   <item><b>Rapid phase</b> — performs <see cref="RapidSyncCount"/> rounds at
    ///   <see cref="RapidSyncInterval"/> intervals to converge the offset estimate
    ///   quickly after connecting.</item>
    ///   <item><b>Steady-state phase</b> — continues at <paramref name="interval"/>
    ///   (default <see cref="DefaultInterval"/>) indefinitely.</item>
    /// </list>
    /// </para>
    /// The caller must route every incoming <see cref="ServerTimeMessage"/> to
    /// <see cref="Deliver"/> while this method is active; otherwise each round
    /// will time out and be retried.
    /// </remarks>
    public async Task RunAsync(
        Action<ClockSyncResult, ClockSyncStats> onSync,
        TimeSpan?                               interval          = null,
        CancellationToken                       cancellationToken = default)
    {
        var period = interval ?? DefaultInterval;

        // Phase 1: rapid convergence — seed the rolling window quickly.
        for (int i = 0; i < RapidSyncCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
                var best   = AcceptResult(result);
                onSync(best, GetStats());
            }
            catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
            catch { /* transient failure — continue to next rapid round */ }

            if (i < RapidSyncCount - 1)
            {
                try { await Task.Delay(_rapidSyncInterval, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
            }
        }

        // Phase 2: steady-state.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try { await Task.Delay(period, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }

            try
            {
                var result = await SyncOnceAsync(cancellationToken).ConfigureAwait(false);
                var best   = AcceptResult(result);
                onSync(best, GetStats());
            }
            catch (OperationCanceledException)
            {
                // If the outer token fired, propagate; otherwise it was the
                // per-round deadline — swallow and retry after the next interval.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch { /* transient failure (e.g. send error) — retry after interval */ }
        }
    }

    // Number of rapid syncs performed immediately after connecting.
    private const int RapidSyncCount = 3;

    // Interval between rapid syncs.
    private static readonly TimeSpan RapidSyncInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Adds <paramref name="raw"/> to the rolling window and returns the sample with
    /// the lowest round-trip time in the current window (NTP min-RTT heuristic).
    /// Samples with the shortest RTT are most likely to have symmetric network delay
    /// and therefore the most accurate clock offset estimate.
    /// </summary>
    /// <remarks>
    /// Accessed only from <see cref="RunAsync"/>'s single async loop — no locking needed.
    /// Exposed as <c>internal</c> for unit testing.
    /// </remarks>
    internal ClockSyncResult AcceptResult(ClockSyncResult raw)
    {
        // Outlier gate: discard samples with RTT > 2× the median RTT of the existing
        // window. High-RTT samples are likely suffering from asymmetric queuing delay,
        // which biases the offset estimate. We need at least 2 window entries to compute
        // a meaningful median; the first two samples always enter unconditionally.
        if (_window.Count >= 2)
        {
            var sorted = _window.Select(r => r.RoundTripTime).OrderBy(x => x).ToList();
            var median = sorted[sorted.Count / 2];
            if (raw.RoundTripTime > median + median) // > 2× median
            {
                Log.Debug(
                    "Clock sync: discarding high-RTT sample ({Rtt} μs > 2×median {Median} μs)",
                    (long)raw.RoundTripTime.TotalMicroseconds,
                    (long)median.TotalMicroseconds);
                _outlierCount++;
                return _window.MinBy(r => r.RoundTripTime)!;
            }
        }

        var nowUs = _clock.UtcNowMicroseconds;

        if (_window.Count >= WindowSize)
        {
            _window.Dequeue();
            _timeSeries.Dequeue();
        }
        _window.Enqueue(raw);
        _timeSeries.Enqueue((nowUs, (long)raw.ClockOffset.TotalMicroseconds));

        _sampleCount++;
        _lastSyncUtcUs = nowUs;

        var sortedWindow = _window.Select(r => r.RoundTripTime).OrderBy(x => x).ToList();
        var medianRtt = sortedWindow[sortedWindow.Count / 2];
        Log.Debug("Clock sync: median RTT {MedianRtt} μs", (long)medianRtt.TotalMicroseconds);

        return _window.MinBy(r => r.RoundTripTime)!;
    }

    /// <summary>
    /// Returns a snapshot of synchronisation statistics for the current window.
    /// Safe to call from any thread, but intended for use inside the
    /// <see cref="RunAsync"/> loop or from the <c>onSync</c> callback.
    /// </summary>
    internal ClockSyncStats GetStats()
    {
        var meanOffset = _window.Count > 0
            ? TimeSpan.FromMicroseconds(_window.Average(r => r.ClockOffset.TotalMicroseconds))
            : TimeSpan.Zero;

        return new ClockSyncStats(
            MeanOffset:                  meanOffset,
            SampleCount:                 _sampleCount,
            LastSyncUtcMicroseconds:     _lastSyncUtcUs,
            OutlierCount:                _outlierCount,
            DriftMicrosecondsPerSecond:  ComputeDrift());
    }

    /// <summary>
    /// Estimates clock drift as the least-squares slope of the time series of
    /// (timestamp, offset) pairs, converted to µs of offset change per second.
    /// Returns zero when fewer than two accepted samples are available.
    /// </summary>
    private double ComputeDrift()
    {
        if (_timeSeries.Count < 2) return 0.0;

        // Subtract the mean timestamp first to keep the arithmetic well-conditioned
        // (raw µs-since-epoch values are ~10^15, which would cause precision loss
        // when squaring in double arithmetic).
        double tMean = _timeSeries.Average(p => (double)p.TimestampUs);
        double oMean = _timeSeries.Average(p => (double)p.OffsetUs);

        double num = 0.0;
        double den = 0.0;
        foreach (var (ts, os) in _timeSeries)
        {
            double dt = ts - tMean;
            num += dt * (os - oMean);
            den += dt * dt;
        }

        if (den == 0.0) return 0.0;

        // slope is (µs offset) / (µs time); multiply by 1_000_000 → µs/s.
        return (num / den) * 1_000_000.0;
    }

    private static ClockSyncResult Compute(long t0, long t1, long t2)
    {
        var rtt    = TimeSpan.FromMicroseconds(t2 - t0);
        var offset = TimeSpan.FromMicroseconds(t1 - t0 - (t2 - t0) / 2);
        return new ClockSyncResult(offset, rtt);
    }
}
