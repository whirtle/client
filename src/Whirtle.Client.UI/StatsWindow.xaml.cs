// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT;

namespace Whirtle.Client.UI;

public sealed partial class StatsWindow : Window
{
    private MicaController?              _micaController;
    private SystemBackdropConfiguration? _backdropConfig;

    public StatsWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);

        RestoreWindowBounds();
        TryApplyMica();

        // Closing the stats window should hide it, not destroy it, for the same
        // reason as LogsWindow: destroying it when MainWindow is hidden to the tray
        // would exit the app because the runtime sees no remaining visible windows.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            App.Current.NowPlayingViewModel.ClockStats.StopTicker();
            SaveWindowBounds();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeWindow.ShowWindow(hwnd, NativeWindow.SW_HIDE);
        };
    }

    public void Show()
    {
        App.Current.NowPlayingViewModel.ClockStats.StartTicker();
        Activate();
        // Re-apply the saved position: Activate() calls ShowWindow(SW_SHOWNORMAL)
        // internally, which can reposition the window to a stale "normal" placement
        // rather than where it was when hidden.
        var settings = App.Current.SettingsViewModel;
        if (settings.StatsWindowX is { } x && settings.StatsWindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void RestoreWindowBounds()
    {
        var settings = App.Current.SettingsViewModel;
        var w = settings.StatsWindowWidth  ?? 380;
        var h = settings.StatsWindowHeight ?? 280;
        AppWindow.Resize(new SizeInt32(w, h));
        if (settings.StatsWindowX is { } x && settings.StatsWindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void SaveWindowBounds()
    {
        var pos  = AppWindow.Position;
        var size = AppWindow.Size;
        App.Current.SettingsViewModel.SaveStatsWindowBounds(pos.X, pos.Y, size.Width, size.Height);
    }

    private void TryApplyMica()
    {
        if (!MicaController.IsSupported())
            return;

        _backdropConfig = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme         = SystemBackdropTheme.Dark,
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
}
