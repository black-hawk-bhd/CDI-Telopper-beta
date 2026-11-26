using System.Globalization;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Application.Events;
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
    VolcanoWarningForecast,
    VolcanoEruptionFlash,
}

public sealed record AudioDecision(
    bool ShouldPlay,
    AudioCueId? Cue,
    string FilePath,
    string Reason);

public interface IAudioPolicy
{
    AudioDecision Evaluate(DisasterEvent disasterEvent, AudioSettings settings);

    // Comparison runs reuse sample reports. Keep their playback history scoped to each run.
    IAudioPolicy CreateComparisonPolicy() => this;

    // Silent receptions still update the announcement baseline. Existing
    // alternative policies can keep the default no-op implementation.
    void Observe(DisasterEvent disasterEvent) { }
}

public sealed class AudioPolicy : IAudioPolicy
{
    private readonly object _gate = new();
    private readonly HashSet<string> _played = new(StringComparer.Ordinal);
    private readonly Dictionary<(SourceMode Mode, bool IsTest, EventId Id), EewAudioReport> _eewReports = [];
    private readonly Queue<(SourceMode Mode, bool IsTest, EventId Id)> _eewReportOrder = [];
    private readonly NewAnnouncementTracker _announcements = new();

    public IAudioPolicy CreateComparisonPolicy() => new AudioPolicy();

    public void Observe(DisasterEvent disasterEvent)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        lock (_gate)
        {
            if (disasterEvent is EewEvent eew)
                ObserveEew(eew);
            else
                _announcements.Observe(disasterEvent);
        }
    }

    public AudioDecision Evaluate(DisasterEvent disasterEvent, AudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            AudioCueId? eewCue = disasterEvent is EewEvent eew ? ObserveEew(eew) : null;
            AnnouncementChange change = _announcements.Take(disasterEvent);
            AudioAnnouncementMode mode = settings.GetAnnouncementMode(disasterEvent.Kind);
            if (settings.Muted)
            {
                return Skip("ミュート中です。");
            }

            bool rehearsal = disasterEvent.SourceMode != SourceMode.Production ||
                disasterEvent is EewEvent { IsTest: true };
            if (rehearsal && !settings.TestUsesProductionSound)
            {
                return Skip("訓練での設定音声が無効です。");
            }

            if (mode != AudioAnnouncementMode.Legacy &&
                (change.AreaKeys.Count == 0 || (mode == AudioAnnouncementMode.FirstAnnouncementOnly && !change.IsFirst)))
            {
                return Skip("選択した再生条件の対象になる新規発表や更新がありません。");
            }
            (AudioCueId Cue, string FilePath)? selected = mode == AudioAnnouncementMode.Legacy
                ? SelectCue(disasterEvent, settings, eewCue)
                : disasterEvent switch
                {
                    // Region additions hidden by the display filter must not
                    // sound. The nationwide maximum is retained by that filter.
                    QuakeEvent quake when !change.AreaKeys.Contains(NewAnnouncementTracker.QuakeMaximumKey) &&
                        !quake.Points.Any(point => change.AreaKeys.Contains(NewAnnouncementTracker.AreaKey(point))) => null,
                    WeatherWarningEvent announcedWeather => SelectWeatherCue(announcedWeather.WithItems(announcedWeather.Items
                        .Where(item => change.AreaKeys.Contains(NewAnnouncementTracker.AreaKey(item))).ToArray()), settings),
                    TsunamiEvent tsunami => SelectTsunamiCue(tsunami, settings, tsunami.Areas
                        .Where(area => area.Role == TsunamiInformationRole.ForecastArea &&
                            change.AreaKeys.Contains(NewAnnouncementTracker.AreaKey(area)))),
                    _ => SelectCue(disasterEvent, settings, eewCue),
                };
            if (selected is null)
            {
                return Skip("この電文は音声の対象外か、該当区分の音声が無効です。");
            }

            if (string.IsNullOrWhiteSpace(selected.Value.FilePath))
            {
                return Skip($"音声ファイルが選択されていません。区分={selected.Value.Cue}");
            }

            if (disasterEvent.SourceMode == SourceMode.ManualTest && mode == AudioAnnouncementMode.Legacy)
            {
                return new AudioDecision(
                    true,
                    selected.Value.Cue,
                    selected.Value.FilePath,
                    "Manual tests may replay the configured audio file.");
            }

            string baseKey = $"{disasterEvent.Id.Value}:{selected.Value.Cue}";
            string key = mode == AudioAnnouncementMode.Legacy
                ? BuildPlaybackKey(disasterEvent, baseKey)
                : $"{disasterEvent.SourceMode}:{disasterEvent.Kind}:{baseKey}:{disasterEvent.IssuedAt.UtcTicks}:{disasterEvent.Signature}";
            if (!_played.Add(key))
            {
                return Skip("同じ電文の音声は再生済みです。");
            }

            // A weather report that contains a newly issued or updated area is
            // report-scoped so a later bulletin for the same event can sound.
            // Remember the base cue as well, otherwise the first subsequent
            // continuation-only bulletin would sound again unexpectedly.
            if (disasterEvent is WeatherWarningEvent weather &&
                mode == AudioAnnouncementMode.Legacy && HasNewlyIssuedOrUpdatedArea(weather))
            {
                _played.Add(baseKey);
            }

            if (_played.Count > 1000)
            {
                _played.Clear();
                _played.Add(key);
                if (disasterEvent is WeatherWarningEvent weatherAfterReset &&
                    mode == AudioAnnouncementMode.Legacy && HasNewlyIssuedOrUpdatedArea(weatherAfterReset))
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

    private static (AudioCueId Cue, string FilePath)? SelectCue(
        DisasterEvent disasterEvent,
        AudioSettings settings,
        AudioCueId? eewCue) => disasterEvent switch
    {
        QuakeEvent quake => SelectQuakeCue(quake, settings),
        TsunamiEvent tsunami => SelectTsunamiCue(tsunami, settings),
        EewEvent => SelectEewCue(eewCue, settings),
        WeatherWarningEvent weather => SelectWeatherCue(weather, settings),
        VolcanoEvent { IsCancelled: false, IsTelegramCancellation: false, IsExpired: false } volcano =>
            SelectVolcanoCue(volcano.InformationType, settings),
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
        AudioSettings settings,
        IEnumerable<TsunamiArea>? eligibleAreas = null)
    {
        if (tsunami.IsCancelled)
        {
            return null;
        }

        TsunamiGrade highestGrade = (eligibleAreas ?? tsunami.Areas)
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

    private static (AudioCueId Cue, string FilePath)? SelectVolcanoCue(
        VolcanoInformationType type, AudioSettings settings) => type switch
    {
        VolcanoInformationType.WarningForecast when settings.VolcanoWarningForecastCue is { Enabled: true } warning =>
            (AudioCueId.VolcanoWarningForecast, warning.FilePath),
        VolcanoInformationType.EruptionFlash when settings.VolcanoEruptionFlashCue is { Enabled: true } flash =>
            (AudioCueId.VolcanoEruptionFlash, flash.FilePath),
        _ => null,
    };

    private static string BuildPlaybackKey(
        DisasterEvent disasterEvent,
        string baseKey)
    {
        if (disasterEvent is EewEvent eew)
        {
            return $"{eew.SourceMode}:{eew.IsTest}:{baseKey}:{GetReportIdentity(eew.Issue.Serial, eew.Signature)}";
        }

        if (disasterEvent is VolcanoEvent volcano)
        {
            return $"{volcano.SourceMode}:{baseKey}:{volcano.IssuedAt.UtcTicks}:{GetReportIdentity(volcano.Issue.Serial, volcano.Signature)}";
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

    private AudioCueId? ObserveEew(EewEvent eew)
    {
        if (eew.IsExpired || (!eew.IsWarning && !eew.IsCancelled)) return null;

        var key = (eew.SourceMode, eew.IsTest, eew.Id);
        _eewReports.TryGetValue(key, out EewAudioReport? previous);
        if (eew.SourceMessageTime < previous?.SourceMessageTime) return null;

        // Live reception observes each report before it evaluates audio.
        if (previous is not null && previous.SourceMessageTime == eew.SourceMessageTime &&
            previous.IssuedAt == eew.IssuedAt &&
            previous.Serial == eew.Issue.Serial && previous.Signature == eew.Signature)
            return previous.Cue;

        HashSet<string>? areaLabels = null;
        AudioCueId? cue = AudioCueId.EewCancellation;
        if (!eew.IsCancelled)
        {
            areaLabels = EewAreaLabelFormatter.Format(eew.Areas).ToHashSet(StringComparer.Ordinal);
            if (previous?.AreaLabels is null ||
                (eew.SourceMode == SourceMode.ManualTest &&
                 int.TryParse(eew.Issue.Serial, NumberStyles.Integer, CultureInfo.InvariantCulture, out int serial) &&
                 serial <= 1))
                cue = AudioCueId.EewInitial;
            else
                cue = areaLabels.SetEquals(previous.AreaLabels) ? null : AudioCueId.EewContinuation;
        }

        if (previous is null)
        {
            _eewReportOrder.Enqueue(key);
            if (_eewReportOrder.Count > EventVersionCache.DefaultKeyLimit)
                _eewReports.Remove(_eewReportOrder.Dequeue());
        }
        _eewReports[key] = new(eew.SourceMessageTime, eew.IssuedAt, eew.Issue.Serial, eew.Signature, areaLabels, cue);
        return cue;
    }

    private static (AudioCueId Cue, string FilePath)? SelectEewCue(AudioCueId? cue, AudioSettings settings) =>
        !settings.EewEnabled ? null : cue switch
        {
            AudioCueId.EewInitial when settings.EewInitialEnabled =>
                (AudioCueId.EewInitial, settings.EewInitialFilePath),
            AudioCueId.EewContinuation when settings.EewContinuationEnabled =>
                (AudioCueId.EewContinuation, settings.EewContinuationFilePath),
            AudioCueId.EewCancellation when settings.EewCancellationEnabled =>
                (AudioCueId.EewCancellation, settings.EewCancellationFilePath),
            _ => null,
        };

    private sealed record EewAudioReport(
        DateTimeOffset? SourceMessageTime, DateTimeOffset IssuedAt, string? Serial, string Signature,
        HashSet<string>? AreaLabels, AudioCueId? Cue);

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
