using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT;

namespace Whirtle.Client.UI;

public sealed partial class SettingsWindow : Window
{
    // Kept alive for the lifetime of the window.
    private MicaController?              _micaController;
    private SystemBackdropConfiguration? _backdropConfig;

    public SettingsWindow()
    {
        InitializeComponent();

        ((FrameworkElement)Content).RequestedTheme = ElementTheme.Dark;

        AppWindow.Resize(new SizeInt32(700, 740));
        TryApplyMica();
    }

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
}
