// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Serilog;
using Whirtle.Client.Audio;
using Whirtle.Client.Discovery;
using Whirtle.Client.State;
using Whirtle.Client.UI.Logging;
using Whirtle.Client.UI.ViewModels;


namespace Whirtle.Client.UI;

public partial class App : Application
{
    private MainWindow?          _mainWindow;
    private NowPlayingViewModel? _nowPlayingViewModel;
    private SettingsViewModel?   _settingsViewModel;
    private LogsViewModel?       _logsViewModel;
    private InMemorySink?        _logSink;
    private AppUiStateService?   _uiStateService;
    private DispatcherQueue?     _dispatcher;
    private NetworkMonitor?      _networkMonitor;

    internal static new App Current => (App)Application.Current;

    public NowPlayingViewModel NowPlayingViewModel => _nowPlayingViewModel!;
    public SettingsViewModel   SettingsViewModel   => _settingsViewModel!;
    public LogsViewModel       LogsViewModel       => _logsViewModel!;
    public AppUiStateService   UiStateService      => _uiStateService!;

    public App()
    {
        InitializeComponent();
        RegisterCrashHandlers();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // ── Logging ──────────────────────────────────────────────────────────
        _logSink = new InMemorySink();
        AppLogger.Configure(_logSink);
        _logsViewModel = new LogsViewModel(_logSink, _dispatcher);

        Log.Information("Whirtle starting up");

        // ── Command line ─────────────────────────────────────────────────────
        if (Environment.GetCommandLineArgs().Contains("--clean-start"))
        {
            Log.Information("--clean-start: removing persisted settings and restarting clean");
            SettingsViewModel.DeleteSettingsFile();
        }

        // ── ViewModels ───────────────────────────────────────────────────────
        _settingsViewModel   = new SettingsViewModel();
        _nowPlayingViewModel = new NowPlayingViewModel(
            AudioDeviceEnumerator.Create(),
            _dispatcher,
            _settingsViewModel);

        _uiStateService = new AppUiStateService(
            _settingsViewModel.TermsAccepted,
            _nowPlayingViewModel.IsConnected);

        _settingsViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.TermsAccepted))
                _uiStateService.Update(_settingsViewModel.TermsAccepted, _nowPlayingViewModel!.IsConnected);
        };

        _nowPlayingViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.IsConnected))
                _uiStateService.Update(_settingsViewModel!.TermsAccepted, _nowPlayingViewModel.IsConnected);
        };

        // Start networking now if we're past the first-run gate; otherwise wait
        // for the FRE to complete before advertising or accepting connections.
        if (_uiStateService.CurrentState != AppUiState.FirstRun)
            MaybeStartServerInitiatedMode();

        _uiStateService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppUiStateService.CurrentState)
                && _uiStateService.CurrentState == AppUiState.Waiting)
            {
                MaybeStartServerInitiatedMode();
            }
        };

        // ── Power events ─────────────────────────────────────────────────────
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // ── Network monitoring ────────────────────────────────────────────────
        _networkMonitor = new NetworkMonitor();
        _networkMonitor.PreferredAddressChanged += OnPreferredAddressChanged;

        _mainWindow = new MainWindow();
        _mainWindow.Closed += (_, _) =>
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _networkMonitor.PreferredAddressChanged -= OnPreferredAddressChanged;
            _networkMonitor.Dispose();
            Log.Information("Main window closed; shutting down");
            AppLogger.CloseAndFlush();
        };

        _mainWindow.Activate();

        // Defer until the window is fully activated so XamlRoot is available.
        void OnFirstActivated(object sender, WindowActivatedEventArgs e)
        {
            _mainWindow!.Activated -= OnFirstActivated;
            _ = CheckFirewallAsync();
        }
        _mainWindow.Activated += OnFirstActivated;
    }

    private void MaybeStartServerInitiatedMode()
    {
        if (_settingsViewModel!.ConnectionMode == ConnectionMode.ServerInitiated)
            _nowPlayingViewModel!.StartServerInitiatedMode();
    }

    private async Task CheckFirewallAsync()
    {
        if (_settingsViewModel!.ConnectionMode != ConnectionMode.ServerInitiated)
            return;

        var port = MdnsAdvertiser.DefaultPort;
        if (FirewallHelper.IsRulePresent())
            return;

        var dialog = new ContentDialog
        {
            Title             = "Firewall rule required",
            Content           = $"Whirtle needs a Windows Firewall rule to allow incoming connections on port {port}. Add it now?",
            PrimaryButtonText = "Add rule",
            CloseButtonText   = "Not now",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = _mainWindow!.Content.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            FirewallHelper.AddRule(port);
            Log.Information("Requested firewall rule for port {Port}", port);
        }
    }

    // ── Network monitoring ────────────────────────────────────────────────────

    private void OnPreferredAddressChanged(object? sender, string newIp)
    {
        if (_nowPlayingViewModel is null || _dispatcher is null) return;

        _dispatcher.TryEnqueue(async () =>
        {
            try   { await _nowPlayingViewModel.OnNetworkChangedAsync(newIp); }
            catch (Exception ex) { Log.Warning(ex, "Error during network change handling"); }
        });
    }

    // ── Power management ──────────────────────────────────────────────────────

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (_nowPlayingViewModel is null || _dispatcher is null) return;

        if (e.Mode == PowerModes.Suspend)
        {
            _dispatcher.TryEnqueue(async () =>
            {
                try   { await _nowPlayingViewModel.OnSuspendAsync(); }
                catch (Exception ex) { Log.Warning(ex, "Error during suspend handling"); }
            });
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.TryEnqueue(async () =>
            {
                try   { await _nowPlayingViewModel.OnResumeAsync(); }
                catch (Exception ex) { Log.Warning(ex, "Error during resume handling"); }
            });
        }
    }

    // ── Crash reporting ───────────────────────────────────────────────────────

    private void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled exception (IsTerminating={IsTerminating})", e.IsTerminating);
            AppLogger.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled WinUI exception: {Message}", e.Message);
            e.Handled = true;   // prevent crash; log and keep running where possible
            AppLogger.CloseAndFlush();
        };
    }
}
