using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class NowPlayingPage : Page
{
    private NowPlayingViewModel ViewModel => App.Current.NowPlayingViewModel;

    private DateTimeOffset _lastVolumeCommandSent = DateTimeOffset.MinValue;
    private static readonly TimeSpan VolumeThrottle = TimeSpan.FromMilliseconds(200);

    public NowPlayingPage()
    {
        InitializeComponent();

        // Update album art and seek bar whenever relevant properties change.
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModel.AlbumArtData):
                if (ViewModel.AlbumArtData is { Length: > 0 } bytes)
                {
                    var bitmap = new BitmapImage();
                    // Keep the stream alive until SetSourceAsync completes; disposing
                    // it early (as a using-in-sync-method would) corrupts the load.
                    using var stream = new System.IO.MemoryStream(bytes);
                    using var ras    = stream.AsRandomAccessStream();
                    try
                    {
                        await bitmap.SetSourceAsync(ras);
                        AlbumArtImage.Source = bitmap;
                    }
                    catch
                    {
                        AlbumArtImage.Source = null;
                    }
                }
                else
                {
                    AlbumArtImage.Source = null;
                }
                break;

            case nameof(ViewModel.DurationSeconds):
                // Always update Maximum so the slider range is correct before Value.
                SeekBar.Maximum = ViewModel.DurationSeconds;
                break;

            case nameof(ViewModel.PositionSeconds):
                SeekBar.Value = ViewModel.PositionSeconds;
                break;
        }
    }

    // Clear auto-focus after the initial layout pass so no control appears
    // highlighted when the page first loads.
    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        LogoSpinStoryboard.Begin();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => FocusSink.Focus(FocusState.Programmatic));
    }

    private async void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (!ViewModel.IsConnected) return;
        if (DateTimeOffset.UtcNow - _lastVolumeCommandSent < VolumeThrottle) return;
        _lastVolumeCommandSent = DateTimeOffset.UtcNow;
        await ViewModel.SetVolumeCommand.ExecuteAsync(slider.Value / 100.0);
    }

    private async void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
        {
            _lastVolumeCommandSent = DateTimeOffset.UtcNow;
            Log.Information("User set volume to {VolumePercent}%", (int)slider.Value);
            await ViewModel.SetVolumeCommand.ExecuteAsync(slider.Value / 100.0);
        }
    }

    private Visibility WaitingVisibility(bool isNotConnected)
        => isNotConnected ? Visibility.Visible : Visibility.Collapsed;
}
