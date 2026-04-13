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
    private bool                 _firewallCheckStarted;

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

        // Networking and firewall setup are handled together by CheckFirewallAsync,
        // which is triggered once the window is ready.  For the FRE path it is
        // triggered again by the AppUiState.Waiting transition below.
        _uiStateService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppUiStateService.CurrentState)
                && _uiStateService.CurrentState == AppUiState.Waiting)
            {
                _ = CheckFirewallAsync();
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

        // Defer until the window is shown so XamlRoot is available for dialogs.
        // Any Activated event (including Deactivated) means the window has been
        // shown; focus state doesn't affect XamlRoot availability.
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
        // During FRE the window isn't usable yet.  Reset the guard so the
        // AppUiState.Waiting handler can re-enter after FRE completes.
        if (_uiStateService!.CurrentState == AppUiState.FirstRun)
            return;

        // Prevent concurrent or duplicate invocations.
        if (_firewallCheckStarted) return;
        _firewallCheckStarted = true;

        // Always start networking at the end, regardless of firewall outcome,
        // so that a skipped or failed dialog doesn't leave the app non-functional.
        try
        {
            if (_settingsViewModel!.ConnectionMode == ConnectionMode.ServerInitiated)
            {
                var port = MdnsAdvertiser.DefaultPort;
                if (!await Task.Run(FirewallHelper.IsRulePresent))
                {
                    // Step 1 — explain before triggering the UAC prompt.
                    // Bring the window to the foreground and omit DefaultButton so
                    // a stray Enter key cannot silently dismiss the dialog.
                    _mainWindow!.Activate();
                    var explainDialog = new ContentDialog
                    {
                        Title             = "Firewall permission needed",
                        Content           = $"Whirtle needs a Windows Firewall rule so Sendspin servers can reach it on port {port}.\n\n" +
                                             "Clicking \"Continue\" will open a Windows administrator prompt — please approve it to add the rule.",
                        PrimaryButtonText = "Continue",
                        CloseButtonText   = "Not now",
                        XamlRoot          = _mainWindow!.Content.XamlRoot,
                    };

                    if (await explainDialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        // Step 2 — trigger the UAC prompt, then loop with a retry
                        // option in case the user accidentally dismissed it.
                        FirewallHelper.AddRule(port);
                        Log.Information("Requested firewall rule for port {Port}", port);

                        while (true)
                        {
                            var waitDialog = new ContentDialog
                            {
                                Title             = "Waiting for administrator prompt",
                                Content           = "Please approve the administrator prompt that appeared.\n\n" +
                                                     "If the prompt was dismissed or did not appear, click \"Try Again\".",
                                PrimaryButtonText = "Try Again",
                                CloseButtonText   = "Done",
                                XamlRoot          = _mainWindow!.Content.XamlRoot,
                            };

                            if (await waitDialog.ShowAsync() != ContentDialogResult.Primary)
                                break;

                            FirewallHelper.AddRule(port);
                            Log.Information("Retrying firewall rule for port {Port}", port);
                        }
                    }
                }
            }
        }
        finally
        {
            MaybeStartServerInitiatedMode();
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
            var ex    = e.Exception;
            var hres  = (ex as System.Runtime.InteropServices.COMException)?.HResult ?? 0;
            Log.Fatal(ex,
                "Unhandled WinUI exception [{Type}] HRESULT=0x{HResult:X8}: {Message}",
                ex?.GetType().FullName ?? "(null)",
                unchecked((uint)hres),
                e.Message);
            e.Handled = true;   // prevent crash; log and keep running where possible
            AppLogger.CloseAndFlush();
        };
    }
}
