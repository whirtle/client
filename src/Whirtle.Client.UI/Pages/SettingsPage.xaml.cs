using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel ViewModel => App.Current.SettingsViewModel;

    public event EventHandler? SaveClicked;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
        => SaveClicked?.Invoke(this, EventArgs.Empty);
}
