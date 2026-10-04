using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public static class EventDisplayFilter
{
    public static DisasterEvent? Apply(FilterSettings filter, DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(disasterEvent);
        if (disasterEvent is QuakeEvent quake)
        {
            return FilterQuakeInformation(filter, quake);
        }
        if (disasterEvent is not WeatherWarningEvent weather)
        {
            return IsEnabled(filter, disasterEvent) ? disasterEvent : null;
        }

        return FilterWeatherInformation(filter, weather);
    }

    public static bool IsEnabled(FilterSettings filter, DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(disasterEvent);
        return disasterEvent switch
        {
            EewEvent => filter.Eew,
            QuakeEvent quake => FilterQuakeInformation(filter, quake) is not null,
            TsunamiEvent => filter.Tsunami,
            WeatherWarningEvent weather => FilterWeatherInformation(filter, weather) is not null,
            VolcanoEvent => filter.Volcano,
            _ => false,
        };
    }

    private static WeatherWarningEvent? FilterWeatherInformation(
        FilterSettings filter,
        WeatherWarningEvent weather)
    {
        if (!filter.WeatherWarning || !IsWeatherInformationTypeEnabled(filter, weather))
        {
            return null;
        }

        WeatherWarningItem[] items = weather.Items
            .Where(item => IsWeatherItemEnabled(filter, weather, item))
            .ToArray();
        if (items.Length == 0)
        {
            if (!weather.IsCancelled || weather.Items.Count > 0 ||
                !MatchesSelectedPrefecture(filter, null, weather.Headline))
            {
                return null;
            }

            return weather;
        }

        if (ShouldHideContinuationOnly(filter, weather, items))
        {
            return null;
        }

        return items.Length == weather.Items.Count ? weather : weather.WithItems(items);
    }

    private static bool ShouldHideContinuationOnly(
        FilterSettings filter,
        WeatherWarningEvent weather,
        IReadOnlyCollection<WeatherWarningItem> visibleItems) =>
        filter.HideWeatherContinuationOnly &&
        !weather.IsCancelled &&
        weather.InformationType == WeatherInformationType.WarningAndAdvisory &&
        visibleItems.Count > 0 &&
        visibleItems.All(static item =>
            item.IsActive &&
            item.Status.Trim().Contains("継続", StringComparison.Ordinal));

    private static bool IsWeatherInformationTypeEnabled(
        FilterSettings filter,
        WeatherWarningEvent weather) => weather.InformationType switch
        {
            WeatherInformationType.RecordShortDurationHeavyRain =>
                filter.WeatherRecordShortRain,
            WeatherInformationType.DisasterPreventionBulletin =>
                filter.WeatherDisasterPreventionBulletins,
            WeatherInformationType.TornadoAdvisory =>
                filter.WeatherTornadoAdvisories,
            _ => true,
        };

    internal static bool IsWeatherItemEnabled(
        FilterSettings filter,
        WeatherWarningEvent weather,
        WeatherWarningItem item)
    {
        bool typeEnabled = weather.InformationType switch
        {
            WeatherInformationType.RecordShortDurationHeavyRain =>
                filter.WeatherRecordShortRain,
            WeatherInformationType.DisasterPreventionBulletin =>
                filter.WeatherDisasterPreventionBulletins,
            WeatherInformationType.TornadoAdvisory =>
                filter.WeatherTornadoAdvisories,
            _ => item.Level switch
            {
                WeatherWarningLevel.SpecialWarning => filter.WeatherSpecialWarnings,
                WeatherWarningLevel.Warning => filter.WeatherWarnings,
                WeatherWarningLevel.Advisory when
                    item.KindName.Contains("竜巻", StringComparison.Ordinal) =>
                        filter.WeatherTornadoAdvisories,
                WeatherWarningLevel.Advisory => filter.WeatherAdvisories,
                // Unknown active warning kinds must fail safe. Treat them as a
                // warning for filtering instead of silently discarding them.
                WeatherWarningLevel.Unknown => filter.WeatherWarnings,
                _ => false,
            },
        };
        return typeEnabled && MatchesSelectedPrefecture(filter, item, weather.Headline);
    }

    private static bool MatchesSelectedPrefecture(
        FilterSettings filter,
        WeatherWarningItem? item,
        string headline)
    {
        string[] selectedCodes = WeatherPrefectureCatalog.ResolveCodes(filter);
        if (selectedCodes.Length == 0)
        {
            return true;
        }

        foreach (string code in selectedCodes)
        {
            WeatherPrefectureOption? selected = WeatherPrefectureCatalog.Find(code);
            if (selected is null)
            {
                continue;
            }

            if (item is not null &&
                ((!string.IsNullOrWhiteSpace(item.AreaCode) &&
                  item.AreaCode.StartsWith(selected.Code, StringComparison.Ordinal)) ||
                 item.AreaName.Contains(selected.Name, StringComparison.Ordinal)))
            {
                return true;
            }

            if (item is null && headline.Contains(selected.Name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsQuakeIntensityEnabled(FilterSettings filter, QuakeEvent quake)
    {
        return !QuakeFilterOptions.ResolveExcludedScales(filter)
            .Contains(quake.Earthquake.MaximumScale);
    }

    private static QuakeEvent? FilterQuakeInformation(FilterSettings filter, QuakeEvent quake)
    {
        if (!filter.Quake)
        {
            return null;
        }

        // Cancellations must still clear previously displayed information.
        if (quake.IsCancelled)
        {
            return quake;
        }

        if (!IsQuakeIntensityEnabled(filter, quake))
        {
            return null;
        }

        string[] codes = WeatherPrefectureCatalog.NormalizeCodes(filter.QuakePrefectureCodes);
        if (codes.Length == 0)
        {
            return quake;
        }

        bool Matches(string? code) => code is not null && codes.Contains(code);
        QuakePoint[] points = quake.Points.Where(point => Matches(
            QuakeFilterOptions.FindPrefectureCode(point.Prefecture, point.MunicipalityCode))).ToArray();
        LongPeriodIntensityInfo? longPeriod = quake.LongPeriodIntensity;
        LongPeriodIntensityArea[] areas = longPeriod?.Areas.Where(area => Matches(
            QuakeFilterOptions.FindPrefectureCode(area.Prefecture))).ToArray() ?? [];
        bool hasKnownRegion = quake.Points.Any(point =>
            QuakeFilterOptions.FindPrefectureCode(point.Prefecture, point.MunicipalityCode) is not null) ||
            (longPeriod?.Areas.Any(area =>
                QuakeFilterOptions.FindPrefectureCode(area.Prefecture) is not null) ?? false);

        // Hypocenter-only, foreign and advisory telegrams cannot be classified
        // by observed prefecture. Keep their summaries instead of guessing.
        if (!hasKnownRegion)
        {
            return quake;
        }

        if (filter.QuakePrefectureMode != QuakePrefectureFilterMode.PointsOnly &&
            points.Length == 0 && areas.Length == 0)
        {
            return null;
        }

        return filter.QuakePrefectureMode == QuakePrefectureFilterMode.EventOnly
            ? quake
            : quake.WithDisplayObservations(points, longPeriod is null
                ? null
                : longPeriod with { Areas = areas });
    }

    public static string DescribeSuppression(
        FilterSettings filter,
        DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(disasterEvent);
        return disasterEvent switch
        {
            EewEvent when !filter.Eew => "種別フィルター:緊急地震速報",
            QuakeEvent when !filter.Quake => "種別フィルター:地震情報",
            QuakeEvent quake when !IsQuakeIntensityEnabled(filter, quake) => "最大震度フィルター",
            QuakeEvent => "地震の都道府県フィルター",
            TsunamiEvent when !filter.Tsunami => "種別フィルター:津波情報",
            WeatherWarningEvent weather => DescribeWeatherSuppression(filter, weather),
            VolcanoEvent when !filter.Volcano => "種別フィルター:火山情報",
            _ => "表示フィルター",
        };
    }

    private static string DescribeWeatherSuppression(
        FilterSettings filter,
        WeatherWarningEvent weather)
    {
        if (!filter.WeatherWarning)
        {
            return "種別フィルター:気象情報";
        }

        if (!IsWeatherInformationTypeEnabled(filter, weather))
        {
            return "気象情報区分フィルター";
        }

        string[] selectedCodes = WeatherPrefectureCatalog.ResolveCodes(filter);
        bool missesSelectedPrefecture = weather.Items.Count > 0
            ? weather.Items.All(item =>
                !MatchesSelectedPrefecture(filter, item, weather.Headline))
            : !MatchesSelectedPrefecture(filter, null, weather.Headline);
        if (selectedCodes.Length > 0 && missesSelectedPrefecture)
        {
            return "都道府県フィルター";
        }

        if (weather.Items.All(item => !IsWeatherItemEnabled(filter, weather, item)))
        {
            return "警戒レベルフィルター";
        }

        WeatherWarningItem[] visibleItems = weather.Items
            .Where(item => IsWeatherItemEnabled(filter, weather, item))
            .ToArray();
        if (ShouldHideContinuationOnly(filter, weather, visibleItems))
        {
            return "継続情報のみ";
        }

        return "気象情報フィルター";
    }
}
