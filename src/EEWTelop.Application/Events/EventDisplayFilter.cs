using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Events;

public static class EventDisplayFilter
{
    public static DisasterEvent? Apply(FilterSettings filter, DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(disasterEvent);
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
            QuakeEvent quake => filter.Quake && IsQuakeIntensityEnabled(filter, quake),
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

        return items.Length == weather.Items.Count ? weather : weather.WithItems(items);
    }

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

    private static bool IsWeatherItemEnabled(
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
        if (!filter.HideQuakeBelowIntensity3)
        {
            return true;
        }

        JmaScale maximum = quake.Earthquake.MaximumScale;
        return maximum == JmaScale.Unknown || (int)maximum >= (int)JmaScale.Three;
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
            QuakeEvent => "最大震度フィルター",
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

        return "気象情報フィルター";
    }
}
