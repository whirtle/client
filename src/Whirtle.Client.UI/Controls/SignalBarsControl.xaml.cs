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
        if (Level == 0)
        {
            BarsPanel.Visibility = Visibility.Collapsed;
            DotsPanel.Visibility = Visibility.Visible;
            return;
        }

        BarsPanel.Visibility = Visibility.Visible;
        DotsPanel.Visibility = Visibility.Collapsed;

        var active   = Level == 1 ? LowSignalBrush() : NormalSignalBrush();
        var inactive = InactiveBrush();

        Bar1.Fill = Level >= 1 ? active : inactive;
        Bar2.Fill = Level >= 2 ? active : inactive;
        Bar3.Fill = Level >= 3 ? active : inactive;
        Bar4.Fill = Level >= 4 ? active : inactive;
    }

    private static SolidColorBrush NormalSignalBrush()
        => new(Color.FromArgb(0xFF, 0x9B, 0x72, 0xFF));

    private static SolidColorBrush LowSignalBrush()
        => new(Color.FromArgb(0xFF, 0xFF, 0xB0, 0x20));

    private SolidColorBrush InactiveBrush()
    {
        if (Application.Current?.Resources.TryGetValue(
                "TextFillColorDisabledBrush", out var obj) == true &&
            obj is SolidColorBrush themeBrush)
            return themeBrush;

        return new SolidColorBrush(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF));
    }
}
