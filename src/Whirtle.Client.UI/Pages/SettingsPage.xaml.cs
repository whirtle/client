using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel ViewModel => App.Current.SettingsViewModel;

    public SettingsPage()
    {
        InitializeComponent();
    }
}
