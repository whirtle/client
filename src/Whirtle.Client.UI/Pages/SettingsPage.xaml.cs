using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.Discovery;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel    ViewModel            => App.Current.SettingsViewModel;
    private NowPlayingViewModel  NowPlayingViewModel  => App.Current.NowPlayingViewModel;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void ConnectSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is ServiceEndpoint endpoint)
            await NowPlayingViewModel.ConnectCommand.ExecuteAsync(endpoint);
    }
}
