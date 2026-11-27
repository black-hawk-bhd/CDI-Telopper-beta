using EEWTelop.Application.Configuration;

namespace EEWTelop.Infrastructure.P2P.Configuration;

public sealed record ProviderOptions(
    ProviderMode Mode,
    Uri WebSocketUri,
    Uri RestBaseUri)
{
    public static ProviderOptions Production { get; } = new(
        ProviderMode.Production,
        new Uri(ProviderSettings.P2pWebSocketEndpoint, UriKind.Absolute),
        new Uri(ProviderSettings.P2pRestBaseEndpoint, UriKind.Absolute));

    public static ProviderOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Mode switch
        {
            ProviderMode.Production => Production,
            ProviderMode.Sandbox => throw new ArgumentException(
                "P2Pのサンドボックス接続は廃止されました。", nameof(settings)),
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
