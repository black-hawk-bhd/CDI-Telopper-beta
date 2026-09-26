using EEWTelop.Application.Events;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

/// <summary>Provider-neutral, read-only state. No rendering or plugin-specific fields.</summary>
internal sealed class ExternalEarthquakeState
{
    private readonly object _gate = new();
    private QuakeEvent? _quake;
    private readonly Dictionary<string, EewEvent> _eew = new(StringComparer.Ordinal);
    private long _revision;

    public void Observe(EventIngestionResult result)
    {
        if (result.Status != EventIngestionStatus.Accepted || result.Event?.SourceMode != SourceMode.Production) return;
        lock (_gate)
        {
            if (result.Event is QuakeEvent { IsCancelled: true } cancelled && _quake is not null && _quake.Id != cancelled.Id) return;
            if (result.Event is QuakeEvent quake && (_quake is null || quake.IssuedAt >= _quake.IssuedAt))
            {
                _quake = quake;
                _revision++;
            }
            if (result.Event is EewEvent { IsTest: false } eew)
            {
                string id = eew.Id.ToString();
                if (_eew.TryGetValue(id, out var previous) && eew.IssuedAt < previous.IssuedAt) return;
                _eew[id] = eew;
                while (_eew.Count > 100) _eew.Remove(_eew.MinBy(static x => x.Value.IssuedAt).Key);
                _revision++;
            }
        }
    }

    public object ReadQuake()
    {
        lock (_gate)
        {
            return new
            {
                apiVersion = "1", revision = _revision, hasInformation = _quake is not null,
                earthquake = _quake is null ? null : new
                {
                    eventId = _quake.Id.ToString(), provider = _quake.Provider,
                    issuedAt = _quake.IssuedAt, receivedAt = _quake.ReceivedAt,
                    serial = _quake.Issue.Serial, isCancelled = _quake.IsCancelled,
                    isExpired = _quake.IsExpired,
                    informationType = _quake.IssueType.ToString(), sourceMode = "production",
                    earthquake = ProjectEarthquake(_quake.Earthquake),
                    points = _quake.Points.Select(static p => new
                    {
                        name = p.DisplayName, prefecture = p.Prefecture, isArea = p.IsArea,
                        intensity = Scale(p.Scale), stationCode = p.StationCode,
                        latitude = p.Latitude, longitude = p.Longitude,
                        municipalityCode = p.MunicipalityCode, municipalityName = p.MunicipalityName,
                        seismicAreaCode = p.SeismicAreaCode, seismicAreaName = p.SeismicAreaName,
                    }).ToArray(),
                    longPeriodIntensity = _quake.LongPeriodIntensity is null ? null : new
                    {
                        maximumClass = _quake.LongPeriodIntensity.MaximumClass,
                        areas = _quake.LongPeriodIntensity.Areas.Select(static a => new
                        {
                            prefecture = a.Prefecture, area = a.Area, @class = a.Class,
                        }).ToArray(),
                    },
                    headline = _quake.Headline, comment = _quake.FreeFormComment,
                },
            };
        }
    }

    public object ReadEew(DateTimeOffset now)
    {
        lock (_gate)
        {
            return new
            {
                apiVersion = "1", revision = _revision, hasInformation = _eew.Count > 0,
                events = _eew.Values.OrderByDescending(static e => e.IssuedAt).Select(e => new
                {
                    eventId = e.Id.ToString(), provider = e.Provider, issuedAt = e.IssuedAt,
                    receivedAt = e.ReceivedAt, serial = e.Issue.Serial, isCancelled = e.IsCancelled,
                    isWarning = e.IsWarning, isFinal = e.IsFinal, sourceMode = "production",
                    // Expiry is a CDI display lifetime, not an official cancellation.
                    expiresAt = e.IssuedAt.AddMinutes(10), isExpired = e.IsExpired || now >= e.IssuedAt.AddMinutes(10),
                    earthquake = e.Earthquake is null ? null : ProjectEarthquake(e.Earthquake),
                    areas = e.Areas.Select(static a => new
                    {
                        name = a.Name, prefecture = a.Prefecture, intensityFrom = Scale(a.ScaleFrom),
                        intensityTo = Scale(Enum.IsDefined(typeof(JmaScale), a.ScaleTo) ? (JmaScale)a.ScaleTo : JmaScale.Unknown),
                        arrivalTime = a.ArrivalTime,
                    }).ToArray(),
                }).ToArray(),
            };
        }
    }

    private static object ProjectEarthquake(EarthquakeInfo e) => new
    {
        originTime = e.OriginTimeIsKnown ? (DateTimeOffset?)e.OriginTime : null, maximumIntensity = Scale(e.MaximumScale),
        hypocenter = e.Hypocenter is null ? null : new
        {
            name = e.Hypocenter.Name, latitude = e.Hypocenter.Latitude, longitude = e.Hypocenter.Longitude,
            depthKilometers = e.Hypocenter.DepthKilometers, magnitude = e.Hypocenter.Magnitude,
        },
        domesticTsunami = e.DomesticTsunami.ToString(), foreignTsunami = e.ForeignTsunami.ToString(),
    };

    private static string? Scale(JmaScale scale) => scale switch
    {
        JmaScale.Zero => "0", JmaScale.One => "1", JmaScale.Two => "2", JmaScale.Three => "3",
        JmaScale.Four => "4", JmaScale.FiveLower => "5-", JmaScale.FiveLowerOrMore => "5-?",
        JmaScale.FiveUpper => "5+", JmaScale.SixLower => "6-", JmaScale.SixUpper => "6+", JmaScale.Seven => "7",
        _ => null,
    };
}
