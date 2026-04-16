// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Whirtle.Client.Clock;

namespace Whirtle.Client.UI.ViewModels;

/// <summary>
/// Observable state for the clock-synchronisation statistics pane.
/// Updated by <see cref="NowPlayingViewModel"/> via <see cref="Update"/>
/// after each sync round. A one-second timer refreshes
/// <see cref="SecondsSinceLastSync"/> while the stats window is visible.
/// </summary>
public sealed partial class ClockStatsViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _ticker;

    // Raw µs timestamp of the last accepted sync; 0 = never synced.
    private long _lastSyncUtcUs;

    // ── Observable stats ──────────────────────────────────────────────────

    /// <summary>Mean clock offset across the current measurement window (ms).</summary>
    [ObservableProperty] private double _meanOffsetMs;

    /// <summary>Total accepted sync samples since the session started.</summary>
    [ObservableProperty] private int _sampleCount;

    /// <summary>
    /// Elapsed seconds since the last accepted sync.
    /// -1.0 when no sync has completed yet.
    /// </summary>
    [ObservableProperty] private double _secondsSinceLastSync = -1.0;

    /// <summary>Running total of sync rounds discarded as RTT outliers.</summary>
    [ObservableProperty] private int _outlierCount;

    /// <summary>
    /// Estimated drift between local and server clocks in µs/s.
    /// Positive means the server clock is moving ahead of the client.
    /// </summary>
    [ObservableProperty] private double _driftMicrosecondsPerSecond;

    // ── Constructor ───────────────────────────────────────────────────────

    public ClockStatsViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => RefreshElapsed();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all observable properties from the latest sync stats.
    /// Safe to call from any thread; dispatches to the UI thread internally.
    /// </summary>
    public void Update(ClockSyncStats stats)
    {
        _lastSyncUtcUs = stats.LastSyncUtcMicroseconds;

        _dispatcher.TryEnqueue(() =>
        {
            MeanOffsetMs                = stats.MeanOffset.TotalMilliseconds;
            SampleCount                 = stats.SampleCount;
            OutlierCount                = stats.OutlierCount;
            DriftMicrosecondsPerSecond  = stats.DriftMicrosecondsPerSecond;
            RefreshElapsed();
        });
    }

    /// <summary>Resets all stats to their initial "no session" state.</summary>
    public void Reset()
    {
        _lastSyncUtcUs = 0;
        _dispatcher.TryEnqueue(() =>
        {
            MeanOffsetMs               = 0;
            SampleCount                = 0;
            SecondsSinceLastSync       = -1.0;
            OutlierCount               = 0;
            DriftMicrosecondsPerSecond = 0;
        });
    }

    /// <summary>
    /// Starts the one-second elapsed-time ticker.
    /// Call when the stats window becomes visible.
    /// </summary>
    public void StartTicker() => _ticker.Start();

    /// <summary>
    /// Stops the one-second elapsed-time ticker.
    /// Call when the stats window is hidden.
    /// </summary>
    public void StopTicker() => _ticker.Stop();

    // ── Helpers ───────────────────────────────────────────────────────────

    private void RefreshElapsed()
    {
        if (_lastSyncUtcUs == 0)
        {
            SecondsSinceLastSync = -1.0;
            return;
        }

        // Convert the stored µs epoch timestamp to a comparable value using
        // the same epoch as DateTimeOffset.UtcNow (Unix epoch in µs).
        long nowUs = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
        SecondsSinceLastSync = Math.Max(0.0, (nowUs - _lastSyncUtcUs) / 1_000_000.0);
    }
}
