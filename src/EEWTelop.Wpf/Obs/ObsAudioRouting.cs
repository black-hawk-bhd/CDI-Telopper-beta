using EEWTelop.Application.Audio;

namespace EEWTelop.Wpf.Obs;

internal static class ObsAudioRouting
{
    public static ObsViewChannel GetChannel(string cue) =>
        Enum.TryParse(cue, ignoreCase: false, out AudioCueId parsed) && Enum.IsDefined(parsed)
            ? GetChannel(parsed)
            : throw new ArgumentOutOfRangeException(nameof(cue), cue, "Unknown OBS audio cue.");

    public static ObsViewChannel GetChannel(AudioCueId cue) => cue switch
    {
        AudioCueId.QuakeIntensity3OrMore or
        AudioCueId.QuakeIntensity1 or AudioCueId.QuakeIntensity2 or
        AudioCueId.QuakeIntensity3 or AudioCueId.QuakeIntensity4 or
        AudioCueId.QuakeIntensity5Lower or AudioCueId.QuakeIntensity5Upper or
        AudioCueId.QuakeIntensity6Lower or AudioCueId.QuakeIntensity6Upper or
        AudioCueId.QuakeIntensity7 => ObsViewChannel.General,
        AudioCueId.EewInitial or AudioCueId.EewContinuation or
        AudioCueId.EewCancellation => ObsViewChannel.Eew,
        AudioCueId.Tsunami or AudioCueId.TsunamiForecast or
        AudioCueId.TsunamiAdvisory or AudioCueId.TsunamiWarning or
        AudioCueId.TsunamiMajorWarning => ObsViewChannel.Tsunami,
        AudioCueId.WeatherSpecialWarning or AudioCueId.WeatherWarning or
        AudioCueId.WeatherAdvisory or AudioCueId.WeatherDisasterPreventionBulletin => ObsViewChannel.Weather,
        _ => throw new ArgumentOutOfRangeException(nameof(cue), cue, "Unknown OBS audio cue."),
    };

    public static string GetRoute(ObsViewChannel channel) => channel switch
    {
        ObsViewChannel.General => "general",
        ObsViewChannel.Eew => "eew",
        ObsViewChannel.Tsunami => "tsunami",
        ObsViewChannel.Weather => "weather",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown OBS view channel."),
    };
}
