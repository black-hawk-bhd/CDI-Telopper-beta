using System.Globalization;

namespace EEWTelop.Application.Formatting;

public static class MagnitudeFormatter
{
    public static string Format(double? magnitude, string unavailable = "-")
    {
        return magnitude is > 0 && double.IsFinite(magnitude.Value)
            ? magnitude.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : unavailable;
    }
}
