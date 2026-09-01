using System.Globalization;

namespace EEWTelop.Infrastructure.P2P.Normalization;

internal static class P2pDateTimeParser
{
    private static readonly string[] Formats =
    [
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss.FFFFFFF",
        "yyyy/M/d H:mm:ss",
        "yyyy/M/d H:mm:ss.FFFFFFF",
    ];

    private static readonly TimeSpan JapanStandardTimeOffset = TimeSpan.FromHours(9);

    public static bool TryParse(string? value, out DateTimeOffset result)
    {
        if (DateTime.TryParseExact(
                value,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            result = new DateTimeOffset(
                DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified),
                JapanStandardTimeOffset);
            return true;
        }

        result = default;
        return false;
    }
}
