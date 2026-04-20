// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT;

namespace Whirtle.Client.UI;

public sealed partial class LogsWindow : Window
{
    private MicaController?              _micaController;
    private SystemBackdropConfiguration? _backdropConfig;

    private bool _allowClose;

    public LogsWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);

        RestoreWindowBounds();
        TryApplyMica();

        // Closing the logs window should hide it, not destroy it.
        // Destroying it when MainWindow is hidden to the tray would exit the app
        // because the runtime sees no remaining visible windows.
        // Exception: during app shutdown _allowClose is set so Exit() can proceed.
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            SaveWindowBounds();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeWindow.ShowWindow(hwnd, NativeWindow.SW_HIDE);
        };

        Closed += (_, _) =>
        {
            _micaController?.Dispose();
            _micaController = null;
        };
    }

    internal void AllowClose() => _allowClose = true;

    public void Show()
    {
        Activate();
        // Re-apply the saved position: Activate() calls ShowWindow(SW_SHOWNORMAL)
        // internally, which can reposition the window to a stale "normal" placement
        // rather than where it was when hidden.
        var settings = App.Current.SettingsViewModel;
        if (settings.LogsWindowX is { } x && settings.LogsWindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void RestoreWindowBounds()
    {
        var settings = App.Current.SettingsViewModel;
        var w = settings.LogsWindowWidth  ?? 900;
        var h = settings.LogsWindowHeight ?? 600;
        AppWindow.Resize(new SizeInt32(w, h));
        if (settings.LogsWindowX is { } x && settings.LogsWindowY is { } y)
            AppWindow.Move(new PointInt32(x, y));
    }

    private void SaveWindowBounds()
    {
        var pos  = AppWindow.Position;
        var size = AppWindow.Size;
        App.Current.SettingsViewModel.SaveLogsWindowBounds(pos.X, pos.Y, size.Width, size.Height);
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
