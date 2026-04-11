using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Whirtle.Client.UI.Pages;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    private bool _hideOnClose = true;

    public NowPlayingViewModel NowPlayingViewModel => App.Current.NowPlayingViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Mica material background (Windows 11 Fluent Design)
        SystemBackdrop = new MicaBackdrop();

        // Extend XAML content into the title bar area
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Initial window size
        AppWindow.Resize(new SizeInt32(1080, 740));

        // Navigate to Now Playing on launch
        NavView.SelectedItem = NavView.MenuItems[0];

        // Intercept close → minimize to tray instead
        AppWindow.Closing += AppWindow_Closing;

        // H.NotifyIcon.WinUI does not support click event attributes in XAML;
        // double-click restore is wired via the DoubleClickCommand DP instead.
        TrayIcon.DoubleClickCommand = new RelayCommand(RestoreFromTray);
    }

    // ── Window close / tray ────────────────────────────────────────────────

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_hideOnClose)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void HideToTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void RestoreFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_RESTORE);
        Activate();
    }

    // ── Tray icon handlers ─────────────────────────────────────────────────

    private void TrayShow_Click(object sender, RoutedEventArgs e)
        => RestoreFromTray();

    private void TrayMute_Click(object sender, RoutedEventArgs e)
    {
        if (NowPlayingViewModel.ToggleMuteCommand.CanExecute(null))
            _ = NowPlayingViewModel.ToggleMuteCommand.ExecuteAsync(null);
    }

    private void TrayQuit_Click(object sender, RoutedEventArgs e)
    {
        _hideOnClose = false;
        Application.Current.Exit();
    }

    // ── Navigation ─────────────────────────────────────────────────────────

    // Simple ICommand wrapper used only to set TaskbarIcon.DoubleClickCommand.
    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? _) => true;
        public void Execute(object? _)    => execute();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            return;

        var pageType = tag switch
        {
            "NowPlaying" => typeof(NowPlayingPage),
            "Settings"   => typeof(SettingsPage),
            _            => (Type?)null,
        };

        if (pageType is not null)
            ContentFrame.Navigate(pageType);
    }
}
