using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

/// <summary>Production reception state, independent of subtitle visibility and test playback.</summary>
public sealed class ExternalApiState
{
    private readonly object _gate = new();
    private TsunamiEvent? _forecast;
    private TsunamiEvent? _observation;
    private long _revision;
    private long _forecastRevision;
    private long _initializationId;
    private ExternalInitialization _initialization = new("notStarted", null, null, false, null);
    private string? _forecastOrigin;

    public (long Id, long ForecastRevision) BeginInitialization(DateTimeOffset now)
    {
        lock (_gate)
        {
            _initialization = new("loading", now, null, false, null);
            _revision++;
            return (++_initializationId, _forecastRevision);
        }
    }

    public void CompleteInitialization((long Id, long ForecastRevision) ticket, TsunamiEvent forecast, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (ticket.Id != _initializationId) return;
            bool apply = forecast.SourceMode == SourceMode.Production &&
                ticket.ForecastRevision == _forecastRevision &&
                (_forecast is null || forecast.IssuedAt > _forecast.IssuedAt);
            if (apply)
            {
                _forecast = forecast;
                _forecastOrigin = "startupSnapshot";
                _forecastRevision++;
            }
            _initialization = _initialization with { State = "ready", CompletedAt = now, Applied = apply, Error = null };
            _revision++;
        }
    }

    public void FailInitialization(long id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (id != _initializationId) return;
            _initialization = _initialization with { State = "failed", CompletedAt = now,
                Error = "Initial tsunami snapshot unavailable; retry in 60 seconds." };
            _revision++;
        }
    }

    public void CancelInitialization()
    {
        lock (_gate)
        {
            _initializationId++;
            if (_initialization.State is "loading" or "failed")
            {
                _initialization = _initialization with { State = "cancelled" };
                _revision++;
            }
        }
    }

    public void Observe(EventIngestionResult result)
    {
        if (result.Status != EventIngestionStatus.Accepted ||
            result.Event is not TsunamiEvent { SourceMode: SourceMode.Production } tsunami) return;

        lock (_gate)
        {
            bool observation = tsunami.Issue.RawType is "VTSE51" or "VTSE52";
            TsunamiEvent? previous = observation ? _observation : _forecast;
            if (previous is not null && tsunami.IssuedAt < previous.IssuedAt) return;
            if (previous is not null && previous.Id != tsunami.Id && tsunami.Issue.InformationType.Contains("取消", StringComparison.Ordinal)) return;
            if (observation) _observation = tsunami;
            else
            {
                _forecast = tsunami;
                _forecastOrigin = "live";
                _forecastRevision++;
            }
            _revision++;
        }
    }

    public ExternalTsunamiSnapshot Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            ExternalTsunamiTelegram? forecast = Project(_forecast, now);
            string forecastState = forecast is null ? "unknown" :
                forecast.IsTelegramCancellation ? "telegramCancelled" :
                forecast.IsExpired ? "expired" :
                forecast.IsCancelled ? "inactive" :
                forecast.Item.Length > 0 ? "active" :
                forecast.Areas.Length == 0 || forecast.Areas.Any(a => a.Grade == "Unknown") ? "unknown" : "inactive";
            return new("1", _revision, _forecast is not null,
                forecast, _forecast is not null && _observation is not null && _forecast.Id != _observation.Id ? null : Project(_observation, now))
            {
                Initialization = _initialization,
                ForecastState = forecastState,
                ForecastOrigin = _forecastOrigin,
            };
        }
    }

    private static ExternalTsunamiTelegram? Project(TsunamiEvent? value, DateTimeOffset now)
    {
        if (value is null) return null;
        // Telegram cancellation is not equivalent to a warning being lifted.
        bool cancellation = value.Issue.InformationType?.Contains("取消", StringComparison.Ordinal) == true;
        bool expired = value.IsExpired || value.ExpireAt is { } expiry && expiry <= now;
        bool observation = value.Issue.RawType is "VTSE51" or "VTSE52";
        IEnumerable<TsunamiArea> areas = value.Areas.Where(a => value.Issue.RawType switch
        {
            "VTSE51" => a.Role is TsunamiInformationRole.CoastalObservation or TsunamiInformationRole.StationForecast,
            "VTSE52" => a.Role == TsunamiInformationRole.OffshoreObservation,
            _ => a.Role == TsunamiInformationRole.ForecastArea,
        });
        return new(value.Id.ToString(), value.Provider, value.Issue.RawType,
            value.IssuedAt, value.ReceivedAt, value.ExpireAt, value.ObservationAsOf,
            value.IsCancelled, cancellation, expired, "production",
            areas.Select(a => new ExternalTsunamiArea(a.Name, a.ParentAreaName,
                a.Role.ToString(), a.Grade.ToString(), a.Immediate,
                a.FirstHeight, a.MaximumHeight, a.HighTideAt)).ToArray(),
            value.IsCancelled || cancellation || expired || observation ? [] : value.Areas
                .Where(a => a.Role == TsunamiInformationRole.ForecastArea &&
                    a.Grade is TsunamiGrade.MajorWarning or TsunamiGrade.Warning or TsunamiGrade.Watch)
                .Select(a => new ExternalMapItem(new(a.Name), a.Grade switch
                {
                    TsunamiGrade.MajorWarning => new("52", "大津波警報"),
                    TsunamiGrade.Warning => new("51", "津波警報"),
                    _ => new ExternalMapKind("62", "津波注意報"),
                })).ToArray());
    }
}

public sealed record ExternalTsunamiSnapshot(string ApiVersion, long Revision, bool HasForecast,
    ExternalTsunamiTelegram? Forecast, ExternalTsunamiTelegram? Observation)
{
    public ExternalInitialization Initialization { get; init; } = new("notStarted", null, null, false, null);
    public string ForecastState { get; init; } = "unknown";
    public string? ForecastOrigin { get; init; }
}
public sealed record ExternalInitialization(string State, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, bool Applied, string? Error);
public sealed record ExternalTsunamiTelegram(string EventId, string Provider, string TelegramType,
    DateTimeOffset IssuedAt, DateTimeOffset ReceivedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? ObservationAsOf, bool IsCancelled, bool IsTelegramCancellation, bool IsExpired,
    string SourceMode, ExternalTsunamiArea[] Areas, ExternalMapItem[] Item);
public sealed record ExternalTsunamiArea(string Name, string ParentAreaName, string Role,
    string Grade, bool Immediate, TsunamiFirstHeight? FirstHeight,
    TsunamiMaximumHeight? MaximumHeight, DateTimeOffset? HighTideAt);
public sealed record ExternalMapItem(ExternalMapArea Area, ExternalMapKind Kind);
public sealed record ExternalMapArea(string Name);
public sealed record ExternalMapKind(string Code, string Name);
