using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Application.Audio;

public enum AudioCueId
{
    QuakeIntensity3OrMore = 0,
    Tsunami,
    EewInitial,
    EewContinuation,
    EewCancellation,
    WeatherSpecialWarning,
    WeatherWarning,
    WeatherAdvisory,
    TsunamiAdvisory,
    TsunamiWarning,
    TsunamiMajorWarning,
    QuakeIntensity1,
    QuakeIntensity2,
    QuakeIntensity3,
    QuakeIntensity4,
    QuakeIntensity5Lower,
    QuakeIntensity5Upper,
    QuakeIntensity6Lower,
    QuakeIntensity6Upper,
    QuakeIntensity7,
    TsunamiForecast,
    WeatherDisasterPreventionBulletin,
}

public sealed record AudioDecision(
    bool ShouldPlay,
    AudioCueId? Cue,
    string FilePath,
    string Reason);

public interface IAudioPolicy
{
    AudioDecision Evaluate(DisasterEvent disasterEvent, AudioSettings settings);
}

public sealed class AudioPolicy : IAudioPolicy
{
    private readonly object _gate = new();
    private readonly HashSet<string> _played = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenEewEvents = new(StringComparer.Ordinal);

    public AudioDecision Evaluate(DisasterEvent disasterEvent, AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Muted)
        {
            return Skip("Audio is muted.");
        }

        bool rehearsal = disasterEvent.SourceMode != SourceMode.Production ||
            disasterEvent is EewEvent { IsTest: true };
        if (rehearsal && !settings.TestUsesProductionSound)
        {
            return Skip("Audio is disabled for training and rehearsal events.");
        }

        lock (_gate)
        {
            (AudioCueId Cue, string FilePath)? selected = SelectCue(disasterEvent, settings);
            if (selected is null || string.IsNullOrWhiteSpace(selected.Value.FilePath))
            {
                return Skip("The event or configured audio category is silent.");
            }

            if (disasterEvent.SourceMode == SourceMode.ManualTest)
            {
                return new AudioDecision(
                    true,
                    selected.Value.Cue,
                    selected.Value.FilePath,
                    "Manual tests may replay the configured audio file.");
            }

            string baseKey = $"{disasterEvent.Id.Value}:{selected.Value.Cue}";
            string key = BuildPlaybackKey(disasterEvent, selected.Value.Cue, baseKey);
            if (!_played.Add(key))
            {
                return Skip("This alert cue already played for the event report.");
            }

            // A weather report that contains a newly issued or updated area is
            // report-scoped so a later bulletin for the same event can sound.
            // Remember the base cue as well, otherwise the first subsequent
            // continuation-only bulletin would sound again unexpectedly.
            if (disasterEvent is WeatherWarningEvent weather &&
                HasNewlyIssuedOrUpdatedArea(weather))
            {
                _played.Add(baseKey);
            }

            if (_played.Count > 1000)
            {
                _played.Clear();
                _played.Add(key);
                if (disasterEvent is WeatherWarningEvent weatherAfterReset &&
                    HasNewlyIssuedOrUpdatedArea(weatherAfterReset))
                {
                    _played.Add(baseKey);
                }
            }

            return new AudioDecision(
                true,
                selected.Value.Cue,
                selected.Value.FilePath,
                "The configured audio file is eligible.");
        }
    }

    private (AudioCueId Cue, string FilePath)? SelectCue(
        DisasterEvent disasterEvent,
        AudioSettings settings) => disasterEvent switch
    {
        QuakeEvent quake => SelectQuakeCue(quake, settings),
        TsunamiEvent tsunami => SelectTsunamiCue(tsunami, settings),
        EewEvent eew => SelectEewCue(eew, settings),
        WeatherWarningEvent weather => SelectWeatherCue(weather, settings),
        _ => null,
    };

    private static (AudioCueId Cue, string FilePath)? SelectQuakeCue(
        QuakeEvent quake,
        AudioSettings settings)
    {
        JmaScale maximum = GetMaximumScale(quake);
        Dictionary<JmaScale, AudioCueSetting>? quakeCues = settings.QuakeScaleCues;
        if (quakeCues is { Count: > 0 })
        {
            JmaScale key = maximum == JmaScale.FiveLowerOrMore
                ? JmaScale.FiveLower
                : maximum;
            return quakeCues.TryGetValue(key, out AudioCueSetting? cue) &&
                cue is { Enabled: true }
                    ? (GetQuakeCueId(key), cue.FilePath)
                    : null;
        }

        return settings.QuakeEnabled && HasIntensityAtLeast(quake, settings.MinimumQuakeScale)
            ? (AudioCueId.QuakeIntensity3OrMore, settings.QuakeFilePath)
            : null;
    }

    private static (AudioCueId Cue, string FilePath)? SelectTsunamiCue(
        TsunamiEvent tsunami,
        AudioSettings settings)
    {
        if (tsunami.IsCancelled)
        {
            return null;
        }

        TsunamiGrade highestGrade = tsunami.Areas
            .Select(static area => area.Grade)
            .OrderByDescending(static grade => GetTsunamiSeverity(grade))
            .FirstOrDefault();
        Dictionary<TsunamiGrade, AudioCueSetting>? tsunamiCues = settings.TsunamiGradeCues;
        if (tsunamiCues is { Count: > 0 } &&
            tsunamiCues.TryGetValue(highestGrade, out AudioCueSetting? configured) &&
            configured is not null)
        {
            return configured.Enabled
                ? (GetTsunamiCueId(highestGrade), configured.FilePath)
                : null;
        }

        return highestGrade switch
        {
            TsunamiGrade.MajorWarning when settings.TsunamiMajorWarningEnabled =>
                (AudioCueId.TsunamiMajorWarning,
                    settings.TsunamiMajorWarningFilePath),
            TsunamiGrade.Warning when settings.TsunamiWarningEnabled =>
                (AudioCueId.TsunamiWarning, settings.TsunamiWarningFilePath),
            TsunamiGrade.Watch when settings.TsunamiAdvisoryEnabled =>
                (AudioCueId.TsunamiAdvisory, settings.TsunamiAdvisoryFilePath),
            TsunamiGrade.Forecast when settings.TsunamiEnabled =>
                (AudioCueId.TsunamiForecast, settings.TsunamiFilePath),
            _ => null,
        };
    }

    private static (AudioCueId Cue, string FilePath)? SelectWeatherCue(
        WeatherWarningEvent weather,
        AudioSettings settings)
    {
        if (weather.IsCancelled)
        {
            return null;
        }

        if (weather.InformationType == WeatherInformationType.DisasterPreventionBulletin)
        {
            return settings.WeatherDisasterPreventionBulletinEnabled
                ? (AudioCueId.WeatherDisasterPreventionBulletin,
                    settings.WeatherDisasterPreventionBulletinFilePath)
                : null;
        }

        return weather.MaximumLevel switch
        {
            WeatherWarningLevel.SpecialWarning when
                settings.WeatherSpecialWarningEnabled =>
                (AudioCueId.WeatherSpecialWarning,
                    settings.WeatherSpecialWarningFilePath),
            WeatherWarningLevel.Warning when settings.WeatherWarningEnabled =>
                (AudioCueId.WeatherWarning, settings.WeatherWarningFilePath),
            WeatherWarningLevel.Advisory when settings.WeatherAdvisoryEnabled =>
                (AudioCueId.WeatherAdvisory, settings.WeatherAdvisoryFilePath),
            _ => null,
        };
    }

    private static string BuildPlaybackKey(
        DisasterEvent disasterEvent,
        AudioCueId cue,
        string baseKey)
    {
        if (disasterEvent is EewEvent eew &&
            cue is AudioCueId.EewContinuation or AudioCueId.EewCancellation)
        {
            return $"{baseKey}:{GetReportIdentity(eew.Issue.Serial, eew.Signature)}";
        }

        if (disasterEvent is WeatherWarningEvent weather &&
            HasNewlyIssuedOrUpdatedArea(weather))
        {
            return $"{baseKey}:{GetReportIdentity(weather.Issue.Serial, weather.Signature)}";
        }

        return baseKey;
    }

    private static string GetReportIdentity(string? serial, string signature) =>
        string.IsNullOrWhiteSpace(serial)
            ? signature
            : $"{serial.Trim()}:{signature}";

    private static bool HasNewlyIssuedOrUpdatedArea(WeatherWarningEvent weather) =>
        weather.Items.Any(static item =>
            item.IsActive && IsNewlyIssuedOrUpdatedStatus(item.Status));

    private static bool IsNewlyIssuedOrUpdatedStatus(string status)
    {
        string value = status.Trim();
        return value.Contains("発表", StringComparison.Ordinal) ||
            value.Contains("更新", StringComparison.Ordinal) ||
            value.Contains("切替", StringComparison.Ordinal) ||
            value.Contains("移行", StringComparison.Ordinal);
    }

    private (AudioCueId Cue, string FilePath)? SelectEewCue(
        EewEvent eew,
        AudioSettings settings)
    {
        if (!settings.EewEnabled)
        {
            return null;
        }

        if (eew.IsCancelled)
        {
            return settings.EewCancellationEnabled
                ? (AudioCueId.EewCancellation, settings.EewCancellationFilePath)
                : null;
        }

        if (IsInitialReport(eew))
        {
            return settings.EewInitialEnabled
                ? (AudioCueId.EewInitial, settings.EewInitialFilePath)
                : null;
        }

        return settings.EewContinuationEnabled
            ? (AudioCueId.EewContinuation, settings.EewContinuationFilePath)
            : null;
    }

    private bool IsInitialReport(EewEvent eew)
    {
        bool firstSeen = _seenEewEvents.Add(eew.Id.Value);
        return int.TryParse(
            eew.Issue.Serial,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int serial)
                ? serial <= 1
                : firstSeen;
    }

    private static bool HasIntensityAtLeast(QuakeEvent quake, JmaScale minimumScale)
    {
        JmaScale threshold = IsSupportedQuakeThreshold(minimumScale)
            ? minimumScale
            : JmaScale.Three;
        int maximum = (int)quake.Earthquake.MaximumScale;
        if (quake.Points.Count > 0)
        {
            maximum = Math.Max(maximum, quake.Points.Max(static point => (int)point.Scale));
        }

        return maximum >= (int)threshold;
    }

    private static JmaScale GetMaximumScale(QuakeEvent quake)
    {
        JmaScale maximum = quake.Earthquake.MaximumScale;
        if (quake.Points.Count > 0)
        {
            JmaScale pointMaximum = quake.Points.Max(static point => point.Scale);
            if ((int)pointMaximum > (int)maximum)
            {
                maximum = pointMaximum;
            }
        }

        return maximum;
    }

    private static AudioCueId GetQuakeCueId(JmaScale scale) => scale switch
    {
        JmaScale.One => AudioCueId.QuakeIntensity1,
        JmaScale.Two => AudioCueId.QuakeIntensity2,
        JmaScale.Three => AudioCueId.QuakeIntensity3,
        JmaScale.Four => AudioCueId.QuakeIntensity4,
        JmaScale.FiveLower => AudioCueId.QuakeIntensity5Lower,
        JmaScale.FiveUpper => AudioCueId.QuakeIntensity5Upper,
        JmaScale.SixLower => AudioCueId.QuakeIntensity6Lower,
        JmaScale.SixUpper => AudioCueId.QuakeIntensity6Upper,
        JmaScale.Seven => AudioCueId.QuakeIntensity7,
        _ => AudioCueId.QuakeIntensity3OrMore,
    };

    private static AudioCueId GetTsunamiCueId(TsunamiGrade grade) => grade switch
    {
        TsunamiGrade.Forecast => AudioCueId.TsunamiForecast,
        TsunamiGrade.Watch => AudioCueId.TsunamiAdvisory,
        TsunamiGrade.Warning => AudioCueId.TsunamiWarning,
        TsunamiGrade.MajorWarning => AudioCueId.TsunamiMajorWarning,
        _ => AudioCueId.Tsunami,
    };

    private static bool IsSupportedQuakeThreshold(JmaScale scale) => scale is
        JmaScale.Three or
        JmaScale.Four or
        JmaScale.FiveLower or
        JmaScale.FiveUpper or
        JmaScale.SixLower or
        JmaScale.SixUpper or
        JmaScale.Seven;

    private static int GetTsunamiSeverity(TsunamiGrade grade) => grade switch
    {
        TsunamiGrade.Watch => 1,
        TsunamiGrade.Warning => 2,
        TsunamiGrade.MajorWarning => 3,
        _ => 0,
    };

    private static AudioDecision Skip(string reason) => new(false, null, string.Empty, reason);
}
