using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Pages;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel ViewModel => App.Current.SettingsViewModel;

    public event EventHandler? OKClicked;
    public event EventHandler? CancelClicked;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
        => OKClicked?.Invoke(this, EventArgs.Empty);

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => CancelClicked?.Invoke(this, EventArgs.Empty);
}
