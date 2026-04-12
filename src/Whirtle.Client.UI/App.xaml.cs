// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Whirtle.Client.Audio;
using Whirtle.Client.UI.Logging;
using Whirtle.Client.UI.ViewModels;


namespace Whirtle.Client.UI;

public partial class App : Application
{
    private MainWindow?         _mainWindow;
    private NowPlayingViewModel? _nowPlayingViewModel;
    private SettingsViewModel?  _settingsViewModel;
    private LogsViewModel?      _logsViewModel;
    private InMemorySink?       _logSink;

    internal static new App Current => (App)Application.Current;

    public NowPlayingViewModel NowPlayingViewModel => _nowPlayingViewModel!;
    public SettingsViewModel   SettingsViewModel   => _settingsViewModel!;
    public LogsViewModel       LogsViewModel       => _logsViewModel!;

    public App()
    {
        InitializeComponent();
        RegisterCrashHandlers();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        // ── Logging ──────────────────────────────────────────────────────────
        _logSink = new InMemorySink();
        AppLogger.Configure(_logSink);
        _logsViewModel = new LogsViewModel(_logSink, dispatcher);

        Log.Information("Whirtle starting up");

        // ── ViewModels ───────────────────────────────────────────────────────
        _settingsViewModel   = new SettingsViewModel();
        _nowPlayingViewModel = new NowPlayingViewModel(
            AudioDeviceEnumerator.Create(),
            dispatcher,
            _settingsViewModel);

        _mainWindow = new MainWindow();
        _mainWindow.Closed += (_, _) =>
        {
            Log.Information("Main window closed; shutting down");
            AppLogger.CloseAndFlush();
        };

        _mainWindow.Activate();
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
