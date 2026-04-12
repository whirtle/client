using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class NowPlayingPage : Page
{
    private NowPlayingViewModel ViewModel => App.Current.NowPlayingViewModel;

    private bool _volumeChanging;
    private SettingsWindow? _settingsWindow;

    public NowPlayingPage()
    {
        InitializeComponent();

        // Update album art whenever the raw bytes change
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ViewModel.AlbumArtData))
            return;

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
    }

    // Volume slider — throttle commands so we don't flood the server
    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_volumeChanging) return;
        _volumeChanging = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await ViewModel.SetVolumeCommand.ExecuteAsync(e.NewValue / 100.0);
            _volumeChanging = false;
        });
    }

    // Seek — optimistic local update; protocol seek command is a stub for now
    private async void SeekBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
            await ViewModel.SeekCommand.ExecuteAsync(slider.Value);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }
}
