namespace EEWTelop.Application.Events;

public sealed class ProviderRoutingEventNormalizer : IEventNormalizer
{
    private readonly Dictionary<string, IEventNormalizer> _normalizers;

    public ProviderRoutingEventNormalizer(
        IEnumerable<KeyValuePair<string, IEventNormalizer>> normalizers)
    {
        ArgumentNullException.ThrowIfNull(normalizers);
        _normalizers = normalizers.ToDictionary(
            static item => item.Key,
            static item => item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return _normalizers.TryGetValue(raw.Provider, out IEventNormalizer? normalizer)
            ? normalizer.Normalize(raw)
            : NormalizeResult.Ignored(new ValidationIssue(
                "provider",
                $"No event normalizer is registered for provider '{raw.Provider}'.",
                ValidationSeverity.Warning));
    }
}
