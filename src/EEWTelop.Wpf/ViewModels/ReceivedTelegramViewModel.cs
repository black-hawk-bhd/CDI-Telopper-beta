using EEWTelop.Application.Display;
using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;

namespace EEWTelop.Wpf.ViewModels;

public sealed class ReceivedTelegramViewModel
{
    private IReadOnlyList<ObservationStationReviewViewModel>? _observationStations;

    public ReceivedTelegramViewModel(
        DisasterEvent disasterEvent,
        DisplayProgram program,
        string displayResult = "表示済み")
    {
        Event = disasterEvent;
        Program = program;
        DisplayResult = string.IsNullOrWhiteSpace(displayResult)
            ? "表示済み"
            : displayResult.Trim();
    }

    public DisasterEvent Event { get; }

    public DisplayProgram Program { get; }

    public string DisplayResult { get; }

    public string TelegramType => Event switch
    {
        EewEvent eew => eew.Issue.RawType,
        QuakeEvent quake => quake.Issue.RawType,
        TsunamiEvent tsunami => tsunami.Issue.RawType,
        WeatherWarningEvent weather => weather.Issue.RawType,
        VolcanoEvent volcano => volcano.Issue.RawType,
        _ => string.Empty,
    };

    public string SourceText => Event.SourceMode switch
    {
        SourceMode.Production => "本番受信",
        SourceMode.HistoryRehearsal => "過去電文",
        SourceMode.Sandbox => "テスト",
        SourceMode.ManualTest => "試験電文・訓練",
        _ => Event.SourceMode.ToString(),
    };

    public string DisplayText =>
        $"{Event.ReceivedAt.ToLocalTime():MM/dd HH:mm:ss}  {SourceText}  " +
        $"{GetKindText(Event.Kind)}  {TelegramType}  {Program.Pages.Count}ページ" +
        (DisplayResult == "表示済み" ? string.Empty : $"  [{DisplayResult}]");

    public string DetailText => string.Join(
        Environment.NewLine,
        $"区分: {SourceText}",
        $"情報種別: {GetKindText(Event.Kind)}",
        $"電文コード: {(string.IsNullOrWhiteSpace(TelegramType) ? "—" : TelegramType)}",
        $"発表時刻: {Event.IssuedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
        $"受信時刻: {Event.ReceivedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss}",
        $"提供元: {Event.Provider}",
        $"イベントID: {Event.Id.Value}",
        $"状態: {(Event.IsCancelled ? "解除・取消" : Event.IsCorrection ? "訂正" : "通常")}",
        $"表示結果: {DisplayResult}");

    public IReadOnlyList<TelegramPageReviewViewModel> Pages
    {
        get
        {
            var places = GetReviewPlaces();
            return Program.Pages
                .Select((page, index) => new TelegramPageReviewViewModel(
                    $"ページ {index + 1} / {Program.Pages.Count}",
                    Event is WeatherWarningEvent
                        ? string.Join(Environment.NewLine, page.Blocks.Where(b => b.StyleToken != DisplayStyleTokens.PageIndicator)
                            .Select(b => (b.WeatherHeading.Length > 0 ? b.WeatherHeading + Environment.NewLine : string.Empty) + b.PrimaryText + (b.SecondaryText.Length > 0 ? Environment.NewLine + b.SecondaryText : string.Empty)))
                        : page.AccessibleText,
                    Event is WeatherWarningEvent
                        ? string.Join(" ／ ", page.Blocks.Select(b => b.Badge).Where(b => b.Length > 0).Distinct())
                        : string.Empty) { Places = places })
                .ToArray();
        }
    }

    public IReadOnlyList<ObservationStationReviewViewModel> ObservationStations =>
        _observationStations ??= Event is QuakeEvent quake
            ? GetObservationStations(quake.Points)
            : [];

    public bool HasObservationStations => ObservationStations.Count > 0;

    private ReviewPlace[] GetReviewPlaces() => Event switch
    {
        WeatherWarningEvent weather => weather.Items.Select(i => new ReviewPlace(i.AreaName, i.AreaCode))
            .Concat(weather.RiverFlood?.Districts.Select(d => new ReviewPlace(d.City, d.CityCode)) ?? [])
            .Distinct().ToArray(),
        VolcanoEvent volcano => volcano.TargetAreas.Select(a => new ReviewPlace(a.Name, a.Code)).Distinct().ToArray(),
        QuakeEvent quake => GetQuakeReviewPlaces(quake).Distinct().ToArray(),
        EewEvent eew => GetHypocenterPlaces(eew.Earthquake?.Hypocenter),
        TsunamiEvent tsunami => tsunami.Areas
            .Select(area => area.Role == TsunamiInformationRole.ForecastArea
                ? new ReviewPlace(area.Name, area.Code) { CodeTable = JmaPlaceCodeTable.AreaTsunami }
                : new ReviewPlace(area.ParentAreaName, area.ParentAreaCode) { CodeTable = JmaPlaceCodeTable.AreaTsunami })
            .Where(place => place.Name.Length > 0).Distinct().ToArray(),
        _ => [],
    };

    private static IEnumerable<ReviewPlace> GetQuakeReviewPlaces(QuakeEvent quake)
    {
        foreach (var point in quake.Points)
        {
            string areaName = point.IsArea ? point.Address : point.SeismicAreaName;
            if (areaName.Length > 0)
            {
                yield return SeismicAreaPlace(areaName, point.SeismicAreaCode, point.Prefecture);
            }

            if (GetStationPlace(point) is { } station)
            {
                yield return station;
            }
        }

        foreach (var area in quake.LongPeriodIntensity?.Areas ?? [])
        {
            yield return SeismicAreaPlace(area.Area, prefecture: area.Prefecture);
        }

        foreach (var place in GetHypocenterPlaces(quake.Earthquake.Hypocenter))
        {
            yield return place;
        }
    }

    private static ReviewPlace[] GetHypocenterPlaces(HypocenterInfo? hypocenter) => hypocenter is null
        ? []
        : new[] { hypocenter.Name, hypocenter.ReducedName }
            .Where(name => name.Length > 0).Distinct(StringComparer.Ordinal).Select(name => SeismicAreaPlace(name)).ToArray();

    private static ReviewPlace SeismicAreaPlace(string name, string code = "", string prefecture = "") => new(name, code)
    {
        CodeTable = JmaPlaceCodeTable.AreaForecastLocalE,
        Prefecture = prefecture,
    };

    private static ReviewPlace? GetStationPlace(QuakePoint point)
    {
        if (point.IsArea || string.IsNullOrWhiteSpace(point.Address) ||
            (point.StationCode.Length == 0 && point.Address == point.MunicipalityName))
        {
            return null;
        }

        string name = point.Address.Trim();
        if (name.EndsWith('＊'))
        {
            name = name[..^1].TrimEnd();
        }

        var place = new ReviewPlace(name, point.StationCode)
        {
            CodeTable = JmaPlaceCodeTable.PointSeismicIntensity,
            Prefecture = point.Prefecture,
        };
        return name.Length > 0 && (point.StationCode.Length > 0 || JmaPlaceReadings.Contains(place)) ? place : null;
    }

    private static ObservationStationReviewViewModel[] GetObservationStations(IReadOnlyList<QuakePoint> points)
    {
        var stations = new List<ObservationStationReviewViewModel>();
        foreach (var point in points)
        {
            if (GetStationPlace(point) is not { } place)
            {
                continue;
            }

            string intensity = point.Scale switch
            {
                JmaScale.FiveLowerOrMore => "震度5弱以上 未入電",
                JmaScale.Unknown => "震度不明",
                _ => $"震度{ScaleFormatter.Format(point.Scale)}",
            };
            stations.Add(new ObservationStationReviewViewModel(place.Name, JmaPlaceReadings.Resolve(place), intensity));
        }

        return stations.ToArray();
    }

    private static string GetKindText(EventKind kind) => kind switch
    {
        EventKind.Eew => "緊急地震速報",
        EventKind.Quake => "地震情報",
        EventKind.Tsunami => "津波情報",
        EventKind.WeatherWarning => "気象情報",
        EventKind.Volcano => "火山情報",
        _ => kind.ToString(),
    };
}

public sealed record TelegramPageReviewViewModel(string Header, string Text, string Title = "")
{
    public IReadOnlyList<ReviewPlace> Places { get; init; } = [];
}

public sealed record ObservationStationReviewViewModel(string Name, string? Reading, string Intensity);
