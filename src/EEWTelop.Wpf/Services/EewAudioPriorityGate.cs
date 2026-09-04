using EEWTelop.Application.Audio;
using EEWTelop.Wpf.Obs;

namespace EEWTelop.Wpf.Services;

/// <summary>
/// Prevents another disaster sound from replacing an EEW sound while the OBS
/// audio source is still starting or playing it.
/// </summary>
internal sealed class EewAudioPriorityGate
{
    private static readonly TimeSpan MaximumPlaybackGuard = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private long _generation;
    private long _pendingGeneration;

    public long BeginEew()
    {
        lock (_gate)
        {
            _pendingGeneration = ++_generation;
            return _pendingGeneration;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    public void CompleteEew(long generation)
    {
        lock (_gate)
        {
            if (_pendingGeneration == generation)
            {
                _pendingGeneration = 0;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _pendingGeneration = 0;
        }
    }

    public bool IsActive(
        ObsAudioDiagnostics diagnostics,
        DateTimeOffset now,
        bool hasConnectedAudioClient)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        lock (_gate)
        {
            if (_pendingGeneration != 0)
            {
                return true;
            }
        }

        if (!hasConnectedAudioClient ||
            !IsEewCue(diagnostics.Cue) ||
            diagnostics.PlaybackResult is not ("Queued" or "Started") ||
            diagnostics.ReportedAtUtc is not { } reportedAt)
        {
            return false;
        }

        TimeSpan elapsed = now - reportedAt;
        return elapsed >= TimeSpan.Zero && elapsed <= MaximumPlaybackGuard;
    }

    public static bool IsEewCue(AudioCueId cue) => cue is
        AudioCueId.EewInitial or
        AudioCueId.EewContinuation or
        AudioCueId.EewCancellation;

    private static bool IsEewCue(string cue) =>
        Enum.TryParse(cue, ignoreCase: false, out AudioCueId parsed) &&
        IsEewCue(parsed);
}
