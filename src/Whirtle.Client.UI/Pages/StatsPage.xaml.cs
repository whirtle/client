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
            case nameof(ClockStatsViewModel.MeanOffsetMs):
                MeanOffsetText.Text = FormatOffset(ClockStats.MeanOffsetMs);
                break;
            case nameof(ClockStatsViewModel.SampleCount):
                SampleCountText.Text = ClockStats.SampleCount.ToString();
                break;
            case nameof(ClockStatsViewModel.SecondsSinceLastSync):
                LastSyncText.Text = FormatElapsed(ClockStats.SecondsSinceLastSync);
                break;
            case nameof(ClockStatsViewModel.OutlierCount):
                OutlierCountText.Text = ClockStats.OutlierCount.ToString();
                break;
            case nameof(ClockStatsViewModel.DriftMicrosecondsPerSecond):
                DriftText.Text = FormatDrift(ClockStats.DriftMicrosecondsPerSecond);
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
        }
    }

    private void RefreshAll()
    {
        MeanOffsetText.Text     = FormatOffset(ClockStats.MeanOffsetMs);
        SampleCountText.Text    = ClockStats.SampleCount.ToString();
        LastSyncText.Text       = FormatElapsed(ClockStats.SecondsSinceLastSync);
        OutlierCountText.Text   = ClockStats.OutlierCount.ToString();
        DriftText.Text          = FormatDrift(ClockStats.DriftMicrosecondsPerSecond);
        QueuedFramesText.Text   = PlaybackStats.StatBufferedFrames.ToString();
        QueuedAudioText.Text    = FormatDuration(PlaybackStats.StatBufferedDuration);
        ChunksReceivedText.Text = PlaybackStats.StatTotalChunks.ToString("N0");
        CodecDetailText.Text    = PlaybackStats.StatCodecDetails;
    }

    private static string FormatDuration(TimeSpan duration)
        => $"{duration.TotalMilliseconds:0} ms";

    private static string FormatOffset(double ms)
        => $"{ms:+0.00;-0.00;0.00} ms";

    private static string FormatElapsed(double seconds)
    {
        if (seconds < 0) return "N/A";
        return $"{seconds:0.0} s ago";
    }

    private static string FormatDrift(double driftUsPerSec)
    {
        if (driftUsPerSec == 0.0) return "N/A";
        return driftUsPerSec >= 0
            ? $"+{driftUsPerSec:0} µs/s"
            : $"{driftUsPerSec:0} µs/s";
    }
}
