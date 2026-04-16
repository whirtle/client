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

    /// <summary>Kalman-filtered clock offset in ms.</summary>
    [ObservableProperty] private double _filteredOffsetMs;

    /// <summary>
    /// Standard deviation of the offset estimate in µs (√P_OO).
    /// A proxy for synchronisation accuracy; decreases as the filter converges.
    /// </summary>
    [ObservableProperty] private double _offsetStdDevUs;

    /// <summary>Estimated clock drift in parts per million (µs/s × 1).</summary>
    [ObservableProperty] private double _driftUsPerS;

    /// <summary>Standard deviation of the drift estimate in µs/s.</summary>
    [ObservableProperty] private double _driftStdDevUsPerS;

    /// <summary>
    /// Whether drift compensation is currently being applied to timestamp conversions.
    /// True when |drift| &gt; 2σ_drift.
    /// </summary>
    [ObservableProperty] private bool _driftIsSignificant;

    /// <summary>Total accepted sync updates since the session started.</summary>
    [ObservableProperty] private int _updateCount;

    /// <summary>
    /// Cumulative times the adaptive forgetting factor fired.
    /// Spikes indicate sudden network or clock disruptions.
    /// </summary>
    [ObservableProperty] private int _forgetCount;

    /// <summary>
    /// Elapsed seconds since the last accepted sync.
    /// -1.0 when no sync has completed yet.
    /// </summary>
    [ObservableProperty] private double _secondsSinceLastSync = -1.0;

    // ── Constructor ───────────────────────────────────────────────────────

    public ClockStatsViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => RefreshElapsed();
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Updates all observable properties from the latest Kalman filter stats.
    /// Safe to call from any thread; dispatches to the UI thread internally.
    /// </summary>
    public void Update(ClockSyncStats stats)
    {
        _lastSyncUtcUs = stats.LastSyncUtcMicroseconds;

        _dispatcher.TryEnqueue(() =>
        {
            FilteredOffsetMs    = stats.FilteredOffsetUs / 1_000.0;
            OffsetStdDevUs      = stats.OffsetStdDevUs;
            DriftUsPerS         = stats.DriftUsPerS;
            DriftStdDevUsPerS   = stats.DriftStdDevUsPerS;
            DriftIsSignificant  = stats.DriftIsSignificant;
            UpdateCount         = stats.UpdateCount;
            ForgetCount         = stats.ForgetCount;
            RefreshElapsed();
        });
    }

    /// <summary>Resets all stats to their initial "no session" state.</summary>
    public void Reset()
    {
        _lastSyncUtcUs = 0;
        _dispatcher.TryEnqueue(() =>
        {
            FilteredOffsetMs    = 0;
            OffsetStdDevUs      = 0;
            DriftUsPerS         = 0;
            DriftStdDevUsPerS   = 0;
            DriftIsSignificant  = false;
            UpdateCount         = 0;
            ForgetCount         = 0;
            SecondsSinceLastSync = -1.0;
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

        long nowUs = (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) / 10;
        SecondsSinceLastSync = Math.Max(0.0, (nowUs - _lastSyncUtcUs) / 1_000_000.0);
    }
}
