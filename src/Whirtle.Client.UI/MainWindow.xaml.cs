using System.Runtime.InteropServices;
using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT;
using Whirtle.Client.UI.Pages;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE    = 0;
    private const int SW_RESTORE = 9;

    private bool _hideOnClose = false;

    // Kept alive for the lifetime of the window (MicaController requires it).
    private MicaController?              _micaController;
    private SystemBackdropConfiguration? _backdropConfig;

    public NowPlayingViewModel NowPlayingViewModel => App.Current.NowPlayingViewModel;

    public MainWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        // Mica material background (Windows 11 Fluent Design).
        // Uses MicaController directly — compatible with all WinAppSDK 1.1+
        // builds regardless of SDK.BuildTools version.
        TryApplyMica();

        // Extend XAML content into the title bar area
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // White caption buttons to match the dark theme
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor         = Colors.White;
        titleBar.ButtonHoverForegroundColor    = Colors.White;
        titleBar.ButtonPressedForegroundColor  = Colors.White;
        titleBar.ButtonInactiveForegroundColor = Colors.White;

        // Initial window size — sized to fit content
        AppWindow.Resize(new SizeInt32(480, 584));

        // Navigate to Now Playing on launch
        ContentFrame.Navigate(typeof(NowPlayingPage));

        // Intercept close → minimize to tray instead
        AppWindow.Closing += AppWindow_Closing;

        // H.NotifyIcon.WinUI does not support click event attributes in XAML;
        // double-click restore is wired via the DoubleClickCommand DP instead.
        TrayIcon.DoubleClickCommand = new RelayCommand(RestoreFromTray);
    }

    // ── Mica backdrop ──────────────────────────────────────────────────────

    private void TryApplyMica()
    {
        if (!MicaController.IsSupported())
            return; // gracefully skip on Windows 10

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

        // Window.Activated fires for both focus-gained and focus-lost;
        // check WindowActivationState to update IsInputActive accordingly.
        Activated += (_, e) =>
        {
            if (_backdropConfig is not null)
                _backdropConfig.IsInputActive =
                    e.WindowActivationState != WindowActivationState.Deactivated;
        };

        _micaController = new MicaController();
        _micaController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _micaController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    // ── Window close / tray ────────────────────────────────────────────────

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_hideOnClose)
        {
            args.Cancel = true;
            HideToTray();
        }
        else
        {
            Application.Current.Exit();
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
        // CanExecute is always true; CanExecuteChanged is intentionally unused.
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? _) => true;
        public void Execute(object? _)    => execute();
    }

}
