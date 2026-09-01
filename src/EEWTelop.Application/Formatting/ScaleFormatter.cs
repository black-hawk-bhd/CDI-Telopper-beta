using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Formatting;

public static class ScaleFormatter
{
    public static JmaScale Normalize(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0 || value.Value > 100)
        {
            return JmaScale.Unknown;
        }

        int direct = checked((int)Math.Round(value.Value, MidpointRounding.AwayFromZero));
        if (IsDefined(direct))
        {
            return (JmaScale)direct;
        }

        int scaled = checked((int)Math.Round(value.Value * 10, MidpointRounding.AwayFromZero));
        return IsDefined(scaled) ? (JmaScale)scaled : JmaScale.Unknown;
    }

    public static string Format(JmaScale scale) => scale switch
    {
        JmaScale.Zero => "0",
        JmaScale.One => "1",
        JmaScale.Two => "2",
        JmaScale.Three => "3",
        JmaScale.Four => "4",
        JmaScale.FiveLower => "5弱",
        JmaScale.FiveLowerOrMore => "5弱以上",
        JmaScale.FiveUpper => "5強",
        JmaScale.SixLower => "6弱",
        JmaScale.SixUpper => "6強",
        JmaScale.Seven => "7",
        _ => "?",
    };

    private static bool IsDefined(int value) => value is
        0 or 10 or 20 or 30 or 40 or 45 or 46 or 50 or 55 or 60 or 70;
}
