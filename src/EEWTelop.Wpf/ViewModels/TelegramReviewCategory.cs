using System;
using System.Linq;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.ViewModels;

public enum TelegramReviewCategory
{
    All, Weather, Advisory, Warning, SpecialWarning, Quake, TsunamiForecast,
    TsunamiObservation, Volcano, Eew, Other,
}

public sealed record TelegramReviewCategoryOption(TelegramReviewCategory Value, string Label);

internal static class TelegramReviewCategoryFilter
{
    public static bool Matches(DisasterEvent item, TelegramReviewCategory category) => category switch
    {
        TelegramReviewCategory.All => true,
        TelegramReviewCategory.Weather => item is WeatherWarningEvent,
        TelegramReviewCategory.Advisory => HasLevel(item, WeatherWarningLevel.Advisory),
        TelegramReviewCategory.Warning => HasLevel(item, WeatherWarningLevel.Warning),
        TelegramReviewCategory.SpecialWarning => HasLevel(item, WeatherWarningLevel.SpecialWarning),
        TelegramReviewCategory.Quake => item is QuakeEvent,
        TelegramReviewCategory.TsunamiForecast => item is TsunamiEvent tsunami && !IsObservation(tsunami),
        TelegramReviewCategory.TsunamiObservation => item is TsunamiEvent tsunami && IsObservation(tsunami),
        TelegramReviewCategory.Volcano => item is VolcanoEvent,
        TelegramReviewCategory.Eew => item is EewEvent,
        _ => item is not (WeatherWarningEvent or QuakeEvent or TsunamiEvent or VolcanoEvent or EewEvent),
    };

    // Match all included levels, including released items, not just the maximum level.
    private static bool HasLevel(DisasterEvent item, WeatherWarningLevel level) =>
        item is WeatherWarningEvent weather && weather.Items.Any(i => i.Level == level);

    private static bool IsObservation(TsunamiEvent tsunami) =>
        tsunami.Issue.RawType.Trim().Equals("VTSE51", StringComparison.OrdinalIgnoreCase) ||
        tsunami.Issue.RawType.Trim().Equals("VTSE52", StringComparison.OrdinalIgnoreCase) ||
        tsunami.Areas.Any(a => a.Role is TsunamiInformationRole.CoastalObservation or TsunamiInformationRole.OffshoreObservation);
}
