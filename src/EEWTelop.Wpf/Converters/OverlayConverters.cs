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
