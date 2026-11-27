using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Wpf.Converters;

public sealed class BackgroundModeToBrushConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => value switch
    {
        BackgroundMode.Green => Brushes.Lime,
        BackgroundMode.Blue => Brushes.Blue,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EewLinePositionConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (values is not [FontFamily font, double fontSize, double lineHeight] ||
            !double.IsFinite(lineHeight) || font.LineSpacing <= 0)
        {
            return Transform.Identity;
        }

        // BlockLineHeight scales the WPF baseline. CSS splits extra leading above and below the glyphs.
        double extraLeading = lineHeight - fontSize * font.LineSpacing;
        double baselineOffset = extraLeading * (0.5 - font.Baseline / font.LineSpacing);
        var transform = new TranslateTransform(0, baselineOffset);
        transform.Freeze();
        return transform;
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
