using EEWTelop.Application.Configuration;
using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.History;

public sealed record HistoryFetchRequest(
    HistoryApi Api,
    int Limit,
    ProviderSettings Provider)
{
    public DateOnly NiiDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public NiiHistoryContent NiiContent { get; init; } = NiiHistoryContent.QuakeAndTsunami;

    public string NiiReportUrl { get; init; } = string.Empty;

    public string LocalXmlFilePath { get; init; } = string.Empty;
}

public interface IHistoryMessageSource : IAsyncDisposable
{
    Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record HistoryRehearsalLoadResult(
    IReadOnlyList<DisasterEvent> Events,
    int IgnoredCount,
    int InvalidCount);

public interface IHistoryRehearsalLoader : IAsyncDisposable
{
    Task<HistoryRehearsalLoadResult> LoadAsync(
        HistorySettings history,
        ProviderSettings provider,
        CancellationToken cancellationToken = default);
}

public sealed class HistoryRehearsalLoader : IHistoryRehearsalLoader
{
    private readonly IHistoryMessageSource _source;
    private readonly IEventNormalizer _normalizer;

    public HistoryRehearsalLoader(
        IHistoryMessageSource source,
        IEventNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(normalizer);
        _source = source;
        _normalizer = normalizer;
    }

    public async Task<HistoryRehearsalLoadResult> LoadAsync(
        HistorySettings history,
        ProviderSettings provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(provider);
        var request = new HistoryFetchRequest(
            history.Api,
            Math.Clamp(history.Limit, 1, 100),
            provider)
        {
            NiiDate = history.NiiDate,
            NiiContent = history.NiiContent,
            NiiReportUrl = history.NiiReportUrl,
            LocalXmlFilePath = history.LocalXmlFilePath,
        };
        IReadOnlyList<RawProviderMessage> messages = await _source.FetchAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        var events = new List<DisasterEvent>(messages.Count);
        int ignored = 0;
        int invalid = 0;
        foreach (RawProviderMessage message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeResult result = _normalizer.Normalize(message);
            if (result.IsSuccess && result.Event is not null)
            {
                events.Add(result.Event);
            }
            else if (result.Status == NormalizeStatus.Invalid)
            {
                invalid++;
            }
            else
            {
                ignored++;
            }
        }

        DisasterEvent[] normalizedOrder = events
            .OrderBy(static item => item.IssuedAt)
            .ThenBy(static item => item.ReceivedAt)
            .ThenBy(static item => item.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var tsunamiAccumulator = new TsunamiEventStateAccumulator();
        DisasterEvent[] ordered = normalizedOrder
            .Select(item => item is TsunamiEvent tsunami
                ? tsunamiAccumulator.Merge(tsunami)
                : item)
            .ToArray();
        return new HistoryRehearsalLoadResult(ordered, ignored, invalid);
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

public sealed class HistoryMessageSourceRouter : IHistoryMessageSource
{
    private readonly Dictionary<HistoryApi, IHistoryMessageSource> _sources;

    public HistoryMessageSourceRouter(
        IEnumerable<KeyValuePair<HistoryApi, IHistoryMessageSource>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToDictionary(static item => item.Key, static item => item.Value);
    }

    public Task<IReadOnlyList<RawProviderMessage>> FetchAsync(
        HistoryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_sources.TryGetValue(request.Api, out IHistoryMessageSource? source))
        {
            throw new InvalidOperationException(
                $"No history message source is registered for API '{request.Api}'.");
        }

        return source.FetchAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHistoryMessageSource source in _sources.Values.Distinct())
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
