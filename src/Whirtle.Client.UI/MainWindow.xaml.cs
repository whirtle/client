using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT;
using Whirtle.Client.Discovery;
using Whirtle.Client.State;
using Whirtle.Client.UI.Pages;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI;

public sealed partial class MainWindow : Window
{
    private bool _hideOnClose;
    private bool _isShuttingDown;

    // Kept alive for the lifetime of the window (MicaController requires it).
    private MicaController?              _micaController;
    private SystemBackdropConfiguration? _backdropConfig;

    // Server picker flyout — built in code to avoid XAML DataTemplate type-resolution
    // issues with ItemsRepeater + project-local types in the WinUI 1.5 XAML compiler.
    private Flyout? _serverPickerFlyout;

    public NowPlayingViewModel NowPlayingViewModel => App.Current.NowPlayingViewModel;
    public AppUiStateService   UiStateService      => App.Current.UiStateService;

    // ── Scrim visibility functions (used by x:Bind in XAML) ──────────────────

    public Visibility StatusBarVisibility(AppUiState state)
        => state != AppUiState.FirstRun ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FreScrimVisibility(AppUiState state)
        => state == AppUiState.FirstRun ? Visibility.Visible : Visibility.Collapsed;


    public MainWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        TryApplyMica();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonForegroundColor         = Colors.White;
        titleBar.ButtonHoverForegroundColor    = Colors.White;
        titleBar.ButtonPressedForegroundColor  = Colors.White;
        titleBar.ButtonInactiveForegroundColor = Colors.White;

        // Prevent manual resizing and maximising; window has a fixed logical size.
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.IsResizable   = false;
            op.IsMaximizable = false;
        }

        RestoreWindowPosition();

        // Resize once after Activate() places the window on a monitor — that
        // is the earliest point at which XamlRoot.RasterizationScale is valid
        // and we can compute the correct physical pixel count for the target
        // logical size.  The pattern mirrors what App.xaml.cs does for the
        // firewall check.
        void OnFirstActivated(object sender, WindowActivatedEventArgs e)
        {
            Activated -= OnFirstActivated;
            var scale = ((FrameworkElement)Content).XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32(
                (int)Math.Ceiling(400 * scale),
                (int)Math.Ceiling(550 * scale)));
        }
        Activated += OnFirstActivated;

        ContentFrame.Navigate(typeof(NowPlayingPage));

        AppWindow.Closing += AppWindow_Closing;

        TrayIcon.DoubleClickCommand = new RelayCommand(RestoreFromTray);
    }

    // ── Mica backdrop ──────────────────────────────────────────────────────

    private void TryApplyMica()
    {
        if (!MicaController.IsSupported())
            return;

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = SystemBackdropTheme.Dark,
        };

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

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPosition();

        if (_hideOnClose)
        {
            args.Cancel = true;
            HideToTray();
        }
        else if (!_isShuttingDown)
        {
            // Defer the close so we can await async cleanup before Exit().
            args.Cancel    = true;
            _isShuttingDown = true;

            // Dispose Mica before the XAML compositor tears down; otherwise the
            // MicaController tries to release its DirectComposition target after
            // the backing objects are gone, causing an access violation (0xc0000005).
            _micaController?.Dispose();
            _micaController = null;

            // Await full teardown so the WASAPI renderer is stopped and disposed
            // before the process exits.  Without this, the audio thread can still
            // be running when WinUI tears down, causing an access violation.
            await App.Current.NowPlayingViewModel.ShutdownAsync();

            _logsWindow?.AllowClose();
            _logsWindow?.Close();
            Application.Current.Exit();
        }
    }

    private void RestoreWindowPosition()
    {
        var settings = App.Current.SettingsViewModel;
        if (settings.WindowX is { } x && settings.WindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void SaveWindowPosition()
    {
        var pos = AppWindow.Position;
        App.Current.SettingsViewModel.SaveWindowPosition(pos.X, pos.Y);
    }

    private void HideToTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeWindow.ShowWindow(hwnd, NativeWindow.SW_HIDE);
    }

    private void RestoreFromTray()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeWindow.ShowWindow(hwnd, NativeWindow.SW_RESTORE);
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
        // Clear _hideOnClose so AppWindow_Closing proceeds with shutdown rather
        // than minimising to tray, then trigger the normal close path so that
        // async cleanup (WASAPI disposal, etc.) runs before the process exits.
        _hideOnClose = false;
        this.Close();
    }

    // ── Server picker flyout (built in code) ──────────────────────────────

    private static string FormatServerLabel(string displayName, string? serverId)
        => serverId is { Length: >= 4 } id ? $"{displayName} ({id[^4..]})" : displayName;

    private void ServerPickerButton_Click(object sender, RoutedEventArgs e)
    {
        _serverPickerFlyout ??= new Flyout { Placement = FlyoutPlacementMode.Top };
        _serverPickerFlyout.Content = BuildServerPickerPanel();
        _serverPickerFlyout.ShowAt(ServerPickerButton);
    }

    private UIElement BuildServerPickerPanel()
    {
        var panel = new StackPanel { MinWidth = 220, Spacing = 2 };

        var vm          = NowPlayingViewModel;
        var currentName = vm.ServerName;
        var autoMode    = App.Current.SettingsViewModel.ConnectionMode == ConnectionMode.ServerInitiated;

        // Automatically connect
        panel.Children.Add(CreatePickerButton(
            "Automatically connect", "\uEC05", AutoConnect_Click, isChecked: autoMode && currentName is null));

        // Discovered servers
        var discovered = vm.DiscoveredServers;
        if (discovered.Count > 0)
        {
            panel.Children.Add(CreatePickerSectionHeader("Discovered"));
            foreach (var ep in discovered)
            {
                var savedMatch = vm.SavedServers.FirstOrDefault(s => s.Host == ep.Host && s.Port == ep.Port);
                var btn = CreatePickerButton(
                    FormatServerLabel(ep.DisplayName, savedMatch?.ServerId), "\uECA5", DiscoveredServer_Click,
                    isChecked: currentName == ep.DisplayName);
                btn.Tag = ep;
                panel.Children.Add(btn);
            }
        }

        // Saved servers
        var saved = vm.SavedServers;
        if (saved.Count > 0)
        {
            panel.Children.Add(CreatePickerSectionHeader("Saved"));
            foreach (var s in saved)
                panel.Children.Add(CreateSavedServerRow(s, isChecked: currentName == s.DisplayName));
        }

        // Separator
        panel.Children.Add(new Border
        {
            Height     = 1,
            Margin     = new Thickness(0, 4, 0, 4),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        });

        // Add connection
        panel.Children.Add(CreatePickerButton("Add connection...", "\uE710", AddConnection_Click));

        return panel;
    }

    private static Button CreatePickerButton(
        string label, string glyph, RoutedEventHandler click, bool isChecked = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (isChecked)
            row.Children.Add(new FontIcon
            {
                Glyph             = "\uE73E", // Checkmark
                FontSize          = 12,
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

        var btn = new Button
        {
            Background                 = new SolidColorBrush(Colors.Transparent),
            BorderThickness            = new Thickness(0),
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding                    = new Thickness(8, 6, 8, 6),
            CornerRadius               = new CornerRadius(4),
            Content                    = row,
        };
        btn.Click += click;
        return btn;
    }

    private static TextBlock CreatePickerSectionHeader(string title)
        => new TextBlock
        {
            Text     = title.ToUpperInvariant(),
            Margin   = new Thickness(8, 8, 0, 2),
            FontSize = 10,
            Opacity  = 0.6,
        };

    private UIElement CreateSavedServerRow(PersistedServer saved, bool isChecked = false)
    {
        var grid = new Grid { ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var serverBtn = CreatePickerButton(FormatServerLabel(saved.DisplayName, saved.ServerId), "\uECA5", SavedServer_Click, isChecked);
        serverBtn.Tag = saved;
        Grid.SetColumn(serverBtn, 0);

        var deleteBtn = new Button
        {
            Background      = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding         = new Thickness(6),
            CornerRadius    = new CornerRadius(4),
            Tag             = saved,
            Content         = new FontIcon { Glyph = "\uE711", FontSize = 11 },
        };
        ToolTipService.SetToolTip(deleteBtn, "Remove this server");
        deleteBtn.Click += DeleteServer_Click;
        Grid.SetColumn(deleteBtn, 1);

        grid.Children.Add(serverBtn);
        grid.Children.Add(deleteBtn);
        return grid;
    }

    // ── Flyout action handlers ─────────────────────────────────────────────

    private void AutoConnect_Click(object sender, RoutedEventArgs e)
    {
        _serverPickerFlyout?.Hide();
        _ = NowPlayingViewModel.SetAutoConnectModeCommand.ExecuteAsync(null);
    }

    private void DiscoveredServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ServiceEndpoint ep }) return;
        _serverPickerFlyout?.Hide();
        _ = NowPlayingViewModel.ConnectCommand.ExecuteAsync(ep);
    }

    private void SavedServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PersistedServer saved }) return;
        _serverPickerFlyout?.Hide();
        _ = NowPlayingViewModel.ConnectToSavedCommand.ExecuteAsync(saved);
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PersistedServer saved }) return;
        _serverPickerFlyout?.Hide();
        await ConfirmDeleteServerAsync(saved);
    }

    private async void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        _serverPickerFlyout?.Hide();
        await ShowAddConnectionDialogAsync();
    }

    // ── Dialogs ────────────────────────────────────────────────────────────

    private async Task ShowAddConnectionDialogAsync()
    {
        var textBox = new TextBox
        {
            PlaceholderText = "hostname or IP address",
            MinWidth        = 280,
        };

        var dialog = new ContentDialog
        {
            Title             = "Add connection",
            Content           = textBox,
            PrimaryButtonText = "Add",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var input = textBox.Text.Trim();
            if (!string.IsNullOrEmpty(input))
                await NowPlayingViewModel.AddSavedServerAsync(input);
        }
    }

    private async Task ConfirmDeleteServerAsync(PersistedServer saved)
    {
        var dialog = new ContentDialog
        {
            Title             = "Remove server?",
            Content           = $"Remove \"{saved.Label}\" from saved servers?",
            PrimaryButtonText = "Remove",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            NowPlayingViewModel.RemoveSavedServer(saved);
    }

    // ── First-run scrim ────────────────────────────────────────────────────

    private void TermsCheckBox_Changed(object sender, RoutedEventArgs e)
        => FreAcceptButton.IsEnabled = TermsCheckBox.IsChecked == true;

    private void FreAcceptButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Current.SettingsViewModel;
        settings.TelemetryConsent = TelemetryCheckBox.IsChecked == true;
        settings.TermsAccepted    = true; // triggers CommitNow() and AppUiState transition
    }

    private void FreDeclineButton_Click(object sender, RoutedEventArgs e)
        => Application.Current.Exit();

    // ── Settings / Logs windows ────────────────────────────────────────────

    private SettingsWindow? _settingsWindow;
    private LogsWindow?     _logsWindow;

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

    private void LogsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _logsWindow ??= new LogsWindow();
        _logsWindow.Show();
        args.Handled = true;
    }

    private void StatsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        App.Current.StatsWindow.Show();
        args.Handled = true;
    }

    // ── Navigation ─────────────────────────────────────────────────────────

    // Simple ICommand wrapper used only to set TaskbarIcon.DoubleClickCommand.
    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? _) => true;
        public void Execute(object? _)    => execute();
    }
}
