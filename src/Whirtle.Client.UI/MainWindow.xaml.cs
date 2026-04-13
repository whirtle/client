using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _hideOnClose = false;

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

    public Visibility WaitingScrimVisibility(AppUiState state)
        => state == AppUiState.Waiting ? Visibility.Visible : Visibility.Collapsed;

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

        AppWindow.Resize(new SizeInt32(480, 584));
        RestoreWindowPosition();

        ContentFrame.Navigate(typeof(NowPlayingPage));

        LicenseTextBlock.Text = LoadLicenseText();

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

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPosition();

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
        _hideOnClose = false;
        Application.Current.Exit();
    }

    // ── Server picker flyout (built in code) ──────────────────────────────

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
                var btn = CreatePickerButton(
                    ep.DisplayName, "\uECA5", DiscoveredServer_Click,
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
                panel.Children.Add(CreateSavedServerRow(s, isChecked: currentName == s.Label));
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

        var serverBtn = CreatePickerButton(saved.Label, "\uECA5", SavedServer_Click, isChecked);
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

    private static string LoadLicenseText()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "LICENSE");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        catch { /* best-effort */ }

        // Fallback: standard GPLv3 program notice.
        return
            "Whirtle is free software: you can redistribute it and/or modify it " +
            "under the terms of the GNU General Public License as published by the " +
            "Free Software Foundation, either version 3 of the License, or (at your " +
            "option) any later version.\n\n" +
            "This program is distributed in the hope that it will be useful, but " +
            "WITHOUT ANY WARRANTY; without even the implied warranty of " +
            "MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU " +
            "General Public License for more details.\n\n" +
            "You should have received a copy of the GNU General Public License " +
            "along with this program. If not, see https://www.gnu.org/licenses/.";
    }

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

    // ── Waiting scrim ───────────────────────────────────────────────────────

    private bool _waitingVolumeChanging;

    private void WaitingVolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_waitingVolumeChanging) return;
        _waitingVolumeChanging = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await NowPlayingViewModel.SetVolumeCommand.ExecuteAsync(e.NewValue / 100.0);
            _waitingVolumeChanging = false;
        });
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
