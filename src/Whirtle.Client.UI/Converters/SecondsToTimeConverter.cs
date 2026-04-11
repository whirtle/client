using Microsoft.UI.Xaml.Data;

namespace Whirtle.Client.UI.Converters;

/// <summary>
/// Converts a <see cref="double"/> (elapsed seconds) to a human-readable
/// time string: <c>m:ss</c> for tracks under an hour, <c>h:mm:ss</c> otherwise.
/// </summary>
public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not double seconds || double.IsNaN(seconds) || seconds < 0)
            return "0:00";

        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
