using EEWTelop.Application.Configuration;

namespace EEWTelop.Infrastructure.P2P.Configuration;

public sealed record ProviderOptions(
    ProviderMode Mode,
    Uri WebSocketUri,
    Uri RestBaseUri)
{
    public static ProviderOptions Production { get; } = new(
        ProviderMode.Production,
        new Uri("wss://api.p2pquake.net/v2/ws", UriKind.Absolute),
        new Uri("https://api.p2pquake.net/v2", UriKind.Absolute));

    public static ProviderOptions Sandbox { get; } = new(
        ProviderMode.Sandbox,
        new Uri("wss://api-realtime-sandbox.p2pquake.net/v2/ws", UriKind.Absolute),
        new Uri("https://api-v2-sandbox.p2pquake.net/v2", UriKind.Absolute));

    public static ProviderOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Mode switch
        {
            ProviderMode.Production => Production,
            ProviderMode.Sandbox => Sandbox,
            _ => new ProviderOptions(
                settings.Mode,
                new Uri(settings.WebSocketUrl, UriKind.Absolute),
                new Uri(settings.RestBaseUrl, UriKind.Absolute)),
        };
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (WebSocketUri.Scheme is not ("ws" or "wss"))
        {
            errors.Add("WebSocketUri must use ws or wss.");
        }

        if (RestBaseUri.Scheme is not ("http" or "https"))
        {
            errors.Add("RestBaseUri must use http or https.");
        }

        return errors;
    }
}
