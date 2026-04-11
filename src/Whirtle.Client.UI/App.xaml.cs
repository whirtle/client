using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Whirtle.Client.Audio;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private NowPlayingViewModel? _nowPlayingViewModel;
    private SettingsViewModel? _settingsViewModel;

    internal static new App Current => (App)Application.Current;

    public NowPlayingViewModel NowPlayingViewModel => _nowPlayingViewModel!;
    public SettingsViewModel SettingsViewModel => _settingsViewModel!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settingsViewModel = new SettingsViewModel();

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _nowPlayingViewModel = new NowPlayingViewModel(
            new WindowsAudioDeviceEnumerator(),
            dispatcher);

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }
}
