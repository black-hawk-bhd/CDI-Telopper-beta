using System.Text.Json;
using EEWTelop.Application.Events;

namespace EEWTelop.Infrastructure.P2P.Transport;

/// <summary>
/// Assigns P2P messages used while AXIS is selected. P2P supplies ordinary
/// earthquake information and code 556 EEW warnings as an independent path.
/// </summary>
public static class P2pHybridRoutingPolicy
{
    public static bool IsEarthquakeOrEew(RawProviderMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!string.Equals(message.Provider, "p2pquake", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(message.Payload);
            if (!document.RootElement.TryGetProperty("code", out JsonElement code) ||
                !code.TryGetInt32(out int value))
            {
                return false;
            }

            return value is 551 or 556;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
