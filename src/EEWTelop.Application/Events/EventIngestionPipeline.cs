using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;
using System.Diagnostics;

namespace EEWTelop.Application.Events;

public sealed class EventIngestionPipeline
{
    private readonly IEventNormalizer _normalizer;
    private readonly IEventVersionCache _versionCache;
    private readonly IPageComposer _pageComposer;
    private readonly IDisplayCoordinator _displayCoordinator;
    private readonly ConcurrentEewProgramComposer _concurrentEewComposer = new();
    private readonly TsunamiEventStateAccumulator _tsunamiStateAccumulator = new();
    private DisplaySettings _settings;
    private FilterSettings _filter;
    private int _holdBeforeDisplay;

    public bool HoldBeforeDisplay
    {
        get => Volatile.Read(ref _holdBeforeDisplay) != 0;
        set => Volatile.Write(ref _holdBeforeDisplay, value ? 1 : 0);
    }

    public EventIngestionPipeline(
        IEventNormalizer normalizer,
        IEventVersionCache versionCache,
        IPageComposer pageComposer,
        IDisplayCoordinator displayCoordinator,
        DisplaySettings settings,
        FilterSettings? filter = null)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(versionCache);
        ArgumentNullException.ThrowIfNull(pageComposer);
        ArgumentNullException.ThrowIfNull(displayCoordinator);
        ArgumentNullException.ThrowIfNull(settings);
        _normalizer = normalizer;
        _versionCache = versionCache;
        _pageComposer = pageComposer;
        _displayCoordinator = displayCoordinator;
        _settings = settings;
        _filter = filter ?? new FilterSettings(true, true, true);
    }

    public EventIngestionResult Process(RawProviderMessage raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        string traceId = Guid.NewGuid().ToString("N");
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        EventIngestionResult result = ProcessCore(raw, out TimeSpan normalizationElapsed);
        stopwatch.Stop();
        return result with
        {
            TraceId = traceId,
            ProcessingStartedAtUtc = startedAtUtc,
            NormalizationCompletedAtUtc = startedAtUtc + normalizationElapsed,
            ProcessingCompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessingMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        };
    }

    private EventIngestionResult ProcessCore(RawProviderMessage raw, out TimeSpan normalizationElapsed)
    {
        var normalizationStopwatch = Stopwatch.StartNew();
        NormalizeResult normalized = _normalizer.Normalize(raw);
        normalizationStopwatch.Stop();
        normalizationElapsed = normalizationStopwatch.Elapsed;
        if (!normalized.IsSuccess || normalized.Event is null)
        {
            EventIngestionStatus status = normalized.Status == NormalizeStatus.Invalid
                ? EventIngestionStatus.Invalid
                : EventIngestionStatus.Ignored;
            return CreateResult(
                raw,
                status,
                null,
                null,
                null,
                normalized.Issues);
        }

        DisasterEvent receivedEvent = normalized.Event;
        if (IsLegacyAxisWeatherTelegram(receivedEvent))
        {
            return CreateResult(
                raw,
                EventIngestionStatus.Ignored,
                receivedEvent,
                null,
                null,
                normalized.Issues,
                "AXIS旧形式気象電文");
        }

        if (!_versionCache.TryAccept(receivedEvent))
        {
            return CreateResult(
                raw,
                EventIngestionStatus.Duplicate,
                receivedEvent,
                null,
                null,
                normalized.Issues);
        }

        DisasterEvent disasterEvent = receivedEvent is TsunamiEvent tsunami
            ? _tsunamiStateAccumulator.Merge(tsunami)
            : receivedEvent;

        DisasterEvent? displayEvent = EventDisplayFilter.Apply(_filter, disasterEvent);
        if (displayEvent is null)
        {
            return CreateResult(
                raw,
                EventIngestionStatus.Accepted,
                disasterEvent,
                null,
                null,
                normalized.Issues,
                EventDisplayFilter.DescribeSuppression(_filter, disasterEvent));
        }

        DisplayProgram program = _pageComposer.Compose(displayEvent, _settings);
        if (displayEvent is EewEvent eew)
        {
            program = _concurrentEewComposer.Compose(eew, program, _settings);
        }
        if (HoldBeforeDisplay)
        {
            return CreateResult(
                raw,
                EventIngestionStatus.Accepted,
                disasterEvent,
                program,
                null,
                normalized.Issues,
                null,
                CountItems(displayEvent)) with
            {
                AwaitingPreDisplayEdit = true,
            };
        }
        CoordinatorSnapshot snapshot = _displayCoordinator.Apply(program);
        return CreateResult(
            raw,
            EventIngestionStatus.Accepted,
            disasterEvent,
            program,
            snapshot,
            normalized.Issues,
            null,
            CountItems(displayEvent));
    }

    private static EventIngestionResult CreateResult(
        RawProviderMessage raw,
        EventIngestionStatus status,
        DisasterEvent? disasterEvent,
        DisplayProgram? program,
        CoordinatorSnapshot? snapshot,
        IReadOnlyList<ValidationIssue> issues) =>
        CreateResult(
            raw,
            status,
            disasterEvent,
            program,
            snapshot,
            issues,
            null,
            program is null ? 0 : CountItems(disasterEvent));

    private static EventIngestionResult CreateResult(
        RawProviderMessage raw,
        EventIngestionStatus status,
        DisasterEvent? disasterEvent,
        DisplayProgram? program,
        CoordinatorSnapshot? snapshot,
        IReadOnlyList<ValidationIssue> issues,
        string? displaySuppressionReason,
        int displayedItemCount = 0) =>
        new(status, disasterEvent, program, snapshot, issues)
        {
            DisplaySuppressionReason = displaySuppressionReason,
            NormalizedItemCount = CountItems(disasterEvent),
            DisplayedItemCount = displayedItemCount,
            UnknownWeatherItemCount = disasterEvent is WeatherWarningEvent weather
                ? weather.Items.Count(static item =>
                    item.Level == WeatherWarningLevel.Unknown)
                : 0,
            ReceptionSummary = ReceptionLogSummary.Create(
                raw,
                disasterEvent,
                status,
                program is not null,
                displaySuppressionReason,
                CountItems(disasterEvent),
                displayedItemCount,
                disasterEvent is WeatherWarningEvent warning
                    ? warning.Items.Count(static item =>
                        item.Level == WeatherWarningLevel.Unknown)
                    : 0),
        };

    private static int CountItems(DisasterEvent? disasterEvent) => disasterEvent switch
    {
        WeatherWarningEvent weather => weather.Items.Count,
        TsunamiEvent tsunami => tsunami.Areas.Count,
        QuakeEvent quake => quake.Points.Count,
        EewEvent eew => eew.Areas.Count,
        VolcanoEvent volcano => volcano.TargetAreas.Count,
        null => 0,
        _ => 1,
    };

    private static bool IsLegacyAxisWeatherTelegram(DisasterEvent disasterEvent) =>
        disasterEvent is WeatherWarningEvent weather &&
        weather.Provider.Equals("axis", StringComparison.OrdinalIgnoreCase) &&
        (weather.Issue.RawType.Trim().Equals("VPWW53", StringComparison.OrdinalIgnoreCase) ||
            weather.Issue.RawType.Trim().Equals("VPWW54", StringComparison.OrdinalIgnoreCase));

    public void UpdateSettings(DisplaySettings settings, FilterSettings? filter = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        if (filter is not null)
        {
            _filter = filter;
        }
        _displayCoordinator.UpdateSettings(settings);
    }

    public void ClearTransientState()
    {
        _concurrentEewComposer.Clear();
        _tsunamiStateAccumulator.Clear();
    }

    public void ClearTransientState(EventKind kind)
    {
        if (kind == EventKind.Eew)
        {
            _concurrentEewComposer.Clear();
        }

        if (kind == EventKind.Tsunami)
        {
            _tsunamiStateAccumulator.Clear();
        }
    }
}

public enum EventIngestionStatus
{
    Accepted = 0,
    Duplicate,
    Ignored,
    Invalid,
}

public sealed record EventIngestionResult(
    EventIngestionStatus Status,
    DisasterEvent? Event,
    DisplayProgram? Program,
    CoordinatorSnapshot? Snapshot,
    IReadOnlyList<ValidationIssue> Issues)
{
    public ReceptionLogSummary? ReceptionSummary { get; init; }

    public string? DisplaySuppressionReason { get; init; }

    public int NormalizedItemCount { get; init; }

    public int DisplayedItemCount { get; init; }

    public int UnknownWeatherItemCount { get; init; }

    public bool AwaitingPreDisplayEdit { get; init; }

    public string TraceId { get; init; } = string.Empty;

    public DateTimeOffset ProcessingStartedAtUtc { get; init; }

    public DateTimeOffset NormalizationCompletedAtUtc { get; init; }

    public DateTimeOffset ProcessingCompletedAtUtc { get; init; }

    public double ProcessingMilliseconds { get; init; }
}
