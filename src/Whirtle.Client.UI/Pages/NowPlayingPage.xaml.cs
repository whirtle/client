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

    private async void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
            await ViewModel.SetVolumeCommand.ExecuteAsync(slider.Value / 100.0);
    }

    // Seek — optimistic local update; protocol seek command is a stub for now
    private async void SeekBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Slider slider)
            await ViewModel.SeekCommand.ExecuteAsync(slider.Value);
    }

    private Visibility WaitingVisibility(bool isNotConnected)
        => isNotConnected ? Visibility.Visible : Visibility.Collapsed;
}
