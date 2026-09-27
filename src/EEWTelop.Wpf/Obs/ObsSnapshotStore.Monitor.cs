using EEWTelop.Application.Configuration;
using EEWTelop.Application.Display;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

public sealed partial class ObsSnapshotStore
{
    private readonly List<MonitorTelegram> _monitorTelegrams = [];
    private long _monitorTelegramId;

    public void AddMonitorTelegram(DisasterEvent item, DisplayProgram program, DisplaySettings settings, string result)
    {
        lock (_gate)
        {
            _monitorTelegrams.RemoveAll(old => old.Event.Provider == item.Provider &&
                old.Event.SourceMode == item.SourceMode && old.Event.Id == item.Id && old.Event.IssuedAt == item.IssuedAt);
            _monitorTelegrams.Insert(0, new(++_monitorTelegramId, item, program, settings, result));
            if (_monitorTelegrams.Count > 500) _monitorTelegrams.RemoveAt(500);
        }
    }

    internal object ReadMonitorList()
    {
        lock (_gate) return _monitorTelegrams.Select(item => new
        {
            id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            kind = item.Event.Kind.ToString(), sourceMode = item.Event.SourceMode.ToString(),
            issuedAt = item.Event.IssuedAt, receivedAt = item.Event.ReceivedAt,
            provider = item.Event.Provider, pages = item.Program.Pages.Count, result = item.Result,
            summary = MonitorSummary(item.Program),
        }).ToArray();
    }

    internal ObsViewSnapshot[]? ReadMonitorTelegram(long id, DateTimeOffset now)
    {
        lock (_gate)
        {
            var item = _monitorTelegrams.Find(entry => entry.Id == id);
            if (item is null) return null;
            // Review does not re-publish a program, advance its clock, or request audio.
            return item.Program.Pages.Select((page, index) => CreateProgramSnapshot(item.Program, page,
                index, item.Settings, now, 0) with { SourceMode = item.Event.SourceMode }).ToArray();
        }
    }

    internal object ReadMonitorChannels(DateTimeOffset now) => new
    {
        general = Silence(Read(ObsViewChannel.General, now)),
        eew = Silence(Read(ObsViewChannel.Eew, now)),
        tsunami = Silence(Read(ObsViewChannel.Tsunami, now)),
        weather = Silence(Read(ObsViewChannel.Weather, now)),
    };

    private static ObsViewSnapshot Silence(ObsViewSnapshot value) => value with
        { AudioSequence = 0, AudioAction = string.Empty, AudioCue = string.Empty, AudioIssuedAtUtc = null };
    private static string MonitorSummary(DisplayProgram program)
    {
        var page = program.Pages.Count > 0 ? program.Pages[0] : null;
        string text = page?.AccessibleText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) && page is { Blocks.Count: > 0 })
        {
            var block = page.Blocks[0];
            text = $"{block.Badge} {block.WeatherHeading} {block.PrimaryText}";
        }
        return text.Length > 100 ? text[..100] + "…" : text;
    }
    private sealed record MonitorTelegram(long Id, DisasterEvent Event, DisplayProgram Program, DisplaySettings Settings, string Result);
}
