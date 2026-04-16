// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class StatsPage : Page
{
    private ClockStatsViewModel ViewModel => App.Current.NowPlayingViewModel.ClockStats;

    public StatsPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ClockStatsViewModel.MeanOffsetMs):
                MeanOffsetText.Text = FormatOffset(ViewModel.MeanOffsetMs);
                break;
            case nameof(ClockStatsViewModel.SampleCount):
                SampleCountText.Text = ViewModel.SampleCount.ToString();
                break;
            case nameof(ClockStatsViewModel.SecondsSinceLastSync):
                LastSyncText.Text = FormatElapsed(ViewModel.SecondsSinceLastSync);
                break;
            case nameof(ClockStatsViewModel.OutlierCount):
                OutlierCountText.Text = ViewModel.OutlierCount.ToString();
                break;
            case nameof(ClockStatsViewModel.DriftMicrosecondsPerSecond):
                DriftText.Text = FormatDrift(ViewModel.DriftMicrosecondsPerSecond);
                break;
        }
    }

    private void RefreshAll()
    {
        MeanOffsetText.Text   = FormatOffset(ViewModel.MeanOffsetMs);
        SampleCountText.Text  = ViewModel.SampleCount.ToString();
        LastSyncText.Text     = FormatElapsed(ViewModel.SecondsSinceLastSync);
        OutlierCountText.Text = ViewModel.OutlierCount.ToString();
        DriftText.Text        = FormatDrift(ViewModel.DriftMicrosecondsPerSecond);
    }

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
