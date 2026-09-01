using System.Text.Json.Serialization;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

public sealed record ObsViewSnapshot(
    int SchemaVersion,
    long Sequence,
    DateTimeOffset GeneratedAtUtc,
    int Width,
    int Height,
    BackgroundMode BackgroundMode,
    double FontScale,
    double LetterSpacingEm,
    double LineSpacing,
    bool HasProgram,
    string ProgramId,
    EventKind? Kind,
    SourceMode? SourceMode,
    string RehearsalLabel,
    int PageIndex,
    int PageCount,
    string PageIndicator,
    string AccessibleText,
    IReadOnlyList<ObsViewBlock> Blocks,
    long AudioSequence,
    string AudioAction,
    string AudioCue,
    DateTimeOffset? AudioIssuedAtUtc)
{
    public OutputTransformSettings OutputTransform { get; init; } =
        OutputTransformSettings.Default;
}

public sealed record ObsViewBlock(
    string Badge,
    string PrimaryText,
    string SecondaryText,
    string StyleToken);

public enum ObsViewChannel
{
    General = 0,
    Eew,
    Tsunami,
    Weather,
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ObsViewSnapshot))]
[JsonSerializable(typeof(OutputTransformSettings))]
internal sealed partial class ObsJsonContext : JsonSerializerContext;

public sealed class ObsSnapshotStore
{
    internal static readonly TimeSpan AudioRetention = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly SortedDictionary<long, ObsAudioPayload> _audioFiles = [];
    private readonly Dictionary<ObsViewChannel, ObsProgramState> _programs = [];
    private readonly PageClock _pageClock = new();
    private long _sequence;
    private long _audioSequence;
    private ObsAudioDiagnostics _audioDiagnostics = ObsAudioDiagnostics.Empty;
    private ObsViewSnapshot _snapshot;

    public ObsSnapshotStore(DisplaySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _snapshot = CreateEmpty(settings, now, sequence: 0);
    }

    public ObsViewSnapshot Read()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public ObsViewSnapshot Read(ObsViewChannel channel, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_programs.TryGetValue(channel, out ObsProgramState? state))
            {
                return WithChannelAudio(CreateEmpty(_snapshot, now, ++_sequence), channel);
            }

            TimeSpan elapsed = now - state.StartedAtUtc;
            PageClockResult result = _pageClock.Evaluate(
                state.Program,
                state.Settings,
                state.StartedAtUtc,
                elapsed);
            if (result.IsExpired || result.Page is null)
            {
                _programs.Remove(channel);
                return WithChannelAudio(CreateEmpty(state.Settings, now, ++_sequence), channel);
            }

            return WithChannelAudio(CreateProgramSnapshot(
                state.Program,
                result.Page,
                result.Index,
                state.Settings,
                now,
                ++_sequence), channel);
        }
    }

    public void PublishProgram(
        DisasterEvent disasterEvent,
        DisplayProgram program,
        DisplaySettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(disasterEvent);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(settings);
        ObsViewChannel? channel = ToViewChannel(disasterEvent.Kind);
        if (channel is null)
        {
            return;
        }

        lock (_gate)
        {
            _programs[channel.Value] = new ObsProgramState(program, settings, now);
        }
    }

    public void ClearPrograms(DateTimeOffset now)
    {
        lock (_gate)
        {
            _programs.Clear();
            _snapshot = CreateEmpty(_snapshot, now, ++_sequence);
        }
    }

    public void ClearProgram(EventKind kind, DateTimeOffset now)
    {
        lock (_gate)
        {
            ObsViewChannel? channel = ToViewChannel(kind);
            if (channel is not null &&
                _programs.TryGetValue(channel.Value, out ObsProgramState? state) &&
                state.Program.Kind == kind)
            {
                _programs.Remove(channel.Value);
            }

            if (_snapshot.Kind == kind)
            {
                ObsViewSnapshot empty = CreateEmpty(_snapshot, now, ++_sequence);
                _snapshot = empty with
                {
                    AudioSequence = _snapshot.AudioSequence,
                    AudioAction = _snapshot.AudioAction,
                    AudioCue = _snapshot.AudioCue,
                    AudioIssuedAtUtc = _snapshot.AudioIssuedAtUtc,
                };
            }
        }
    }

    public ObsViewSnapshot Publish(
        CoordinatorSnapshot snapshot,
        DisplaySettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            long sequence = ++_sequence;
            if (snapshot.CurrentProgram is null || snapshot.CurrentPage is null)
            {
                ObsViewSnapshot empty = CreateEmpty(settings, now, sequence);
                _snapshot = empty with
                {
                    AudioSequence = _snapshot.AudioSequence,
                    AudioAction = _snapshot.AudioAction,
                    AudioCue = _snapshot.AudioCue,
                    AudioIssuedAtUtc = _snapshot.AudioIssuedAtUtc,
                };
                return _snapshot;
            }

            DisplayProgram program = snapshot.CurrentProgram;
            DisplayPage page = snapshot.CurrentPage;
            ObsViewChannel? channel = ToViewChannel(program.Kind);
            if (channel is not null)
            {
                _programs[channel.Value] = new ObsProgramState(
                    program,
                    settings,
                    snapshot.ProgramStartedAtUtc ?? now);
            }

            _snapshot = new ObsViewSnapshot(
                SchemaVersion: 1,
                Sequence: sequence,
                GeneratedAtUtc: now,
                Width: settings.Width,
                Height: settings.Height,
                BackgroundMode: settings.BackgroundMode,
                FontScale: settings.FontScale,
                LetterSpacingEm: settings.LetterSpacingEm,
                LineSpacing: settings.LineSpacing,
                HasProgram: true,
                ProgramId: program.ProgramId,
                Kind: program.Kind,
                SourceMode: program.SourceMode,
                RehearsalLabel: GetRehearsalLabel(program),
                PageIndex: snapshot.CurrentPageIndex,
                PageCount: program.Pages.Count,
                PageIndicator: settings.ShowPageIndicator && program.Pages.Count > 1
                    ? $"{snapshot.CurrentPageIndex + 1} / {program.Pages.Count}"
                    : string.Empty,
                AccessibleText: page.AccessibleText,
                Blocks: page.Blocks
                    .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                    .Select(static block => new ObsViewBlock(
                        block.Badge,
                        block.PrimaryText,
                        block.SecondaryText,
                        block.StyleToken))
                    .ToArray(),
                AudioSequence: _snapshot.AudioSequence,
                AudioAction: _snapshot.AudioAction,
                AudioCue: _snapshot.AudioCue,
                AudioIssuedAtUtc: _snapshot.AudioIssuedAtUtc)
            {
                OutputTransform = settings.OutputTransform,
            };
            return _snapshot;
        }
    }

    public ObsViewSnapshot PublishAudio(
        string cue,
        string filePath,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cue);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The OBS audio file does not exist.", fullPath);
        }

        lock (_gate)
        {
            RemoveExpiredAudio(now);
            long audioSequence = ++_audioSequence;
            _audioFiles[audioSequence] = new ObsAudioPayload(
                fullPath,
                GetAudioContentType(fullPath),
                now + AudioRetention);
            _audioDiagnostics = new ObsAudioDiagnostics(
                cue,
                "Queued",
                now,
                audioSequence);
            _snapshot = _snapshot with
            {
                Sequence = ++_sequence,
                GeneratedAtUtc = now,
                AudioSequence = audioSequence,
                AudioAction = "play",
                AudioCue = cue,
                AudioIssuedAtUtc = now,
            };
            return _snapshot;
        }
    }

    public ObsViewSnapshot PublishAudioStop(DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpiredAudio(now);
            _audioDiagnostics = _audioDiagnostics with
            {
                PlaybackResult = "Stopped",
                ReportedAtUtc = now,
            };
            _snapshot = _snapshot with
            {
                Sequence = ++_sequence,
                GeneratedAtUtc = now,
                AudioSequence = ++_audioSequence,
                AudioAction = "stop",
                AudioCue = string.Empty,
                AudioIssuedAtUtc = now,
            };
            return _snapshot;
        }
    }

    internal bool TryReadAudio(
        long sequence,
        DateTimeOffset now,
        out ObsAudioPayload? payload)
    {
        lock (_gate)
        {
            RemoveExpiredAudio(now);
            return _audioFiles.TryGetValue(sequence, out payload);
        }
    }

    internal bool TryReportAudioPlayback(
        long sequence,
        string result,
        DateTimeOffset now,
        out ObsAudioDiagnostics diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        lock (_gate)
        {
            RemoveExpiredAudio(now);
            if (sequence != _audioDiagnostics.Sequence ||
                sequence != _snapshot.AudioSequence ||
                !string.Equals(_snapshot.AudioAction, "play", StringComparison.Ordinal))
            {
                diagnostics = _audioDiagnostics;
                return false;
            }

            _audioDiagnostics = _audioDiagnostics with
            {
                PlaybackResult = result,
                ReportedAtUtc = now,
            };
            diagnostics = _audioDiagnostics;
            return true;
        }
    }

    public ObsAudioDiagnostics ReadAudioDiagnostics()
    {
        lock (_gate)
        {
            return _audioDiagnostics;
        }
    }

    public ObsViewSnapshot PublishSettings(DisplaySettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            foreach (ObsViewChannel channel in _programs.Keys.ToArray())
            {
                _programs[channel] = _programs[channel] with { Settings = settings };
            }

            _snapshot = _snapshot with
            {
                Sequence = ++_sequence,
                GeneratedAtUtc = now,
                Width = settings.Width,
                Height = settings.Height,
                BackgroundMode = settings.BackgroundMode,
                FontScale = settings.FontScale,
                LetterSpacingEm = settings.LetterSpacingEm,
                LineSpacing = settings.LineSpacing,
                OutputTransform = settings.OutputTransform,
            };
            return _snapshot;
        }
    }

    private static ObsViewSnapshot CreateEmpty(
        DisplaySettings settings,
        DateTimeOffset now,
        long sequence) => new(
            SchemaVersion: 1,
            Sequence: sequence,
            GeneratedAtUtc: now,
            Width: settings.Width,
            Height: settings.Height,
            BackgroundMode: settings.BackgroundMode,
            FontScale: settings.FontScale,
            LetterSpacingEm: settings.LetterSpacingEm,
            LineSpacing: settings.LineSpacing,
            HasProgram: false,
            ProgramId: string.Empty,
            Kind: null,
            SourceMode: null,
            RehearsalLabel: string.Empty,
            PageIndex: -1,
            PageCount: 0,
            PageIndicator: string.Empty,
            AccessibleText: string.Empty,
            Blocks: [],
            AudioSequence: 0,
            AudioAction: string.Empty,
            AudioCue: string.Empty,
            AudioIssuedAtUtc: null)
        {
            OutputTransform = settings.OutputTransform,
        };

    private static ObsViewSnapshot CreateEmpty(
        ObsViewSnapshot existing,
        DateTimeOffset now,
        long sequence) => new(
            1, sequence, now, existing.Width, existing.Height, existing.BackgroundMode,
            existing.FontScale, existing.LetterSpacingEm, existing.LineSpacing,
            false, string.Empty, null, null, string.Empty, -1, 0, string.Empty,
            string.Empty, [], existing.AudioSequence, existing.AudioAction,
            existing.AudioCue, existing.AudioIssuedAtUtc)
        {
            OutputTransform = existing.OutputTransform,
        };

    private static ObsViewSnapshot CreateProgramSnapshot(
        DisplayProgram program,
        DisplayPage page,
        int pageIndex,
        DisplaySettings settings,
        DateTimeOffset now,
        long sequence) => new(
            SchemaVersion: 2,
            Sequence: sequence,
            GeneratedAtUtc: now,
            Width: settings.Width,
            Height: settings.Height,
            BackgroundMode: settings.BackgroundMode,
            FontScale: settings.FontScale,
            LetterSpacingEm: settings.LetterSpacingEm,
            LineSpacing: settings.LineSpacing,
            HasProgram: true,
            ProgramId: program.ProgramId,
            Kind: program.Kind,
            SourceMode: program.SourceMode,
            RehearsalLabel: GetRehearsalLabel(program),
            PageIndex: pageIndex,
            PageCount: program.Pages.Count,
            PageIndicator: settings.ShowPageIndicator && program.Pages.Count > 1
                ? $"{pageIndex + 1} / {program.Pages.Count}"
                : string.Empty,
            AccessibleText: page.AccessibleText,
            Blocks: page.Blocks
                .Where(static block => block.StyleToken != DisplayStyleTokens.PageIndicator)
                .Select(static block => new ObsViewBlock(
                    block.Badge,
                    block.PrimaryText,
                    block.SecondaryText,
                    block.StyleToken))
                .ToArray(),
            AudioSequence: 0,
            AudioAction: string.Empty,
            AudioCue: string.Empty,
            AudioIssuedAtUtc: null)
        {
            OutputTransform = settings.OutputTransform,
        };

    private ObsViewSnapshot WithChannelAudio(ObsViewSnapshot snapshot, ObsViewChannel channel) =>
        channel == ObsViewChannel.General
            ? snapshot with
            {
                AudioSequence = _snapshot.AudioSequence,
                AudioAction = _snapshot.AudioAction,
                AudioCue = _snapshot.AudioCue,
                AudioIssuedAtUtc = _snapshot.AudioIssuedAtUtc,
            }
            : snapshot with
            {
                AudioSequence = 0,
                AudioAction = string.Empty,
                AudioCue = string.Empty,
                AudioIssuedAtUtc = null,
            };

    private static ObsViewChannel? ToViewChannel(EventKind kind) => kind switch
    {
        EventKind.Quake => ObsViewChannel.General,
        EventKind.Eew => ObsViewChannel.Eew,
        EventKind.Tsunami => ObsViewChannel.Tsunami,
        EventKind.WeatherWarning => ObsViewChannel.Weather,
        EventKind.Volcano => ObsViewChannel.General,
        _ => null,
    };

    private static string GetAudioContentType(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            _ => throw new NotSupportedException(
                "OBS audio supports WAV, MP3, and OGG files only."),
        };

    private void RemoveExpiredAudio(DateTimeOffset now)
    {
        foreach (long sequence in _audioFiles
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _audioFiles.Remove(sequence);
        }
    }

    private static string GetRehearsalLabel(DisplayProgram program)
    {
        if (!string.IsNullOrWhiteSpace(program.RehearsalLabel))
        {
            return program.RehearsalLabel;
        }

        return program.SourceMode switch
        {
            SourceMode.ManualTest => "操作テスト／訓練",
            SourceMode.HistoryRehearsal => "履歴リハーサル／訓練",
            SourceMode.Sandbox => "サンドボックス／訓練",
            _ => string.Empty,
        };
    }
}

internal sealed record ObsProgramState(
    DisplayProgram Program,
    DisplaySettings Settings,
    DateTimeOffset StartedAtUtc);

public sealed record ObsAudioDiagnostics(
    string Cue,
    string PlaybackResult,
    DateTimeOffset? ReportedAtUtc,
    long Sequence)
{
    public static ObsAudioDiagnostics Empty { get; } = new(
        string.Empty,
        "None",
        null,
        0);
}

internal sealed record ObsAudioPayload(
    string FilePath,
    string ContentType,
    DateTimeOffset ExpiresAtUtc);
