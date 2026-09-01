using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

/// <summary>
/// Applies an edition-level allow list after provider payload normalization.
/// This keeps unsupported event kinds out of every downstream path, including
/// live reception, history rehearsal, and the isolated raw-data test library.
/// </summary>
public sealed class EventKindFilteringEventNormalizer : IEventNormalizer
{
    private readonly IEventNormalizer _inner;
    private readonly HashSet<EventKind> _allowedKinds;

    public EventKindFilteringEventNormalizer(
        IEventNormalizer inner,
        IEnumerable<EventKind> allowedKinds)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(allowedKinds);

        _inner = inner;
        _allowedKinds = allowedKinds.ToHashSet();
    }

    public NormalizeResult Normalize(RawProviderMessage raw)
    {
        NormalizeResult result = _inner.Normalize(raw);
        if (!result.IsSuccess || result.Event is null || _allowedKinds.Contains(result.Event.Kind))
        {
            return result;
        }

        return NormalizeResult.Ignored(new ValidationIssue(
            "event.kind",
            $"Event kind '{result.Event.Kind}' is not available in this edition.",
            ValidationSeverity.Warning));
    }
}
