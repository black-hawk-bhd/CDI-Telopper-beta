using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public interface IProviderSelectionConfigurable
{
    void UpdateProviderSelection(ProviderSettings settings);
}

/// <summary>
/// Applies the operator's per-information provider selection after payload
/// normalization.  Nankai trough information shares EventKind.Quake with
/// ordinary earthquake reports, so it is deliberately routed by issue type.
/// </summary>
public sealed class ProviderSelectionEventNormalizer :
    IEventNormalizer,
    IProviderSelectionConfigurable
{
    private readonly IEventNormalizer _inner;
    private ProviderRoutingSettings _routing;

    public ProviderSelectionEventNormalizer(
        IEventNormalizer inner,
        ProviderSettings settings)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(settings);
        _routing = settings.Routing;
    }

    public void UpdateProviderSelection(ProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _routing, settings.Routing);
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        NormalizeResult result = _inner.Normalize(raw);
        if (!result.IsSuccess || result.Event is null ||
            !TryMapProvider(raw.Provider, out ReceptionProvider actualProvider))
        {
            return result;
        }

        bool isNankaiTrough = result.Event is QuakeEvent
        {
            IssueType: QuakeIssueType.NankaiTroughTemporaryInformation,
        };
        ReceptionProvider selectedProvider = Volatile.Read(ref _routing)
            .GetProvider(result.Event.Kind, isNankaiTrough);
        return selectedProvider == actualProvider
            ? result
            : NormalizeResult.Ignored();
    }

    private static bool TryMapProvider(
        string provider,
        out ReceptionProvider receptionProvider)
    {
        if (string.Equals(provider, "p2pquake", StringComparison.OrdinalIgnoreCase))
        {
            receptionProvider = ReceptionProvider.P2pQuake;
            return true;
        }

        if (string.Equals(provider, "dmdata.jp", StringComparison.OrdinalIgnoreCase))
        {
            receptionProvider = ReceptionProvider.Dmdata;
            return true;
        }

        if (string.Equals(provider, "axis", StringComparison.OrdinalIgnoreCase))
        {
            receptionProvider = ReceptionProvider.Axis;
            return true;
        }

        if (string.Equals(provider, "wolfx", StringComparison.OrdinalIgnoreCase))
        {
            receptionProvider = ReceptionProvider.Wolfx;
            return true;
        }

        receptionProvider = default;
        return false;
    }
}
