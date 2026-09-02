using System;
using System.Collections.Generic;
using EEWTelop.Application.Configuration;

namespace EEWTelop.Infrastructure.Wolfx.Configuration;

public sealed record WolfxProviderOptions(
    Uri EewWebSocketUri,
    Uri QuakeWebSocketUri,
    bool ReceiveEew,
    bool ReceiveQuake)
{
    public const string ProviderName = "wolfx";

    public static WolfxProviderOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new WolfxProviderOptions(
            new Uri(settings.WolfxEewWebSocketUrl, UriKind.Absolute),
            new Uri(settings.WolfxQuakeWebSocketUrl, UriKind.Absolute),
            settings.Routing.Eew == ReceptionProvider.Wolfx,
            settings.Routing.Quake == ReceptionProvider.Wolfx);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        ValidateUri(EewWebSocketUri, "EEW", errors);
        ValidateUri(QuakeWebSocketUri, "earthquake", errors);
        if (!ReceiveEew && !ReceiveQuake)
        {
            errors.Add("At least one Wolfx information category must be selected.");
        }

        return errors;
    }

    private static void ValidateUri(Uri uri, string category, List<string> errors)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeWss)
        {
            errors.Add($"The Wolfx {category} endpoint must be an absolute wss URL.");
        }
    }
}
