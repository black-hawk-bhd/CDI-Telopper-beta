using EEWTelop.Application.Configuration;
using EEWTelop.Infrastructure.Axis.Security;

namespace EEWTelop.Infrastructure.Axis.Configuration;

public sealed record AxisProviderOptions(
    Uri ApiBaseUri,
    string AccessToken,
    string Channel)
{
    public const string ProviderName = "axis";
    public const string SeismologyChannel = "jmx-seismology";
    public const string MeteorologyChannel = "jmx-meteorology";
    public const string VolcanologyChannel = "jmx-volcanology";
    public const string EewChannel = "eew";
    public const string DefaultChannel =
        SeismologyChannel + "," + MeteorologyChannel + "," + VolcanologyChannel + "," +
        EewChannel;

    public static IReadOnlyList<string> SupportedChannels { get; } =
        [SeismologyChannel, MeteorologyChannel, VolcanologyChannel, EewChannel];

    public static AxisProviderOptions FromSettings(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string selectedChannels = BuildSelectedChannels(settings.Routing);
        return new AxisProviderOptions(
            new Uri(EnsureTrailingSlash(settings.AxisApiBaseUrl), UriKind.Absolute),
            AxisCredentialProtector.Unprotect(settings.AxisProtectedAccessToken),
            !string.IsNullOrWhiteSpace(selectedChannels)
                ? selectedChannels
                : string.IsNullOrWhiteSpace(settings.AxisChannel)
                    ? DefaultChannel
                    : settings.AxisChannel.Trim());
    }

    public static string BuildSelectedChannels(ProviderRoutingSettings routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var channels = new List<string>(4);
        if (routing.Quake == ReceptionProvider.Axis ||
            routing.Tsunami == ReceptionProvider.Axis ||
            routing.NankaiTrough == ReceptionProvider.Axis)
        {
            channels.Add(SeismologyChannel);
        }
        if (routing.Weather == ReceptionProvider.Axis)
        {
            channels.Add(MeteorologyChannel);
        }
        if (routing.Volcano == ReceptionProvider.Axis)
        {
            channels.Add(VolcanologyChannel);
        }
        if (routing.Eew == ReceptionProvider.Axis)
        {
            channels.Add(EewChannel);
        }

        return string.Join(",", channels);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ApiBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("AXIS API URL must use https.");
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            errors.Add("AXIS access token is required.");
        }

        string[] channels = ParseChannels(Channel);
        if (channels.Length == 0)
        {
            errors.Add("AXIS channel is required.");
        }
        else
        {
            string[] unsupported = channels
                .Where(channel => !SupportedChannels.Contains(
                    channel,
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (unsupported.Length > 0)
            {
                errors.Add(
                    $"CDI-Telopper supports only the AXIS {SeismologyChannel}, " +
                    $"{MeteorologyChannel}, {VolcanologyChannel}, and {EewChannel} channels. " +
                    $"Unsupported: {string.Join(", ", unsupported)}.");
            }
        }

        return errors;
    }

    public bool AcceptsChannel(string channel) => ParseChannels(Channel)
        .Contains(channel, StringComparer.OrdinalIgnoreCase);

    public static string[] ParseChannels(string? value) => (value ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string EnsureTrailingSlash(string value) =>
        value.Trim().TrimEnd('/') + "/";
}
