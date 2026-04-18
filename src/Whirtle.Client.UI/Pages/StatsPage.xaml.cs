// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class StatsPage : Page
{
    private ClockStatsViewModel  ClockStats   => App.Current.NowPlayingViewModel.ClockStats;
    private NowPlayingViewModel  PlaybackStats => App.Current.NowPlayingViewModel;

    public StatsPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClockStats.PropertyChanged   += OnClockStatsPropertyChanged;
        PlaybackStats.PropertyChanged += OnPlaybackStatsPropertyChanged;
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ClockStats.PropertyChanged   -= OnClockStatsPropertyChanged;
        PlaybackStats.PropertyChanged -= OnPlaybackStatsPropertyChanged;
    }

    private void OnClockStatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClockStatsViewModel.FilteredOffsetMs):
            case nameof(ClockStatsViewModel.OffsetStdDevUs):
                FilteredOffsetText.Text = FormatOffset(ClockStats.FilteredOffsetMs, ClockStats.OffsetStdDevUs);
                break;
            case nameof(ClockStatsViewModel.UpdateCount):
                UpdateCountText.Text = ClockStats.UpdateCount.ToString();
                break;
            case nameof(ClockStatsViewModel.SecondsSinceLastSync):
                LastSyncText.Text = FormatElapsed(ClockStats.SecondsSinceLastSync);
                break;
            case nameof(ClockStatsViewModel.ForgetCount):
                ForgetCountText.Text = ClockStats.ForgetCount.ToString();
                break;
            case nameof(ClockStatsViewModel.DriftUsPerS):
            case nameof(ClockStatsViewModel.DriftIsSignificant):
                DriftText.Text = FormatDrift(ClockStats.DriftUsPerS, ClockStats.DriftIsSignificant);
                break;
        }
    }

    private void OnPlaybackStatsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingViewModel.StatBufferedFrames):
                QueuedFramesText.Text = PlaybackStats.StatBufferedFrames.ToString();
                break;
            case nameof(NowPlayingViewModel.StatBufferedDuration):
                QueuedAudioText.Text = FormatDuration(PlaybackStats.StatBufferedDuration);
                break;
            case nameof(NowPlayingViewModel.StatTotalChunks):
                ChunksReceivedText.Text = PlaybackStats.StatTotalChunks.ToString("N0");
                break;
            case nameof(NowPlayingViewModel.StatCodecDetails):
                CodecDetailText.Text = PlaybackStats.StatCodecDetails;
                break;
            case nameof(NowPlayingViewModel.StatBufferUnderruns):
                BufferUnderrunsText.Text = PlaybackStats.StatBufferUnderruns.ToString("N0");
                break;
            case nameof(NowPlayingViewModel.StatMinBufferFloorHits):
                MinBufferFloorHitsText.Text = PlaybackStats.StatMinBufferFloorHits.ToString("N0");
                break;
            case nameof(NowPlayingViewModel.StatRateRatio):
                RateRatioText.Text = FormatRateRatio(PlaybackStats.StatRateRatio);
                break;
            case nameof(NowPlayingViewModel.IsClockConverged):
                ConvergedText.Text = FormatBool(PlaybackStats.IsClockConverged);
                break;
            case nameof(NowPlayingViewModel.IsClockReady):
                ClockReadyText.Text = FormatBool(PlaybackStats.IsClockReady);
                break;
        }
    }

    private void RefreshAll()
    {
        FilteredOffsetText.Text  = FormatOffset(ClockStats.FilteredOffsetMs, ClockStats.OffsetStdDevUs);
        UpdateCountText.Text     = ClockStats.UpdateCount.ToString();
        LastSyncText.Text        = FormatElapsed(ClockStats.SecondsSinceLastSync);
        ForgetCountText.Text     = ClockStats.ForgetCount.ToString();
        DriftText.Text           = FormatDrift(ClockStats.DriftUsPerS, ClockStats.DriftIsSignificant);
        QueuedFramesText.Text    = PlaybackStats.StatBufferedFrames.ToString();
        QueuedAudioText.Text     = FormatDuration(PlaybackStats.StatBufferedDuration);
        ChunksReceivedText.Text  = PlaybackStats.StatTotalChunks.ToString("N0");
        CodecDetailText.Text     = PlaybackStats.StatCodecDetails;
        BufferUnderrunsText.Text    = PlaybackStats.StatBufferUnderruns.ToString("N0");
        MinBufferFloorHitsText.Text = PlaybackStats.StatMinBufferFloorHits.ToString("N0");
        RateRatioText.Text          = FormatRateRatio(PlaybackStats.StatRateRatio);
        ConvergedText.Text          = FormatBool(PlaybackStats.IsClockConverged);
        ClockReadyText.Text      = FormatBool(PlaybackStats.IsClockReady);
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{duration.TotalMilliseconds:0} ms";

    private static string FormatOffset(double ms, double stdDevUs)
        => $"{ms:+0.00;-0.00;0.00} ms ±{stdDevUs:0.00} µs";

    private static string FormatElapsed(double seconds)
    {
        if (seconds < 0) return "N/A";
        return $"{seconds:0.0} s ago";
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";

    private static string FormatRateRatio(double ratio)
        => $"{ratio:0.0000}× ({(ratio - 1.0) * 100.0:+0.00;-0.00;0.00}%)";

    private static string FormatDrift(double driftUsPerSec, bool isSignificant)
    {
        if (!isSignificant) return "< 2σ (suppressed)";
        return driftUsPerSec >= 0
            ? $"+{driftUsPerSec:0.00} µs/s"
            : $"{driftUsPerSec:0.00} µs/s";
    }
}
