// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
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

        RestoreWindowPosition();
        TryApplyMica();
        ((FrameworkElement)Content).Loaded += (_, _) => SizeToContent();

        // Closing the stats window should hide it, not destroy it, for the same
        // reason as LogsWindow: destroying it when MainWindow is hidden to the tray
        // would exit the app because the runtime sees no remaining visible windows.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            App.Current.NowPlayingViewModel.ClockStats.StopTicker();
            SaveWindowPosition();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeWindow.ShowWindow(hwnd, NativeWindow.SW_HIDE);
        };

        Closed += (_, _) =>
        {
            _micaController?.Dispose();
            _micaController = null;
        };
    }

    public void Show()
    {
        App.Current.NowPlayingViewModel.ClockStats.StartTicker();
        SizeToContent();
        Activate();
        // Re-apply the saved position: Activate() calls ShowWindow(SW_SHOWNORMAL)
        // internally, which can reposition the window to a stale "normal" placement
        // rather than where it was when hidden.
        RestoreWindowPosition();
    }

    private void RestoreWindowPosition()
    {
        var settings = App.Current.SettingsViewModel;
        if (settings.StatsWindowX is { } x && settings.StatsWindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void SizeToContent()
    {
        if (Content is not FrameworkElement root) return;
        var scale        = root.XamlRoot?.RasterizationScale ?? 1.0;
        const int maxLogicalWidth = 450;
        var w = (int)Math.Ceiling(maxLogicalWidth * scale);
        root.Measure(new Size(maxLogicalWidth, double.PositiveInfinity));
        var h = (int)Math.Ceiling(root.DesiredSize.Height * scale) + 8;
        if (h > 0)
            AppWindow.Resize(new SizeInt32(w, h));
    }

    private void SaveWindowPosition()
    {
        var pos = AppWindow.Position;
        App.Current.SettingsViewModel.SaveStatsWindowPosition(pos.X, pos.Y);
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
