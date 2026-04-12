using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Whirtle.Client.UI.Controls;

public sealed partial class SignalBarsControl : UserControl
{
    // ── Dependency property ────────────────────────────────────────────────

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(int),
            typeof(SignalBarsControl),
            new PropertyMetadata(0, OnLevelChanged));

    /// <summary>Signal strength level, 0–3 (0 = no signal, 3 = full).</summary>
    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SignalBarsControl)d).UpdateBars();

    // ── Construction ───────────────────────────────────────────────────────

    public SignalBarsControl()
    {
        InitializeComponent();
        UpdateBars();
    }

    // ── Bar painting ───────────────────────────────────────────────────────

    private void UpdateBars()
    {
        var active   = ActiveBrush();
        var inactive = InactiveBrush();

        Bar1.Fill = Level >= 1 ? active : inactive;
        Bar2.Fill = Level >= 2 ? active : inactive;
        Bar3.Fill = Level >= 3 ? active : inactive;
    }

    private SolidColorBrush ActiveBrush()
    {
        // Prefer the theme accent brush; fall back to a hardcoded blue if the
        // resource is not available (e.g. during design-time rendering).
        if (Application.Current?.Resources.TryGetValue(
                "SystemAccentColorBrush", out var obj) == true &&
            obj is SolidColorBrush themeBrush)
            return themeBrush;

        return new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    }

    private SolidColorBrush InactiveBrush()
    {
        if (Application.Current?.Resources.TryGetValue(
                "TextFillColorDisabledBrush", out var obj) == true &&
            obj is SolidColorBrush themeBrush)
            return themeBrush;

        return new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    }
}
