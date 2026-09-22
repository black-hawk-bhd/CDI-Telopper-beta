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
    public JmaFallbackRouting? FallbackRouting { get; init; }
    private readonly Dictionary<string, ReceptionProvider> _recentProviders = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();

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
        ReceptionProvider selectedProvider = (FallbackRouting?.GetRouting() ?? Volatile.Read(ref _routing))
            .GetProvider(result.Event.Kind, isNankaiTrough);
        if (selectedProvider != actualProvider) return NormalizeResult.Ignored();
        if (FallbackRouting is not null && result.Event.Kind != EventKind.Eew && !raw.IsStateSnapshot)
        {
            string fingerprint = EventSignatureBuilder.BuildCrossProvider(result.Event);
            lock (_recentProviders)
            {
                if (_recentProviders.TryGetValue(fingerprint, out var previous))
                {
                    if (previous != actualProvider) return NormalizeResult.Ignored();
                }
                else
                {
                    _recentProviders.Add(fingerprint, actualProvider);
                    _recentOrder.Enqueue(fingerprint);
                    while (_recentOrder.Count > 2000) _recentProviders.Remove(_recentOrder.Dequeue());
                }
            }
        }
        return result;
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

        if (string.Equals(provider, "jma-xml", StringComparison.OrdinalIgnoreCase))
        {
            receptionProvider = ReceptionProvider.JmaXml;
            return true;
        }

        receptionProvider = default;
        return false;
    }
}
