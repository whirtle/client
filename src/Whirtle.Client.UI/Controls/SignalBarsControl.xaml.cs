using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Whirtle.Client.UI.ViewModels;

namespace Whirtle.Client.UI.Controls;

public sealed partial class SignalBarsControl : UserControl
{
    // ── Level property ─────────────────────────────────────────────────────

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(int),
            typeof(SignalBarsControl),
            new PropertyMetadata(0, OnLevelChanged));

    /// <summary>Signal strength level, 0–4 (0 = no signal, 4 = full).</summary>
    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SignalBarsControl)d).UpdateBars();

    // ── TooltipLines property ──────────────────────────────────────────────

    public static readonly DependencyProperty TooltipLinesProperty =
        DependencyProperty.Register(
            nameof(TooltipLines),
            typeof(IReadOnlyList<SignalInputLine>),
            typeof(SignalBarsControl),
            new PropertyMetadata(null, OnTooltipLinesChanged));

    public IReadOnlyList<SignalInputLine>? TooltipLines
    {
        get => (IReadOnlyList<SignalInputLine>?)GetValue(TooltipLinesProperty);
        set => SetValue(TooltipLinesProperty, value);
    }

    private static void OnTooltipLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SignalBarsControl)d).UpdateTooltip();

    // ── Construction ───────────────────────────────────────────────────────

    public SignalBarsControl()
    {
        InitializeComponent();
        UpdateBars();
    }

    // ── Tooltip ────────────────────────────────────────────────────────────

    // A single persistent panel is used as tooltip content; its children are
    // rebuilt in place when the lines change. Re-registering the tooltip on
    // every update would dismiss an already-open popup.
    private readonly StackPanel _tooltipPanel = new()
    {
        Padding  = new Thickness(2, 2, 2, 2),
        Spacing  = 3,
        MinWidth = 160,
    };
    private bool _tooltipAttached;

    private static readonly SolidColorBrush ImpairingBrush = new(Color.FromArgb(0xFF, 0xFF, 0xB0, 0x20));
    private static readonly SolidColorBrush NormalBrush    = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private void UpdateTooltip()
    {
        _tooltipPanel.Children.Clear();

        var lines = TooltipLines;
        if (lines is null || lines.Count == 0)
        {
            if (_tooltipAttached)
            {
                ToolTipService.SetToolTip(this, null);
                _tooltipAttached = false;
            }
            return;
        }

        foreach (var line in lines)
        {
            var brush = line.IsImpairing ? ImpairingBrush : NormalBrush;

            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock { Text = line.Label, FontSize = 11, Opacity = 0.7, Foreground = brush };
            var valueBlock = new TextBlock { Text = line.Value, FontSize = 11, Foreground = brush, HorizontalAlignment = HorizontalAlignment.Right };

            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(labelBlock);
            row.Children.Add(valueBlock);
            _tooltipPanel.Children.Add(row);
        }

        if (!_tooltipAttached)
        {
            ToolTipService.SetToolTip(this, _tooltipPanel);
            _tooltipAttached = true;
        }
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
