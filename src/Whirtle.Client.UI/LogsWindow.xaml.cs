// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

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

    public LogsWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragBar);

        AppWindow.Resize(new SizeInt32(900, 600));
        TryApplyMica();

        // Closing the logs window should hide it, not destroy it.
        // Destroying it when MainWindow is hidden to the tray would exit the app
        // because the runtime sees no remaining visible windows.
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeWindow.ShowWindow(hwnd, NativeWindow.SW_HIDE);
        };
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
