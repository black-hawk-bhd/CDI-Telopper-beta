using System.Text.RegularExpressions;

namespace EEWTelop.Infrastructure.Logging;

public static partial class LogTextSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return UrlPattern().Replace(value, static match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out Uri? uri))
            {
                return "<redacted-url>";
            }

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "redacted",
                Fragment = string.Empty,
            };
            return builder.Uri.AbsoluteUri;
        });
    }

    [GeneratedRegex("(?i)\\b(?:https?|wss?)://[^\\s<>\\\"']+")]
    private static partial Regex UrlPattern();
}
